using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ReScene.App.Core.Helpers;
using ReScene.App.Core.Models;
using ReScene.App.Core.Services;
using ReScene.App.Core.ViewModels.Reconstruction;
using ReScene.Core;
using ReScene.Core.Diagnostics;
using ReScene.RAR;
using ReScene.SRR;
namespace ReScene.App.Core.ViewModels;

public partial class ReconstructorViewModel : ViewModelBase, IRunSink
{
    private const long DefaultVolumeSizeKb = 15000;

    private readonly IBruteForceService _bruteForceService;
    private readonly IFileDialogService _fileDialog;
    private readonly IAppSettingsService? _settingsService;

    private readonly IUiDispatcher _uiDispatcher;
    private readonly ITempDirectoryService _tempDir;
    private readonly ILauncherService _launcher;
    private readonly IFileMover _fileMover;
    private CancellationTokenSource? _cts;

    // Temp directory holding the SFV extracted from the last imported SRR (VerificationPath points
    // into it, so it must outlive the import). Replaced on the next import and deleted on Cleanup.
    private string? _sfvTempDir;

    // The verification file, parsed once at Start before any destructive cleanup. The sole source
    // for every downstream verification read for the run; null before the first Start (#14).
    private VerificationSnapshot? _verificationSnapshot;

    // Elapsed timer — ticks every second so the clock doesn't freeze between progress events
    private readonly IUiTimer _elapsedTimer;

    // Per-run progress bookkeeping (timing + version table + copy/verify timing).
    private readonly ReconstructionProgressTracker<VersionEntry> _progress;

    // ── Imported SRR state ──
    // All reconstruction state captured from an imported SRR lives in one holder so the options
    // builder and config capture/restore can pass it around as a unit.
    private ReconstructionImportState _import = new();

    // Timestamp-preservation failures accumulated during the current run.
    // Surfaced as a single MessageBox from the run's finally so the user is aware that the resulting
    // RAR's File Time (DOS) may not match the original for those files. Written from engine-callback
    // threads and read at summary time, so every access is guarded by _timestampFailuresLock and the
    // summary reads a snapshot taken under that lock (#19).
    private readonly List<TimestampPreservationFailedEventArgs> _timestampFailures = [];
    private readonly Lock _timestampFailuresLock = new();

    // ── Generation-safe batched log (#20) ──
    // Owned by ReconstructionLogBuffer, which holds the queue, the generation token and the flush
    // flag along with the three orderings between them.
    private readonly ReconstructionLogBuffer _log;
    private readonly ReconstructorStartValidator _startValidator;
    private readonly VersionTreeCoordinator _versions;
    private readonly ReconstructionRunner _runner;

    // The active set/attempt label prepended to progress messages (#24), so a seed→full progress reset
    // within one set reads as a labelled stage change rather than an unexplained rewind. Volatile: it is
    // written on the run's await continuation and read on the engine's progress-callback thread.
    private volatile SetStageLabel? _setStageLabel;

    public ReconstructorViewModel(IBruteForceService bruteForceService, IFileDialogService fileDialog, IUiDispatcher uiDispatcher, IUiTimerFactory timerFactory, IAppSettingsService? settingsService = null, ITempDirectoryService? tempDir = null, ILauncherService? launcher = null)
        : this(bruteForceService, fileDialog, uiDispatcher, timerFactory, settingsService, tempDir, launcher, fileMover: null)
    {
    }

    /// <summary>Test-facing overload that injects the <see cref="IFileMover"/> relocation seam (default <see cref="SystemFileMover"/>).</summary>
    internal ReconstructorViewModel(IBruteForceService bruteForceService, IFileDialogService fileDialog, IUiDispatcher uiDispatcher, IUiTimerFactory timerFactory, IAppSettingsService? settingsService, ITempDirectoryService? tempDir, ILauncherService? launcher, IFileMover? fileMover)
    {
        ArgumentNullException.ThrowIfNull(timerFactory);

        _bruteForceService = bruteForceService;
        _fileDialog = fileDialog;
        _settingsService = settingsService;
        _uiDispatcher = uiDispatcher;
        _tempDir = tempDir ?? new TempDirectoryService();
        _launcher = launcher ?? new SystemLauncherService();
        _fileMover = fileMover ?? new SystemFileMover();
        // Constructed here rather than as a field initializer: it needs the injected dispatcher.
        // LogEntries is a field-initialized collection, so it already exists.
        _log = new ReconstructionLogBuffer(uiDispatcher, LogEntries);
        _startValidator = new ReconstructorStartValidator(
            fileDialog,
            message => Log(LogTarget.System, message),
            EvaluateRunPreflight,
            SubdirTimestampWarningText);
        _versions = new VersionTreeCoordinator(
            uiDispatcher,
            VersionGroups,
            () => WinRARPath,
            () => HasScannedVersions,
            v => HasScannedVersions = v,
            v => ShowNoVersionsHint = v,
            EnabledMajors,
            SyncMajorsFromTree);


        _bruteForceService.Progress += OnProgress;
        _bruteForceService.LogMessage += OnLogMessage;
        _bruteForceService.FileCopyProgress += OnFileCopyProgress;
        _bruteForceService.CRCValidationProgress += OnCRCValidationProgress;
        _bruteForceService.TimestampPreservationFailed += OnTimestampPreservationFailed;

        _progress = new ReconstructionProgressTracker<VersionEntry>(
            VersionEntries,
            createRow: (label, args, dir, inputDir, outputPath, executedArgs, inputFileArgs) => new VersionEntry
            {
                VersionName = label,
                Arguments = args,
                VersionDirectory = dir,
                InputDirectory = inputDir,
                OutputFilePath = outputPath,
                ExecutedArguments = executedArgs,
                InputFileArguments = inputFileArgs,
            },
            setStatus: (row, status) => row.Status = status,
            setResult: (row, result) => row.Result = result,
            setSetText: (row, setText) => row.SetText = setText,
            // The per-combination "Testing …" log line deliberately uses the SHORT exe+switches form —
            // the merged log's lines stay terse (the cd-prefix and temp paths would repeat on every
            // line); the full runnable invocation is the row's FullCommandLine, reached via
            // Copy Full Command Line.
            getFullCommandLine: row => row.ExeAndArguments,
            appendLog: AppendLog);

        _runner = new ReconstructionRunner(
            bruteForceService,
            _fileMover,
            settingsService,
            _progress,
            VersionEntries,
            this,
            () => _import,
            () => OutputPath,
            () => CompleteAllVolumes,
            () => LastVersionScan,
            BuildSharedSettingsAsync,
            message => Log(LogTarget.System, message));

        _elapsedTimer = timerFactory.Create(TimeSpan.FromSeconds(1), OnElapsedTimerTick);

        ApplyPathDefaultsFromSettings();
        RefreshPathStatuses();

        _settingsService?.Changed += OnSettingsChanged;
    }

    /// <summary>
    /// A settings save (e.g. a new WinRAR versions folder) should reach the Reconstructor without a
    /// restart. ApplyPathDefaultsFromSettings only fills empty paths, so a path the user typed here
    /// is never overwritten.
    /// </summary>
    private void OnSettingsChanged(object? sender, EventArgs e)
    {
        ApplyPathDefaultsFromSettings();
        RefreshPathStatuses();
    }

    /// <summary>
    /// Pre-fills the WinRAR versions folder and output folder from settings, never overwriting
    /// values the user already typed.
    /// </summary>
    private void ApplyPathDefaultsFromSettings()
    {
        if (_settingsService is null)
        {
            return;
        }

        AppSettings settings = _settingsService.Load();
        if (string.IsNullOrWhiteSpace(WinRARPath) && !string.IsNullOrWhiteSpace(settings.ReconstructWinRARPath))
        {
            WinRARPath = settings.ReconstructWinRARPath;
        }

        if (string.IsNullOrWhiteSpace(OutputPath) && !string.IsNullOrWhiteSpace(settings.ReconstructOutputPath))
        {
            OutputPath = settings.ReconstructOutputPath;
        }
    }

    // ── Warning ──

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasCustomPackerWarning))]
    public partial string? CustomPackerWarning { get; set; }

    public bool HasCustomPackerWarning => !string.IsNullOrEmpty(CustomPackerWarning);

    /// <summary>
    /// The last Import/Export Configuration outcome, bound by the Reconstructor to a visible
    /// TextBlock with <c>AutomationProperties.LiveSetting=Polite</c> so the outcome is announced to
    /// screen readers (4.1.3). Before this, both commands reported only into
    /// <see cref="LogEntries"/>, which is deliberately not a live region, so neither said anything.
    /// Mirrors <c>SaveLogAnnouncement</c>'s contract exactly, including staying empty when the user
    /// cancels the dialog — the cancel is its own feedback, and a stale success line would mislead.
    /// </summary>
    [ObservableProperty]
    public partial string ConfigAnnouncement { get; set; } = string.Empty;

    /// <summary>True once an SRR has been successfully imported (drives the Beginner wizard's step gating).</summary>
    [ObservableProperty]
    public partial bool HasImportedSRR { get; set; }

    // ── Imported SRR details (shown after import) ──

    [ObservableProperty]
    public partial string ImportedSRRName { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string ImportedSRRAppName { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string ImportedRARVolumeText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string ImportedArchivedFilesText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string ImportedCompressionText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string ImportedStoredFilesText { get; set; } = string.Empty;

    // ── Paths ──


    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartCommand))]
    [NotifyPropertyChangedFor(nameof(PathsNeedAttention))]
    [NotifyPropertyChangedFor(nameof(PathsTabAccessibleName))]
    public partial string ReleasePath { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PathsNeedAttention))]
    [NotifyPropertyChangedFor(nameof(PathsTabAccessibleName))]
    public partial string VerificationPath { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartCommand))]
    [NotifyPropertyChangedFor(nameof(PathsNeedAttention))]
    [NotifyPropertyChangedFor(nameof(PathsTabAccessibleName))]
    public partial string OutputPath { get; set; } = string.Empty;

    // ── Path status ──

    [ObservableProperty]
    public partial FieldStatus WinRARStatus { get; set; } = FieldStatus.None;

    [ObservableProperty]
    public partial FieldStatus ReleaseStatus { get; set; } = FieldStatus.None;

    [ObservableProperty]
    public partial FieldStatus VerifyStatus { get; set; } = FieldStatus.None;

    [ObservableProperty]
    public partial FieldStatus OutputStatus { get; set; } = FieldStatus.None;

    [ObservableProperty]
    public partial FieldStatus ArchiveSetStatus { get; set; } = FieldStatus.None;

    partial void OnWinRARPathChanged(string value)
    {
        WinRARStatus = ReconstructorFieldGuidance.EvaluateWinRARPath(value);

        // The folder changed, so the previous folder's scan no longer describes the current path.
        // Mark the tree as not-yet-scanned (and invalidate any in-flight scan) BEFORE kicking off the
        // async scan for this folder. Otherwise a config's pending version selection applied right
        // after this (the mapper sets WinRARPath, then LoadPendingVersionSelection) would be consumed
        // by ApplyReconcile against the STALE previous scan and lost before the new folder's scan
        // lands, clearing the restored major toggles too.
        _versions.InvalidateAndStartScan();
    }

    partial void OnReleasePathChanged(string value) => RefreshReleaseOutputStatuses();

    partial void OnOutputPathChanged(string value) => RefreshReleaseOutputStatuses();

    /// <summary>
    /// Recomputes the Release and Output statuses together: an overlap between the two folders is a
    /// relationship, so a change to either must re-evaluate both (turning both red on overlap, or
    /// clearing both when resolved).
    /// </summary>
    private void RefreshReleaseOutputStatuses()
    {
        ReleaseStatus = ReconstructorFieldGuidance.EvaluateReleasePath(ReleasePath, OutputPath);
        OutputStatus = ReconstructorFieldGuidance.EvaluateOutputPath(OutputPath, ReleasePath);
    }

    partial void OnVerificationPathChanged(string value) =>
        VerifyStatus = ReconstructorFieldGuidance.EvaluateVerificationPath(value);

    /// <summary>
    /// Recomputes all four path statuses from the current path values. Called at construction and
    /// after <see cref="Reset"/> so a blank field shows its "Required" marker immediately — the
    /// per-property change hooks only fire when a value actually changes.
    /// </summary>
    private void RefreshPathStatuses()
    {
        WinRARStatus = ReconstructorFieldGuidance.EvaluateWinRARPath(WinRARPath);
        VerifyStatus = ReconstructorFieldGuidance.EvaluateVerificationPath(VerificationPath);
        RefreshReleaseOutputStatuses();
    }

    /// <summary>
    /// True while any required path (WinRAR, Release, Verify, Output) is empty or invalid —
    /// drives the warning glyph on the Paths sub-tab header.
    /// </summary>
    public bool PathsNeedAttention =>
        ReconstructorFieldGuidance.PathsNeedAttention(WinRARPath, ReleasePath, VerificationPath, OutputPath);

    /// <summary>
    /// The Paths sub-tab's accessible name, carrying BOTH halves of what that header shows: the
    /// word "Paths" and, when <see cref="PathsNeedAttention"/> is true, the warning glyph's meaning
    /// in words. It exists as a VM property rather than a literal in the view because the glyph is
    /// the only place that state was ever expressed, and it is expressed purely visually — a
    /// TabItem peer does not expose its header's TextBlocks as children, so nothing about "needs
    /// attention" reached a screen reader at all. Change-notified from all four path properties
    /// (the same four <see cref="PathsNeedAttention"/> is derived from), so the announced name
    /// tracks the glyph exactly rather than going stale behind it.
    /// </summary>
    public string PathsTabAccessibleName => PathsNeedAttention ? "Paths — needs attention" : "Paths";

    // ── Progress ──

    [ObservableProperty]
    public partial double ProgressPercent { get; set; }

    [ObservableProperty]
    public partial string ProgressMessage { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string PhaseDescription { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartCommand))]
    public partial bool IsRunning { get; set; }

    /// <summary>
    /// True after a run completed successfully; reset when a new run starts. The wizard uses this
    /// to hide Back once the reconstruction is done.
    /// </summary>
    [ObservableProperty]
    public partial bool LastRunSucceeded { get; set; }

    /// <summary>
    /// One-shot: set by the wizard after it already asked the "output directory is not empty"
    /// question on the Files &amp; folders step, so Start doesn't ask a second time.
    /// </summary>
    public bool SuppressOutputNotEmptyConfirm { get; set; }

    /// <summary>
    /// One-shot: set by the wizard after it already asked the subdirectory modified-date
    /// warning on the Files &amp; folders step, so Start doesn't ask a second time.
    /// </summary>
    public bool SuppressSubdirTimestampConfirm { get; set; }

    /// <summary>
    /// The subdirectory modified-date warning, shared between Start and the wizard's step.
    /// </summary>
    public const string SubdirTimestampWarningText =
        "Release directory contains one or more subdirectories.\n" +
        "RAR file(s) preserve the modified date of files and subdirectories.\n" +
        "This means that if one or more subdirectories have been created manually, " +
        "the modified date will be different than the modified date of the directory in the original archive.\n" +
        "In this case, there is no chance of properly recreating the RAR file(s).\n\n" +
        "Are you sure the modified date of the file(s) and subdirectories are correct?";

    /// <summary>
    /// Whether Start would show the subdirectory modified-date warning: the release directory
    /// has subdirectories but the imported SRR carried no directory timestamps to restore.
    /// </summary>
    public bool NeedsSubdirTimestampWarning() =>
        ReconstructorFieldGuidance.NeedsSubdirTimestampWarning(ReleasePath, _import.DirTimestamps.Count);

    [ObservableProperty]
    public partial bool ShowProgress { get; set; }

    // ── Progress window state ──

    [ObservableProperty] public partial string TestCountText { get; set; } = string.Empty;
    [ObservableProperty] public partial string ProgressPercentText { get; set; } = string.Empty;
    [ObservableProperty] public partial string CurrentDetailText { get; set; } = string.Empty;
    [ObservableProperty] public partial string ElapsedText { get; set; } = string.Empty;
    [ObservableProperty] public partial string RemainingText { get; set; } = string.Empty;
    [ObservableProperty] public partial string SpeedText { get; set; } = string.Empty;
    [ObservableProperty] public partial string EtaText { get; set; } = string.Empty;
    [ObservableProperty] public partial bool AutoScrollProgress { get; set; } = true;
    [ObservableProperty] public partial bool AutoScrollLog { get; set; } = true;

    public ObservableCollection<VersionEntry> VersionEntries { get; } = [];

    // ── File copy progress window state ──

    [ObservableProperty] public partial bool IsCopying { get; set; }
    [ObservableProperty] public partial string CopyHeadingText { get; set; } = string.Empty;
    [ObservableProperty] public partial string CopySourceText { get; set; } = string.Empty;
    [ObservableProperty] public partial string CopyDestText { get; set; } = string.Empty;
    [ObservableProperty] public partial double CopyProgressPercent { get; set; }
    [ObservableProperty] public partial string CopyProgressPercentText { get; set; } = string.Empty;
    [ObservableProperty] public partial string CopyCurrentFileText { get; set; } = string.Empty;
    [ObservableProperty] public partial string CopyRemainingText { get; set; } = string.Empty;
    [ObservableProperty] public partial string CopyElapsedText { get; set; } = string.Empty;
    [ObservableProperty] public partial string CopySpeedText { get; set; } = string.Empty;
    [ObservableProperty] public partial string CopyTimeRemainingText { get; set; } = string.Empty;
    [ObservableProperty] public partial string CopyEtaText { get; set; } = string.Empty;

    // ── CRC validation progress window state ──

    [ObservableProperty] public partial bool IsVerifying { get; set; }
    [ObservableProperty] public partial string VerifyHeadingText { get; set; } = string.Empty;
    [ObservableProperty] public partial double VerifyProgressPercent { get; set; }
    [ObservableProperty] public partial string VerifyProgressPercentText { get; set; } = string.Empty;
    [ObservableProperty] public partial string VerifyCurrentFileText { get; set; } = string.Empty;
    [ObservableProperty] public partial string VerifyRemainingText { get; set; } = string.Empty;
    [ObservableProperty] public partial string VerifyElapsedText { get; set; } = string.Empty;
    [ObservableProperty] public partial string VerifySpeedText { get; set; } = string.Empty;
    [ObservableProperty] public partial string VerifyTimeRemainingText { get; set; } = string.Empty;
    [ObservableProperty] public partial string VerifyEtaText { get; set; } = string.Empty;

    // ── Log ──

    /// <summary>
    /// The run's single chronological log, shown by both the Advanced tab and the Beginner wizard and
    /// written verbatim by Save log. Replaces the WPF-era System/Phase 1/Phase 2 split (the split made
    /// Phase-2 failures invisible to wizard users parked on the System view). Engine phase lines carry
    /// a [P1]/[P2] provenance tag (stamped in <see cref="AppendLog"/>; legend logged at run start);
    /// narrative lines are untagged. Mutated on the UI thread only, via the batched flush (#20).
    /// </summary>
    public ObservableCollection<string> LogEntries { get; } = [];

    // ── RAR Versions ──

    [ObservableProperty] public partial bool Version2 { get; set; }
    [ObservableProperty] public partial bool Version3 { get; set; } = true;
    [ObservableProperty] public partial bool Version4 { get; set; } = true;
    [ObservableProperty] public partial bool Version5 { get; set; } = true;
    [ObservableProperty] public partial bool Version6 { get; set; } = true;
    [ObservableProperty] public partial bool Version7 { get; set; }

    // ── Per-sub-version selection (tree over the installed WinRAR versions) ──

    /// <summary>Installed-version tree grouped by major; the checked leaves drive the brute-force.</summary>
    public ObservableCollection<RARVersionGroup> VersionGroups { get; } = [];

    /// <summary>True once a folder scan has completed for an existing folder (even if it had no versions).</summary>
    [ObservableProperty]
    public partial bool HasScannedVersions { get; set; }

    /// <summary>True when the tree is empty, so the view can show the "no versions found" hint.</summary>
    [ObservableProperty]
    public partial bool ShowNoVersionsHint { get; set; }

    /// <summary>The currently-ticked leaf versions, ascending. Snapshotted at Start and by config Capture.</summary>
    internal IReadOnlyList<int> SelectedLeafVersions =>
        VersionGroups.SelectMany(g => g.Leaves).Where(l => l.IsChecked).Select(l => l.Version).OrderBy(v => v).ToList();

    /// <summary>
    /// The currently-ticked leaf FOLDER names (e.g. "winrar-390-beta1"). Carried to the engine as the
    /// version-folder allow-list so unticking one same-version variant leaf actually excludes its
    /// folder (two folders can parse to the same version, so version ranges alone cannot distinguish
    /// them).
    /// </summary>
    internal IReadOnlyList<string> SelectedLeafFolders =>
        VersionGroups.SelectMany(g => g.Leaves).Where(l => l.IsChecked).Select(l => l.FolderName).ToList();

    [RelayCommand]
    private void RescanVersions() => _versions.Rescan();

    [RelayCommand]
    private void SelectAllVersions() => _versions.SetAllLeaves(true);

    [RelayCommand]
    private void SelectNoVersions() => _versions.SetAllLeaves(false);

    /// <summary>The most recent folder-scan Task, exposed so tests can await scan completion
    /// deterministically (production is fire-and-forget and marshals results to the UI thread).</summary>
    internal Task? LastVersionScan => _versions.LastVersionScan;

    /// <summary>Stores a scan result and reconciles the tree. Also the test seam for the async scan.</summary>
    internal void ApplyScanResult(IReadOnlyList<InstalledRARVersion> installed, bool folderScanned) =>
        _versions.ApplyScanResult(installed, folderScanned);

    /// <summary>Sets the pending explicit selection (config load) and reconciles against the last scan.
    /// Called from production by ReconstructorConfigMapper.</summary>
    internal void LoadPendingVersionSelection(IReadOnlyList<int>? explicitVersions) =>
        _versions.LoadPendingVersionSelection(explicitVersions);

    /// <summary>Mirrors "any leaf in this major ticked" onto the coarse major bools - but only when a
    /// tree exists; with no scan the bools remain the fallback/coarse intent.</summary>
    /// <remarks>
    /// Each read is interleaved with its own write, and every write can synchronously raise
    /// PropertyChanged - so a subscriber that mutates a later major's leaves IS seen by the reads that
    /// follow. Do not batch the six predicates and write them afterwards.
    /// </remarks>
    private void SyncMajorsFromTree()
    {
        if (!HasScannedVersions)
        {
            return;
        }

        Version2 = MajorHasTick(2);
        Version3 = MajorHasTick(3);
        Version4 = MajorHasTick(4);
        Version5 = MajorHasTick(5);
        Version6 = MajorHasTick(6);
        Version7 = MajorHasTick(7);
    }

    private bool MajorHasTick(int major) =>
        VersionGroups.FirstOrDefault(g => g.Major == major)?.Leaves.Any(l => l.IsChecked) ?? false;

    private HashSet<int> EnabledMajors()
    {
        HashSet<int> majors = [];
        if (Version2)
        {
            majors.Add(2);
        }

        if (Version3)
        {
            majors.Add(3);
        }

        if (Version4)
        {
            majors.Add(4);
        }

        if (Version5)
        {
            majors.Add(5);
        }

        if (Version6)
        {
            majors.Add(6);
        }

        if (Version7)
        {
            majors.Add(7);
        }

        return majors;
    }

    // ── Computed enable/disable ──

    public bool IsMTRangeEnabled => SwitchMT;
    public bool IsVolumeOptionsEnabled => SwitchV;
    public bool IsSwitchAIEnabled => FileA == false && FileI == false;
    public bool IsFileAttributesEnabled => !SwitchAI;
    public bool IsDeleteDuplicateCRCEnabled => !DeleteRARFiles;
    public bool IsRenameEnabled => StopOnFirstMatch;

    // Host OS patching
    [ObservableProperty] public partial bool EnableHostOSPatching { get; set; } = true;

    // ── Reset ──

    /// <summary>
    /// Clears the import-gating and UI state back to a freshly-constructed default so a
    /// Beginner wizard opens clean. No-op while a run is in progress (e.g. started from the
    /// Advanced tab) so an active run isn't disrupted.
    /// </summary>
    public void Reset()
    {
        if (IsRunning)
        {
            return;
        }

        // Paths
        WinRARPath = string.Empty;
        ReleasePath = string.Empty;
        VerificationPath = string.Empty;
        OutputPath = string.Empty;

        // Import gating + warning
        HasImportedSRR = false;
        CustomPackerWarning = null;
        LastRunSucceeded = false;

        // Imported SRR details
        ImportedSRRName = string.Empty;
        ImportedSRRAppName = string.Empty;
        ImportedRARVolumeText = string.Empty;
        ImportedArchivedFilesText = string.Empty;
        ImportedCompressionText = string.Empty;
        ImportedStoredFilesText = string.Empty;

        // Imported SRR + detected header state — back to empty/null
        _import.Clear();
        ArchiveSetStatus = FieldStatus.None;

        // Progress
        ProgressPercent = 0;
        ProgressMessage = string.Empty;
        PhaseDescription = string.Empty;
        ShowProgress = false;
        TestCountText = string.Empty;
        ProgressPercentText = string.Empty;
        CurrentDetailText = string.Empty;
        ElapsedText = string.Empty;
        RemainingText = string.Empty;
        SpeedText = string.Empty;
        EtaText = string.Empty;
        _progress.Clear();

        // Log
        LogEntries.Clear();

        // The brute-force option toggles (versions, compression, dictionary, timestamps,
        // volume, etc.) are intentionally left untouched: they are re-applied wholesale by
        // the mandatory Import-from-SRR step that opens the reconstruct wizard.

        // The paths were just cleared; pre-fill the configured defaults again.
        ApplyPathDefaultsFromSettings();
        RefreshPathStatuses();
    }

    // ── Browse Commands ──

    [RelayCommand]
    private async Task BrowseWinRARAsync()
    {
        string? path = await _fileDialog.OpenFolderAsync("Select WinRAR Installations Directory", WinRARPath);
        if (path is not null)
        {
            WinRARPath = path;
        }
    }

    [RelayCommand]
    private async Task BrowseReleaseAsync()
    {
        string? path = await _fileDialog.OpenFolderAsync("Select Release Directory", ReleasePath);
        if (path is not null)
        {
            ReleasePath = path;
        }
    }

    [RelayCommand]
    private async Task BrowseVerificationAsync()
    {
        // Anchor on the field unless it is blank OR points at the SRR import's auto-extracted SFV
        // in the scratch temp dir — starting the picker inside that scratch folder would strand the
        // user far from their release; the .sfv they actually want lives in the release.
        bool fieldIsUsableAnchor = !string.IsNullOrWhiteSpace(VerificationPath)
            && !(_sfvTempDir is not null && VerificationPath.StartsWith(_sfvTempDir, StringComparison.Ordinal));

        string? path = await _fileDialog.OpenFileAsync("Select Verification File",
            FileDialogFilters.VerificationFiles,
            fieldIsUsableAnchor ? VerificationPath : ReleasePath);
        if (path is not null)
        {
            VerificationPath = path;
        }
    }

    [RelayCommand]
    private async Task BrowseOutputAsync()
    {
        string? path = await _fileDialog.OpenFolderAsync("Select Output Directory", OutputPath);
        if (path is not null)
        {
            OutputPath = path;
        }
    }

    // ── Import SRR ──

    [RelayCommand]
    private async Task ImportSRRAsync()
    {
        string? path = await _fileDialog.OpenFileAsync("Select SRR File",
            FileDialogFilters.SRRFiles, ReleasePath); // SRRs are typically kept near the release
        if (path is null)
        {
            return;
        }

        HasImportedSRR = false;

        // Cleared alongside HasImportedSRR, for two reasons. A failed load must not leave the
        // PREVIOUS SRR's warning on screen; and the warning now also drives an always-in-tree
        // polite live region in the view, which only announces on an empty-to-text transition —
        // without this, importing an SRR whose warning text matches the one already showing would
        // set an equal value, raise no change notification, and say nothing. Same reasoning as
        // OperationViewModelBase.SaveLogToFileAsync's own clear-first. Both branches below set it
        // definitively, so this never leaves it stale.
        CustomPackerWarning = null;

        try
        {
            Log(LogTarget.System, $"=== SRR Import: {Path.GetFileName(path)} ===");

            var srr = SRRFile.Load(path);
            Log(LogTarget.System, "SRR loaded successfully");

            // Pure parse: imported/detected state, custom-packer detection, and display strings.
            ImportedSRRInfo info = SRRImportParser.Parse(srr, path);

            // Detect SRRs that carry no RAR reconstruction information
            // (no RAR volume entries, no archived-file metadata, no detected
            // compression method). These can't drive automatic option setup,
            // so warn the user that they'll need to configure things manually.
            if (!info.HasRARReconstructionInfo)
            {
                Log(LogTarget.System,
                    "WARNING: SRR contains no RAR reconstruction information.");
                _fileDialog.ShowInfo(
                    "No RAR Reconstruction Info",
                    "This SRR file does not contain RAR reconstruction information " +
                    "(no RAR volume entries, archived files, or compression metadata).\n\n" +
                    "You will need to configure the RAR options manually before reconstructing.");
            }

            // Remember the imported SRR path for ALL SRRs (not just custom-packer ones). It is the
            // source for each set's embedded per-volume SFV (LoadEmbeddedSfvBytes) and lets
            // ArchiveSetPlanner.ResolveSets re-derive sets from the SRR on config-restore. This is
            // harmless for normal SRRs: RAROptions.SRRFilePath is consumed by the engine only on the
            // custom-packer direct path (Manager guards on CustomPackerDetected != None), so a
            // non-null value is ignored by the brute-force path.
            _import.SRRFilePath = path;

            // Custom packer detection
            if (srr.HasCustomPackerHeaders)
            {
                Log(LogTarget.System, $"Custom RAR packer detected: {srr.CustomPackerDetected}");
                _import.CustomPackerType = info.CustomPackerType;
                string warning = info.CustomPackerWarning ?? string.Empty;
                CustomPackerWarning = warning;

                _fileDialog.ShowWarning("Custom RAR Packer Detected", warning);
            }
            else
            {
                _import.CustomPackerType = CustomPackerType.None;
                CustomPackerWarning = null;
            }

            // Store imported data
            _import.ArchiveFiles = info.ArchiveFiles;
            _import.ArchiveDirectories = info.ArchiveDirectories;
            _import.DirTimestamps = info.DirTimestamps;
            _import.DirCreationTimes = info.DirCreationTimes;
            _import.DirAccessTimes = info.DirAccessTimes;
            _import.FileTimestamps = info.FileTimestamps;
            _import.FileCreationTimes = info.FileCreationTimes;
            _import.FileAccessTimes = info.FileAccessTimes;
            _import.ArchiveFileCrcs = info.ArchiveFileCrcs;
            _import.OriginalRARFileNames = info.OriginalRARFileNames;
            _import.ArchiveSets = info.ArchiveSets;
            ArchiveSetStatus = _import.ArchiveSets.Count > 1
                ? FieldStatus.Info($"This release has {_import.ArchiveSets.Count} archive sets " +
                    $"({string.Join(", ", _import.ArchiveSets.Select(s => string.IsNullOrEmpty(s.Directory) ? s.Key : s.Directory))}); each is reconstructed independently.")
                : FieldStatus.None;
            _import.ArchiveComment = info.ArchiveComment;
            _import.ArchiveCommentBytes = info.ArchiveCommentBytes;
            _import.CmtCompressedData = info.CmtCompressedData;
            _import.CmtCompressionMethod = info.CmtCompressionMethod;

            if (_import.ArchiveFiles.Count > 0 || _import.ArchiveDirectories.Count > 0)
            {
                string dirSuffix = _import.ArchiveDirectories.Count > 0 ? $", {_import.ArchiveDirectories.Count} dirs" : "";
                Log(LogTarget.System, $"Archive entries: {_import.ArchiveFiles.Count} files{dirSuffix}");
            }

            Log(LogTarget.System, $"Per-file timestamps: mtime={_import.FileTimestamps.Count}, ctime={_import.FileCreationTimes.Count}, atime={_import.FileAccessTimes.Count}");

            if (_import.CmtCompressedData is { Length: > 0 })
            {
                Log(LogTarget.System, $"CMT data: {_import.CmtCompressedData.Length} bytes — Phase 1 enabled");
            }

            // Host OS
            _import.DetectedFileHostOS = info.DetectedFileHostOS;
            _import.DetectedFileAttributes = info.DetectedFileAttributes;
            _import.DetectedCmtHostOS = info.DetectedCmtHostOS;
            _import.DetectedCmtFileTime = info.DetectedCmtFileTime;
            _import.DetectedCmtFileAttributes = info.DetectedCmtFileAttributes;
            _import.DetectedLargeFlag = info.DetectedLargeFlag;
            _import.DetectedHighPackSize = info.DetectedHighPackSize;
            _import.DetectedHighUnpSize = info.DetectedHighUnpSize;

            if (srr.HasLargeFiles == true)
            {
                EnableHostOSPatching = true;
                Log(LogTarget.System, "LARGE flag detected — header patching enabled");
            }

            if (srr.DetectedHostOS.HasValue)
            {
                Log(LogTarget.System, $"Host OS: {srr.DetectedHostOSName} (0x{srr.DetectedHostOS:X2})");
                bool isCurrentWindows = Environment.OSVersion.Platform == PlatformID.Win32NT;
                bool isRARUnix = srr.DetectedHostOS == 3;
                bool isRARWindows = srr.DetectedHostOS == 2;
                if ((isCurrentWindows && isRARUnix) || (!isCurrentWindows && isRARWindows))
                {
                    EnableHostOSPatching = true;
                    Log(LogTarget.System, "Host OS patching enabled (platform mismatch)");
                }
            }

            // Pure switch mapping: only the toggles the SRR actually specifies (partial diff —
            // unspecified groups stay null and the corresponding toggles are left untouched).
            SRRSwitchMapper.SwitchDiff switches = SRRSwitchMapper.Map(srr);
            ApplySwitchDiff(switches);

            // Timestamp precision
            TimestampPrecision? mtimePrecision = srr.FileMtimePrecision ?? srr.CmtMtimePrecision;
            TimestampPrecision? ctimePrecision = srr.FileCtimePrecision ?? srr.CmtCtimePrecision;
            TimestampPrecision? atimePrecision = srr.FileAtimePrecision ?? srr.CmtAtimePrecision;

            if (mtimePrecision.HasValue)
            {
                SetTimestampFlags(mtimePrecision.Value,
                    v => SwitchTSM0 = v, v => SwitchTSM1 = v, v => SwitchTSM2 = v, v => SwitchTSM3 = v, v => SwitchTSM4 = v);
                Log(LogTarget.System, $"Mtime precision: -tsm{(int)mtimePrecision.Value}");
            }

            if (ctimePrecision.HasValue)
            {
                SetTimestampFlags(ctimePrecision.Value,
                    v => SwitchTSC0 = v, v => SwitchTSC1 = v, v => SwitchTSC2 = v, v => SwitchTSC3 = v, v => SwitchTSC4 = v);
                Log(LogTarget.System, $"Ctime precision: -tsc{(int)ctimePrecision.Value}");
            }

            if (atimePrecision.HasValue)
            {
                SetTimestampFlags(atimePrecision.Value,
                    v => SwitchTSA0 = v, v => SwitchTSA1 = v, v => SwitchTSA2 = v, v => SwitchTSA3 = v, v => SwitchTSA4 = v);
                Log(LogTarget.System, $"Atime precision: -tsa{(int)atimePrecision.Value}");
            }

            // Optimise: single attribute/thread configuration
            FileA = false;
            FileI = false;
            SwitchAI = false;
            SwitchMT = false;
            SwitchR = true;

            // Volume size. The SRR fully determines the volume state, so a single-volume release must
            // actively CLEAR any multi-volume switch left over from a previous import — otherwise a
            // stale -v… would be added to every combination and guarantee a no-match.
            if (srr.RARFiles.Count > 1 && srr.VolumeSizeBytes.HasValue)
            {
                ApplyVolumeSize(srr.VolumeSizeBytes.Value);
            }
            else if (srr.IsVolumeArchive == true)
            {
                SwitchV = true;
                Log(LogTarget.System, "Multi-volume: Yes (size unknown)");
            }
            else if (srr.IsVolumeArchive == false || srr.RARFiles.Count <= 1)
            {
                if (SwitchV)
                {
                    Log(LogTarget.System, "Multi-volume: No");
                }

                SwitchV = false;
                UseOldVolumeNaming = false;
            }

            // Volume naming
            if (srr.IsVolumeArchive == true && srr.HasNewVolumeNaming == false)
            {
                UseOldVolumeNaming = true;
                Log(LogTarget.System, "Volume naming: Old (.rar, .r00)");
            }
            else if (srr.IsVolumeArchive == true && srr.HasNewVolumeNaming == true)
            {
                UseOldVolumeNaming = false;
            }

            // RAR version selection
            SetRARVersionsFromSRR(srr);
            _versions.ClearPendingSelectionAndReconcile();

            // Extract stored SFV for verification
            TryExtractStoredSFV(path, srr);

            Log(LogTarget.System, "=== SRR Import Complete ===");

            PopulateImportedSRRDetails(info);
            HasImportedSRR = true;
        }
        catch (Exception ex)
        {
            Log(LogTarget.System, $"Failed to import SRR: {ex.Message}");
        }
    }

    /// <summary>Maps the parsed SRR summary onto the bound display properties shown on the wizard's import step.</summary>
    private void PopulateImportedSRRDetails(ImportedSRRInfo info)
    {
        ImportedSRRName = info.DisplayName;
        ImportedSRRAppName = info.DisplayAppName;
        ImportedRARVolumeText = info.DisplayRARVolumeText;
        ImportedArchivedFilesText = info.DisplayArchivedFilesText;
        ImportedCompressionText = info.DisplayCompressionText;
        ImportedStoredFilesText = info.DisplayStoredFilesText;
    }

    // ── Import / Export Configuration ──

    // PropertyNameCaseInsensitive so configs exported by older builds (which used mixed SRR/SRR,
    // RAR/RAR property casing) still import after the identifiers were normalized to SRR/RAR.
    private static readonly System.Text.Json.JsonSerializerOptions _configSerializerOptions =
        new() { WriteIndented = true, PropertyNameCaseInsensitive = true };

    [RelayCommand]
    private async Task ImportConfigAsync()
    {
        // Cleared FIRST so every outcome below is a genuine empty-to-message transition: both
        // CommunityToolkit's setter and Avalonia's TextBlock.Text suppress equal-value changes, so
        // re-importing the same file would otherwise announce nothing. Same reasoning, and the same
        // "do not simplify this away" warning, as OperationViewModelBase.SaveLogToFileAsync.
        ConfigAnnouncement = string.Empty;

        string? path = await _fileDialog.OpenFileAsync("Select Reconstructor Configuration",
            FileDialogFilters.ReconstructorConfig); // no meaningful anchor — deliberate platform-default start
        if (path is null)
        {
            return;
        }

        try
        {
            string json = await File.ReadAllTextAsync(path);
            ReconstructorConfig? config = System.Text.Json.JsonSerializer.Deserialize<ReconstructorConfig>(json, _configSerializerOptions);
            if (config is null)
            {
                Log(LogTarget.System, "Failed to import configuration: file is empty or invalid");
                ConfigAnnouncement = "Could not import the configuration: the file is empty or invalid";
                return;
            }

            ApplyConfig(config);
            Log(LogTarget.System, $"Configuration imported from {Path.GetFileName(path)}");
            ConfigAnnouncement = $"Configuration imported from {Path.GetFileName(path)}";
        }
        catch (Exception ex)
        {
            Log(LogTarget.System, $"Failed to import configuration: {ex.Message}");
            ConfigAnnouncement = $"Could not import the configuration: {ex.Message}";
        }
    }

    [RelayCommand]
    private async Task ExportConfigAsync()
    {
        // Cleared FIRST — see ImportConfigAsync.
        ConfigAnnouncement = string.Empty;

        string? path = await _fileDialog.SaveFileAsync("Save Reconstructor Configuration",
            ".json", FileDialogFilters.ReconstructorConfig, "reconstructor-config.json");
        if (path is null)
        {
            return;
        }

        try
        {
            ReconstructorConfig config = CaptureConfig();
            string json = System.Text.Json.JsonSerializer.Serialize(config, _configSerializerOptions);
            await File.WriteAllTextAsync(path, json);
            Log(LogTarget.System, $"Configuration exported to {Path.GetFileName(path)}");
            ConfigAnnouncement = $"Configuration exported to {Path.GetFileName(path)}";
        }
        catch (Exception ex)
        {
            Log(LogTarget.System, $"Failed to export configuration: {ex.Message}");
            ConfigAnnouncement = $"Could not export the configuration: {ex.Message}";
        }
    }

    private ReconstructorConfig CaptureConfig()
    {
        ReconstructorConfig config = ReconstructorConfigMapper.Capture(this);
        config.ImportedSRR = CaptureImportedSRRState();
        return config;
    }

    private ImportedSRRState? CaptureImportedSRRState() =>
        ImportedSRRStateMapper.Capture(_import, CustomPackerWarning);

    private void ApplyConfig(ReconstructorConfig c)
    {
        ReconstructorConfigMapper.Apply(this, c);
        ApplyImportedSRRState(c.ImportedSRR);
    }

    private void ApplyImportedSRRState(ImportedSRRState? s)
    {
        // Always reset — an absent block means "no SRR imported"
        _import = ImportedSRRStateMapper.Apply(s);
        CustomPackerWarning = s?.CustomPackerWarning;

        if (s is not null)
        {
            Log(LogTarget.System, $"Restored SRR state: {_import.ArchiveFiles.Count} files, mtime={_import.FileTimestamps.Count}, CRCs={_import.ArchiveFileCrcs.Count}, CMT={_import.CmtCompressedData?.Length ?? 0} bytes");
        }
    }

    // ── Start / Stop ──

    /// <summary>
    /// Whether the WinRAR, Release, and Output paths are all set and the Release/Output folders do
    /// not overlap — the path preconditions shared by Start (the command) and the Beginner wizard's
    /// "Files &amp; folders" step. Centralised so the two callers cannot drift apart.
    /// </summary>
    public bool PathsReadyToStart =>
        !string.IsNullOrWhiteSpace(WinRARPath)
        && !string.IsNullOrWhiteSpace(ReleasePath)
        && !string.IsNullOrWhiteSpace(OutputPath)
        && !ReconstructorFieldGuidance.PathsOverlap(ReleasePath, OutputPath);

    /// <summary>
    /// The plan-before-mutate preflight: resolves the archive sets and makes every reject-the-run
    /// decision (multi-set custom packer, reserved-root distinctness, live-input overlap, and the
    /// no-file-list release/output self-inclusion) WITHOUT touching the filesystem destructively.
    /// Returns null when the run may proceed, else the user-facing rejection reason. <see cref="StartAsync"/>
    /// calls it before any cleanup, and the Beginner wizard calls it before its delete-confirmation, so
    /// a run that will be rejected never erases existing output (#3, #17). Returns null when there is no
    /// output path yet (the per-path validation reports that separately).
    /// </summary>
    public string? EvaluateRunPreflight()
    {
        if (string.IsNullOrWhiteSpace(OutputPath))
        {
            return null;
        }

        IReadOnlyList<SRRArchiveSet> sets;
        try
        {
            IReadOnlyList<string> flatNames = _import.OriginalRARFileNames.Count > 0
                ? _import.OriginalRARFileNames
                : (_verificationSnapshot ?? VerificationSnapshot.Empty).VolumeNames;
            sets = ArchiveSetPlanner.ResolveSets(_import.ArchiveSets, _import.SRRFilePath, flatNames, _import.ArchiveFiles);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            return $"The imported SRR could not be read:\n{ex.Message}";
        }

        IReadOnlyList<string> releaseInputs = string.IsNullOrWhiteSpace(ReleasePath)
            ? []
            : [.. _import.ArchiveFiles.Select(f => Path.Combine(ReleasePath, f))];

        bool hasArchiveFileList = _import.ArchiveFiles.Count > 0 || _import.ArchiveDirectories.Count > 0;

        return ReconstructionPreflight.Evaluate(new ReconstructionPreflight.Inputs(
            sets, OutputPath, ReleasePath, WinRARPath, VerificationPath, _import.SRRFilePath,
            releaseInputs, _import.CustomPackerType, hasArchiveFileList));
    }

    private bool CanStart() => !IsRunning && PathsReadyToStart;

    [RelayCommand(CanExecute = nameof(CanStart))]
    private async Task StartAsync()
    {
        // One-shot confirmations the wizard may already have asked on its "Files & folders"
        // step — consume them up front so a stale flag can never suppress a future prompt.
        bool subdirTimestampsConfirmed = SuppressSubdirTimestampConfirm;
        bool outputNotEmptyConfirmed = SuppressOutputNotEmptyConfirm;
        SuppressSubdirTimestampConfirm = false;
        SuppressOutputNotEmptyConfirm = false;

        if (!await _startValidator.ValidateAsync(
            new ReconstructorStartValidator.Inputs
            {
                WinRARPath = () => WinRARPath,
                HasScannedVersions = () => HasScannedVersions,
                VersionGroups = VersionGroups,
                ReleasePath = () => ReleasePath,
                OutputPath = () => OutputPath,
                VerificationPath = () => VerificationPath,
                Import = () => _import,
                SubdirTimestampsConfirmed = subdirTimestampsConfirmed,
                OutputNotEmptyConfirmed = outputNotEmptyConfirmed,
            },
            // The snapshot lands at the PARSE point, not on acceptance - a rejected start still
            // replaces it. See ReconstructorStartValidationTests.
            snapshot => _verificationSnapshot = snapshot))
        {
            return;
        }

        // ── Start brute-force ──

        IsRunning = true;
        LastRunSucceeded = false;
        ShowProgress = true;
        ProgressPercent = 0;
        ProgressMessage = "Starting...";
        _log.BeginNewGeneration();
        _setStageLabel = null;
        lock (_timestampFailuresLock)
        {
            _timestampFailures.Clear();
        }

        // Reset progress window state
        TestCountText = string.Empty;
        ProgressPercentText = string.Empty;
        CurrentDetailText = string.Empty;
        ElapsedText = "00:00";
        RemainingText = string.Empty;
        SpeedText = string.Empty;
        EtaText = string.Empty;
        _progress.StartRun();
        _elapsedTimer.Start();

        _cts = new CancellationTokenSource();
        CancellationToken token = _cts.Token;

        // Yield so the dispatcher can open the progress window before heavy work starts
        await Task.Yield();

        await ExecuteReconstructionAsync(token);
    }

    // ── Test seam (InternalsVisibleTo ReScene.App.Core.Tests) ──
    // Drives the run body plus its try/catch/finally directly, so a test can prove the once-per-run
    // timestamp summary and the synchronous final log drain both fire from the finally (#19, #20).
    internal Task ExecuteReconstructionForTestAsync(CancellationToken token) => ExecuteReconstructionAsync(token);

    /// <summary>
    /// Runs the reconstruction under a single try/catch/finally. The finally is the sole place the run
    /// finalises: it stops timers, releases the copy/verify flags and the cancellation source, drains
    /// any batched log lines synchronously (#20), and surfaces the timestamp-preservation summary
    /// exactly once (#19) — on normal completion, cancellation, and exception alike.
    /// </summary>
    private async Task ExecuteReconstructionAsync(CancellationToken token)
    {
        try
        {
            // Legend for the [P1]/[P2] provenance tags on engine phase lines — logged live (not only in
            // the saved file) so a reader of the on-screen log can decode the tags.
            Log(LogTarget.System, "[P1] = Phase 1 (comment filtering), [P2] = Phase 2 (RAR creation)");
            Log(LogTarget.System, "Starting brute-force...");
            Log(LogTarget.System, $"WinRAR: {WinRARPath}");
            Log(LogTarget.System, $"Release: {ReleasePath}");
            Log(LogTarget.System, $"Output: {OutputPath}");

            await _runner.RunArchiveSetsAsync(token);

            // A Stop during RAR execution cancels the run but returns normally (the library
            // swallows the process's OperationCanceledException), so detect the cancelled token
            // here and report "Cancelled" rather than the misleading "No match found".
            token.ThrowIfCancellationRequested();
        }
        catch (OperationCanceledException)
        {
            _progress.SetActiveVersionStatus("Cancelled");

            ProgressMessage = "Cancelled.";
            PhaseDescription = "Cancelled";
            Log(LogTarget.System, "Brute-force cancelled by user.");
        }
        catch (Exception ex)
        {
            _progress.SetActiveVersionStatus("Error");

            ProgressMessage = "Error.";
            PhaseDescription = "Error";
            Log(LogTarget.System, $"Error: {ex.Message}");
        }
        finally
        {
            _elapsedTimer.Stop();
            _progress.StopRun();
            ElapsedText = _progress.FinalElapsedText();
            IsRunning = false;

            // A cancelled/failed run stops mid-copy without a final copy-progress event;
            // clear the flag here so the copy progress window can close.
            if (IsCopying)
            {
                _progress.StopCopy();
                IsCopying = false;
            }

            // Same for input-CRC validation: a cancel during verification throws before the lib's
            // final 100% event, so IsVerifying would otherwise stay true forever and the modal CRC
            // window could never close (its Closing handler cancels while IsVerifying).
            if (IsVerifying)
            {
                _progress.StopVerify();
                IsVerifying = false;
            }

            _cts?.Dispose();
            _cts = null;

            // Drain any log lines still queued between the last batched flush and now so the run's
            // final messages are never lost (#20), then surface the timestamp summary once (#19).
            _log.Drain();
            ShowTimestampFailureWarningIfAny();
        }
    }

    // ── Per-archive-set reconstruction loop ──

    /// <summary>
    /// Reconstructs each archive set independently: per-set input/CRCs/metadata, isolated work dirs,
    /// subfolder-preserving relocation, and seeded-with-fallback cross-set search. A single root set
    /// runs exactly as before (work dir = OutputPath, no relocation, byte-identical output). A
    /// failure in one set is recorded and the loop continues to the next; a cancellation stops the
    /// loop, cleans the in-flight set, and leaves completed sets intact.
    /// </summary>
    // ── Test seams (InternalsVisibleTo ReScene.App.Core.Tests) ──
    // The reconstruction run loop reads the imported-SRR state and drives the guarded scratch → output
    // relocation; these let a test inject that state and drive the loop with a file-writing fake service.
    internal void SetImportStateForTest(ReconstructionImportState import) => _import = import;

    // ── IRunSink: the bound state the run loop writes back ──
    void IRunSink.SetStageLabel(SetStageLabel? label) => _setStageLabel = label;
    void IRunSink.SetProgressPercent(double value) => ProgressPercent = value;
    void IRunSink.SetProgressPercentText(string value) => ProgressPercentText = value;
    void IRunSink.SetTestCountText(string value) => TestCountText = value;
    void IRunSink.SetProgressMessage(string value) => ProgressMessage = value;
    void IRunSink.SetPhaseDescription(string value) => PhaseDescription = value;
    void IRunSink.SetLastRunSucceeded(bool value) => LastRunSucceeded = value;

    /// <summary>
    /// Test-facing forwarder for the embedded-SFV name match. ArchiveSetEmbeddedSfvTests calls this
    /// through the view-model, so the surface stays here even though the logic moved to the runner.
    /// </summary>
    internal static bool EmbeddedSfvMatchesSet(string storedName, SRRArchiveSet set) =>
        ReconstructionRunner.EmbeddedSfvMatchesSet(storedName, set);

    /// <summary>
    /// Test seams for the two SRR-import decisions extracted in the next step. Both are otherwise
    /// only reachable by driving a whole import, which would make a per-branch theory unreadable.
    /// </summary>
    internal void SetRARVersionsFromSRRForTest(SRRFile srr) => SetRARVersionsFromSRR(srr);

    internal void ApplyVolumeSizeForTest(long sizeBytes) => ApplyVolumeSize(sizeBytes);

    internal Task RunArchiveSetsForTestAsync(CancellationToken token) => _runner.RunArchiveSetsAsync(token);

    internal async Task<SharedReconstructionSettings> BuildSharedSettingsAsync(CancellationToken ct)
    {
        RARSwitchSettings switches = BuildSwitchSettings();
        VerificationSnapshot snapshot = _verificationSnapshot ?? VerificationSnapshot.Empty;
        IReadOnlyList<RARCommandLineArgument[]> commandLineArguments =
            await Task.Run(() => RARCommandLineBuilder.BuildCommandLineArguments(switches, ct), ct);

        return new SharedReconstructionSettings
        {
            WinRARPath = WinRARPath,
            ReleasePath = ReleasePath,
            OutputPath = OutputPath,
            RARVersions = RARCommandLineBuilder.BuildVersionRanges(switches),
            // Only folder-filter when a real scan produced the tree; the no-scan fallback uses broad
            // major-version ranges and must NOT be restricted to specific folder names.
            SelectedVersionFolders = HasScannedVersions ? SelectedLeafFolders : [],
            CommandLineArguments = commandLineArguments,
            Switches = switches,
            // Scan-state-guarded exactly like SelectedVersionFolders above: a WinRARPath change clears
            // HasScannedVersions synchronously but leaves _lastScan stale until the new scan lands, so
            // a stale scan must contribute no installed versions rather than the old folder's list.
            InstalledVersions = HasScannedVersions ? [.. _versions.LastScan] : [],
            HashType = snapshot.HashType,
            VerificationHashes = snapshot.AllHashes,
            Verification = snapshot,
            SetFileArchiveAttribute = ToTriState(FileA),
            SetFileNotContentIndexedAttribute = ToTriState(FileI),
            DeleteRARFiles = DeleteRARFiles,
            DeleteDuplicateCRCFiles = DeleteDuplicateCRCFiles,
            StopOnFirstMatch = StopOnFirstMatch,
            CompleteAllVolumes = CompleteAllVolumes,
            RenameToReleaseNames = RenameToReleaseNames,
            EnableHostOSPatching = EnableHostOSPatching,
            UseOldVolumeNaming = UseOldVolumeNaming,
            ArchiveComment = _import.ArchiveComment,
            ArchiveCommentBytes = _import.ArchiveCommentBytes,
            CmtCompressedData = _import.CmtCompressedData,
            CmtCompressionMethod = _import.CmtCompressionMethod,
            DetectedCmtHostOS = _import.DetectedCmtHostOS,
            DetectedCmtFileTime = _import.DetectedCmtFileTime,
            DetectedCmtFileAttributes = _import.DetectedCmtFileAttributes,
            CustomPackerDetected = _import.CustomPackerType,
            SRRFilePath = _import.SRRFilePath,
            ArchiveDirectories = _import.ArchiveDirectories,
            DirectoryTimestamps = _import.DirTimestamps,
            DirectoryCreationTimes = _import.DirCreationTimes,
            DirectoryAccessTimes = _import.DirAccessTimes,
        };
    }

    /// <summary>
    /// The confirmation shown before the pre-run cleanup: it clears only the two reserved subtrees
    /// (<c>output</c> and <c>.rescene-work</c>) under <paramref name="outputPath"/>, preserving unrelated
    /// root files. Shared verbatim by the Start command and the Beginner wizard so the two never drift.
    /// </summary>
    public static string OutputCleanupConfirmText(string outputPath) =>
        ReservedOutputTreeManager.ConfirmText(outputPath);

    /// <summary>
    /// Whether either reserved subtree under <c>OutputPath</c> currently holds content the pre-run
    /// cleanup would clear. Shared by Start and the Beginner wizard so both prompt on the same
    /// condition. Fails closed (returns true → prompt) if the roots cannot be resolved.
    /// </summary>
    public bool OutputHasReconstructionArtifacts() =>
        ReservedOutputTreeManager.HasReconstructionArtifacts(OutputPath);

    /// <summary>
    /// Clears the two reserved subtrees (<c>output</c> + <c>.rescene-work</c>) under <c>OutputPath</c>,
    /// resolved through the path guard so a junction cannot redirect the delete. Unrelated files at the
    /// OutputPath root are untouched (#4). Returns false (after surfacing the error) if the delete fails.
    /// </summary>
    internal bool ClearReservedSubtrees() =>
        ReservedOutputTreeManager.ClearReservedSubtrees(
            OutputPath,
            message => Log(LogTarget.System, message),
            _fileDialog.ShowError);

    [RelayCommand]
    private void Stop()
    {
        // Cancelling the token reaches the running RAR processes through the service and
        // Manager (the token is threaded into BruteForceRARVersionAsync).
        _cts?.Cancel();
        Log(LogTarget.System, "Cancellation requested...");
    }

    [RelayCommand]
    private void OpenOutputFolder()
    {
        try
        {
            // Brute-force runs put the final archives in the "output" subdirectory; direct
            // (custom packer) reconstruction writes to the output folder root.
            string folder = Path.Combine(OutputPath, "output");
            if (!Directory.Exists(folder))
            {
                folder = OutputPath;
            }

            if (Directory.Exists(folder))
            {
                _launcher.RevealPath(folder);
            }
        }
        catch (Exception ex)
        {
            Log(LogTarget.System, $"Could not open output folder: {ex.Message}");
        }
    }

    /// <summary>
    /// The last Save-log outcome, bound by both Reconstructor surfaces (Advanced tab + wizard) to
    /// a visible TextBlock with <c>AutomationProperties.LiveSetting=Polite</c> (4.1.3) — the log
    /// list itself is deliberately not a live region. Same contract as
    /// <see cref="OperationViewModelBase.SaveLogAnnouncement"/>.
    /// </summary>
    [ObservableProperty]
    public partial string SaveLogAnnouncement { get; set; } = string.Empty;

    [RelayCommand]
    private async Task SaveLogAsync()
    {
        // Cleared FIRST so every outcome below is a genuine empty-to-message transition: both
        // CommunityToolkit's setter and Avalonia's TextBlock.Text suppress equal-value changes,
        // so a repeat save to the same file would otherwise announce nothing. Do not simplify
        // this away.
        SaveLogAnnouncement = string.Empty;

        // The single chronological log is saved verbatim — the [P1]/[P2] tags carry the provenance the
        // old three-section stitching used to encode.
        if (LogEntries.Count == 0)
        {
            // The button is always enabled (a disabled button could not explain itself), so the
            // empty press must say why nothing happened.
            SaveLogAnnouncement = SaveLogMessages.Empty;
            return;
        }

        string? path = await _fileDialog.SaveFileAsync(
            "Save log", ".txt", ["Text Files|*.txt"], "log.txt");

        if (path is null)
        {
            return;
        }

        try
        {
            // Snapshot on the UI thread before exporting: a run may still be appending via the batched
            // drain while the exporter enumerates across awaits — writing the live collection can throw
            // "Collection was modified" mid-write and leave a partial file.
            string[] snapshot = [.. LogEntries];
            await LogExporter.SaveAsync(snapshot, path);
            Log(LogTarget.System, $"Log saved to {Path.GetFileName(path)}");
            SaveLogAnnouncement = SaveLogMessages.Saved(path);
        }
        catch (Exception ex)
        {
            Log(LogTarget.System, $"ERROR saving log: {ex.Message}");
            SaveLogAnnouncement = SaveLogMessages.Failed(ex.Message);
        }
    }

    // ── Build Options ──

    /// <summary>Captures the current RAR switch toggles for <see cref="RARCommandLineBuilder"/>.</summary>
    private RARSwitchSettings BuildSwitchSettings() => new()
    {
        Version2 = Version2,
        Version3 = Version3,
        Version4 = Version4,
        Version5 = Version5,
        Version6 = Version6,
        Version7 = Version7,
        SelectedRARVersions = SelectedLeafVersions,
        HasScannedVersions = HasScannedVersions,

        SwitchM0 = SwitchM0,
        SwitchM1 = SwitchM1,
        SwitchM2 = SwitchM2,
        SwitchM3 = SwitchM3,
        SwitchM4 = SwitchM4,
        SwitchM5 = SwitchM5,

        SwitchMA4 = SwitchMA4,
        SwitchMA5 = SwitchMA5,

        SwitchMD64K = SwitchMD64K,
        SwitchMD128K = SwitchMD128K,
        SwitchMD256K = SwitchMD256K,
        SwitchMD512K = SwitchMD512K,
        SwitchMD1024K = SwitchMD1024K,
        SwitchMD2048K = SwitchMD2048K,
        SwitchMD4096K = SwitchMD4096K,
        SwitchMD8M = SwitchMD8M,
        SwitchMD16M = SwitchMD16M,
        SwitchMD32M = SwitchMD32M,
        SwitchMD64M = SwitchMD64M,
        SwitchMD128M = SwitchMD128M,
        SwitchMD256M = SwitchMD256M,
        SwitchMD512M = SwitchMD512M,
        SwitchMD1G = SwitchMD1G,

        SwitchTSM0 = SwitchTSM0,
        SwitchTSM1 = SwitchTSM1,
        SwitchTSM2 = SwitchTSM2,
        SwitchTSM3 = SwitchTSM3,
        SwitchTSM4 = SwitchTSM4,
        SwitchTSC0 = SwitchTSC0,
        SwitchTSC1 = SwitchTSC1,
        SwitchTSC2 = SwitchTSC2,
        SwitchTSC3 = SwitchTSC3,
        SwitchTSC4 = SwitchTSC4,
        SwitchTSA0 = SwitchTSA0,
        SwitchTSA1 = SwitchTSA1,
        SwitchTSA2 = SwitchTSA2,
        SwitchTSA3 = SwitchTSA3,
        SwitchTSA4 = SwitchTSA4,

        SwitchAI = SwitchAI,
        SwitchR = SwitchR,
        SwitchDS = SwitchDS,
        SwitchS = SwitchS,
        SwitchSDash = SwitchSDash,
        SwitchMT = SwitchMT,
        SwitchMTStart = SwitchMTStart,
        SwitchMTEnd = SwitchMTEnd,

        SwitchV = SwitchV,
        VolumeSize = VolumeSize,
        VolumeSizeUnitIndex = VolumeSizeUnitIndex,
        UseOldVolumeNaming = UseOldVolumeNaming,
    };

    private static TriState ToTriState(bool? value) => value switch
    {
        true => TriState.Checked,
        false => TriState.Unchecked,
        null => TriState.Indeterminate
    };

    private void OnTimestampPreservationFailed(object? _, TimestampPreservationFailedEventArgs e)
    {
        // The library already logs a Warning via its logger (routed through OnLogMessage). Track the
        // failure here — under the lock, since this fires from engine-callback threads — so the run's
        // finally can surface a single summary MessageBox when the run finishes (#19).
        lock (_timestampFailuresLock)
        {
            _timestampFailures.Add(e);
        }
    }

    private void ShowTimestampFailureWarningIfAny()
    {
        // Snapshot under the lock, then read only the snapshot — so a concurrent add from an engine
        // thread can never corrupt the enumeration below (#19).
        TimestampPreservationFailedEventArgs[] failures;
        lock (_timestampFailuresLock)
        {
            if (_timestampFailures.Count == 0)
            {
                return;
            }

            failures = [.. _timestampFailures];
        }

        const int MaxFilesToList = 10;
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("Could not copy the source file's modification time onto the working copy " +
                      "for the following file(s):");
        sb.AppendLine();

        int shown = Math.Min(failures.Length, MaxFilesToList);
        for (int i = 0; i < shown; i++)
        {
            TimestampPreservationFailedEventArgs f = failures[i];
            sb.AppendLine($"  • {f.DestinationPath}");
            sb.AppendLine($"      ({f.ErrorMessage})");
        }

        if (failures.Length > MaxFilesToList)
        {
            sb.AppendLine($"  … and {failures.Length - MaxFilesToList} more.");
        }

        sb.AppendLine();
        sb.AppendLine("WinRAR will pack these files with the copy time instead of the original " +
                      "modification time, so the resulting RAR's File Time (DOS) may differ " +
                      "from the original release.");

        _fileDialog.ShowWarning("Timestamp Preservation Failed", sb.ToString());
    }

    /// <summary>Prepends the active <c>Set X/N · &lt;stage&gt;</c> label (if any) to a progress message (#24).</summary>
    private string ComposeProgressMessage(string baseMessage)
    {
        SetStageLabel? label = _setStageLabel;
        return label is null ? baseMessage : $"{label.Format()} | {baseMessage}";
    }

    // Engine log messages arrive on a background thread; enqueue directly (thread-safe) rather than
    // marshalling per line — the batched flush owns the UI-thread hop (#20).
    private void OnLogMessage(object? _, LogEventArgs e) => AppendLog(e.Target, e.Message);

    private void Log(LogTarget target, string message) => AppendLog(target, message);

    /// <summary>
    /// Enqueues a timestamped log line (thread-safe) stamped with the current run generation, then
    /// schedules a single batched flush. Never mutates the bound log collection directly (#20).
    /// Phase lines get their [P1]/[P2] provenance tag here, at enqueue, so the drain is a plain append.
    /// </summary>
    /// <summary>
    /// Forwards to <see cref="ReconstructionLogBuffer.Append"/>. Kept as a method on the view-model
    /// rather than replaced at its ~40 call sites, and because <c>_progress</c> is handed this method
    /// group in the constructor.
    /// </summary>
    private void AppendLog(LogTarget target, string message) => _log.Append(target, message);

    // ── Test seams (InternalsVisibleTo ReScene.App.Core.Tests) ──
    internal void ShowTimestampSummaryForTest() => ShowTimestampFailureWarningIfAny();

    internal int TimestampFailureCountForTest
    {
        get
        {
            lock (_timestampFailuresLock)
            {
                return _timestampFailures.Count;
            }
        }
    }

    internal void BeginNewLogGenerationForTest() => _log.BeginNewGeneration();


    // ── SRR Import Helpers ──

    private void SetRARVersionsFromSRR(SRRFile srr)
    {
        if (SRRImportApplier.SelectRARVersions(srr) is not { } selection)
        {
            return;
        }

        // The blanket clear stays, and stays SEPARATE from the writes below: a flag already holding
        // its final value is written false and then true again, which subscribers see as two
        // notifications.
        Version2 = Version3 = Version4 = Version5 = Version6 = Version7 = false;

        // Only the flags this branch OWNS are written; null means "leave it as the clear left it",
        // which is not the same as writing false. See RARVersionSelection's remarks.
        if (selection.Version2 is { } v2)
        {
            Version2 = v2;
        }

        if (selection.Version3 is { } v3)
        {
            Version3 = v3;
        }

        if (selection.Version4 is { } v4)
        {
            Version4 = v4;
        }

        if (selection.Version5 is { } v5)
        {
            Version5 = v5;
        }

        if (selection.Version6 is { } v6)
        {
            Version6 = v6;
        }

        if (selection.Version7 is { } v7)
        {
            Version7 = v7;
        }

        Log(LogTarget.System, selection.LogLine);
    }

    private static void SetTimestampFlags(TimestampPrecision precision,
        Action<bool> set0, Action<bool> set1, Action<bool> set2, Action<bool> set3, Action<bool> set4)
    {
        set0(precision == TimestampPrecision.NotSaved);
        set1(precision == TimestampPrecision.OneSecond);
        set2(precision == TimestampPrecision.HighPrecision1);
        set3(precision == TimestampPrecision.HighPrecision2);
        set4(precision == TimestampPrecision.NtfsPrecision);
    }

    /// <summary>
    /// Applies the partial switch diff produced by <see cref="SRRSwitchMapper"/> onto the bound
    /// option toggles, emitting the same log lines in the same order as the original inline mapping.
    /// Groups left null by the mapper (no SRR information) are skipped, so their toggles keep their
    /// current values rather than being reset.
    /// </summary>
    private void ApplySwitchDiff(SRRSwitchMapper.SwitchDiff diff)
    {
        // Compression method
        if (diff.Compression is { } compression)
        {
            int method = compression.Method;
            SwitchM0 = method == 0;
            SwitchM1 = method == 1;
            SwitchM2 = method == 2;
            SwitchM3 = method == 3;
            SwitchM4 = method == 4;
            SwitchM5 = method == 5;
            Log(LogTarget.System, $"Compression: -m{method} ({compression.LogName})");
        }

        // Dictionary size
        if (diff.Dictionary is { } dictionary)
        {
            SwitchMD64K = SwitchMD128K = SwitchMD256K = SwitchMD512K = false;
            SwitchMD1024K = SwitchMD2048K = SwitchMD4096K = false;
            SwitchMD8M = SwitchMD16M = SwitchMD32M = SwitchMD64M = false;
            SwitchMD128M = SwitchMD256M = SwitchMD512M = SwitchMD1G = false;

            switch (dictionary.Switch)
            {
                case SRRSwitchMapper.DictionarySwitch.MD64K:
                    SwitchMD64K = true;
                    break;
                case SRRSwitchMapper.DictionarySwitch.MD128K:
                    SwitchMD128K = true;
                    break;
                case SRRSwitchMapper.DictionarySwitch.MD256K:
                    SwitchMD256K = true;
                    break;
                case SRRSwitchMapper.DictionarySwitch.MD512K:
                    SwitchMD512K = true;
                    break;
                case SRRSwitchMapper.DictionarySwitch.MD1024K:
                    SwitchMD1024K = true;
                    break;
                case SRRSwitchMapper.DictionarySwitch.MD2048K:
                    SwitchMD2048K = true;
                    break;
                case SRRSwitchMapper.DictionarySwitch.MD4096K:
                    SwitchMD4096K = true;
                    break;
                case SRRSwitchMapper.DictionarySwitch.MD8M:
                    SwitchMD8M = true;
                    break;
                case SRRSwitchMapper.DictionarySwitch.MD16M:
                    SwitchMD16M = true;
                    break;
                case SRRSwitchMapper.DictionarySwitch.MD32M:
                    SwitchMD32M = true;
                    break;
                case SRRSwitchMapper.DictionarySwitch.MD64M:
                    SwitchMD64M = true;
                    break;
                case SRRSwitchMapper.DictionarySwitch.MD128M:
                    SwitchMD128M = true;
                    break;
                case SRRSwitchMapper.DictionarySwitch.MD256M:
                    SwitchMD256M = true;
                    break;
                case SRRSwitchMapper.DictionarySwitch.MD512M:
                    SwitchMD512M = true;
                    break;
                case SRRSwitchMapper.DictionarySwitch.MD1G:
                    SwitchMD1G = true;
                    break;
            }

            Log(LogTarget.System, $"Dictionary: {dictionary.SizeKb} KB");
        }

        // Solid archive
        if (diff.SwitchS is { } switchS)
        {
            SwitchS = switchS;
        }

        if (diff.SwitchSDash is { } switchSDash)
        {
            SwitchSDash = switchSDash;
        }

        if (diff.SwitchS is { } || diff.SwitchSDash is { })
        {
            Log(LogTarget.System, SwitchS ? "Solid archiving: -s" : "Solid archiving: -s-");
        }

        // Archive format
        if (diff.Format is { } format)
        {
            SwitchMA4 = format.MA4;
            SwitchMA5 = format.MA5;
            Log(LogTarget.System, format.LogLine);
        }
    }

    private void ApplyVolumeSize(long sizeBytes)
    {
        if (SRRImportApplier.SelectVolumeSize(sizeBytes) is not { } selection)
        {
            return;
        }

        SwitchV = true;
        VolumeSize = selection.Size;
        VolumeSizeUnitIndex = selection.UnitIndex;
        Log(LogTarget.System, $"Volume size: {VolumeSize} {VolumeSizeUnits[VolumeSizeUnitIndex]}");
    }

    /// <summary>
    /// The most recently PARSED verification snapshot. Exposed because the assignment happens at the
    /// parse point, before the rejections that follow it: a rejected start never reaches the runner,
    /// so this seam is what observes the snapshot it nonetheless retained. The SUCCESSFUL handoff is
    /// observed through <see cref="BuildSharedSettingsAsync"/> instead.
    /// </summary>
    internal VerificationSnapshot? VerificationSnapshotForTest => _verificationSnapshot;

    // Test seam (InternalsVisibleTo ReScene.App.Core.Tests): exposes the auto-extracted-SFV temp dir
    // so a test can assert it was retired (set back to null) without reaching for reflection.
    internal string? SfvTempDirForTest => _sfvTempDir;

    private void TryExtractStoredSFV(string srrFilePath, SRRFile srr)
    {
        // Delete the SFV temp from a previous import before starting a new one so at most one is
        // ever on disk — even when THIS import has no stored files of its own (#15), otherwise the
        // previous import's auto-extracted SFV would dangle and silently verify the wrong release.
        // If the current VerificationPath points into that dir (i.e. it was the previous import's
        // auto-extracted SFV, not a user-chosen path), clear it too so it never dangles at a file
        // we just deleted.
        if (_sfvTempDir is not null
            && VerificationPath.StartsWith(_sfvTempDir, StringComparison.Ordinal))
        {
            VerificationPath = string.Empty;
        }

        _tempDir.Cleanup(_sfvTempDir);
        _sfvTempDir = null;

        if (srr.StoredFiles.Count == 0)
        {
            return;
        }

        string? tempDir = null;
        try
        {
            tempDir = _tempDir.CreateTempDirectory();

            string? extracted = srr.ExtractStoredFile(srrFilePath, tempDir,
                fileName => Path.GetExtension(fileName).Equals(".sfv", StringComparison.OrdinalIgnoreCase));

            if (!string.IsNullOrEmpty(extracted))
            {
                _sfvTempDir = tempDir;
                VerificationPath = extracted;
                Log(LogTarget.System, $"Stored SFV extracted: {Path.GetFileName(extracted)}");
            }
            else
            {
                // Nothing extracted — don't leave the empty temp dir behind.
                _tempDir.Cleanup(tempDir);
            }
        }
        catch (Exception ex)
        {
            _tempDir.Cleanup(tempDir);
            Log(LogTarget.System, $"Failed to extract stored SFV: {ex.Message}");
        }
    }

    /// <summary>
    /// Releases the temp directory holding the last import's extracted SFV. Called on app shutdown.
    /// </summary>
    public void Cleanup()
    {
        _tempDir.Cleanup(_sfvTempDir);
        _sfvTempDir = null;
    }
}
