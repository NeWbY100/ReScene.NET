using System.Collections.Concurrent;
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

public partial class ReconstructorViewModel : ViewModelBase
{
    private const long DefaultVolumeSizeKb = 15000;

    private readonly IBruteForceService _bruteForceService;
    private readonly IFileDialogService _fileDialog;
    private readonly IAppSettingsService? _settingsService;

    // Captured once at run start from settings: whether each finished set's scratch work-root is
    // deleted (user opt-in) or kept for diagnostics (the default — per-attempt rar logs, input
    // copies and attempted archives stay inspectable under the output folder's .rescene-work).
    private bool _cleanupWorkFilesThisRun;
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
    // Log lines are enqueued (thread-safe) and applied to the bound log properties in batches on the UI
    // thread. An atomic flush flag coalesces many enqueues into at most one pending UI dispatch, and a
    // run-generation token stamped on each line lets a stale flush from a prior run discard its batch
    // rather than repopulate a log the next run already cleared.
    private readonly ConcurrentQueue<PendingLogLine> _logQueue = new();
    // Accessed only through Interlocked/Volatile helpers (not declared volatile, which would conflict
    // with passing it by ref and emit CS0420) — those calls carry the needed memory semantics.
    private int _logGeneration;
    private int _logFlushScheduled;

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
    public partial string WinRARPath { get; set; } = string.Empty;

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
        HasScannedVersions = false;
        _scanToken++;
        TriggerVersionScan();
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

    public partial class VersionEntry : ObservableObject
    {
        [ObservableProperty] public partial string VersionName { get; set; } = "";
        [ObservableProperty] public partial string Status { get; set; } = "Testing";
        [ObservableProperty] public partial string Arguments { get; set; } = "";
        [ObservableProperty] public partial string Result { get; set; } = "";

        /// <summary>Label of the archive set this test belongs to (empty for single-set releases).</summary>
        public string SetText { get; set; } = "";

        /// <summary>
        /// Directory of the WinRAR version this entry tested; the run executes the RAR console
        /// binary (see <see cref="RarExecutable"/>) inside it.
        /// </summary>
        public string VersionDirectory { get; set; } = "";

        /// <summary>
        /// Working directory of this attempt's rar invocation — the run's prepared input-files copy.
        /// Empty when unknown (e.g. Phase-1 comment-filter rows). Note it is a temp directory the run
        /// may clean up afterwards.
        /// </summary>
        public string InputDirectory { get; set; } = "";

        /// <summary>Output archive path of this attempt's rar invocation; empty when unknown.</summary>
        public string OutputFilePath { get; set; } = "";

        /// <summary>
        /// The argument string rar was ACTUALLY invoked with — the display form plus engine-added
        /// switches (-cfg-, -ds with an explicit file order, -ma4 for 5.50–6.x, -vn, -z&lt;commentfile&gt;).
        /// Empty when unknown. The runnable
        /// copied command must use this; the grid column and "Testing …" log lines keep the display
        /// form (<see cref="Arguments"/>).
        /// </summary>
        public string ExecutedArguments { get; set; } = "";

        /// <summary>
        /// This attempt's rar INPUT operand, rendered the same way as <see cref="ExecutedArguments"/> —
        /// the explicit, SRR-ordered file-list tail passed after the output path when SRR-guided
        /// assembly supplied an archived-file order. Empty when this attempt used rar's own input mask
        /// (no order available, or assembly not engaged for this set); the copied command line then
        /// falls back to the platform mask exactly as before.
        /// </summary>
        public string InputFileArguments { get; set; } = "";

        /// <summary>
        /// The quoted RAR console binary followed by the switches — the terse per-attempt form used by
        /// the "Testing …" log lines (the merged log keeps its lines short; the temp paths would
        /// repeat on every line).
        /// </summary>
        public string ExeAndArguments => string.IsNullOrEmpty(VersionDirectory)
            ? Arguments
            : $"\"{RarExecutable.ResolveIn(VersionDirectory)}\" {Arguments}";

        /// <summary>
        /// The complete command line as executed, runnable as pasted (no trailing newline — pasting
        /// must never auto-execute): a shell prefix entering the invocation's working directory
        /// (pushd on Windows so cross-drive cd works in cmd — the Windows form is cmd dialect by
        /// choice; cd on Unix, valid in every POSIX shell), the quoted RAR console binary, the
        /// switches, the quoted output archive, and either <see cref="InputFileArguments"/> (when this
        /// attempt used an explicit SRR-ordered file list) or the platform input mask — mirroring
        /// <see cref="ReScene.Core.Diagnostics.RARProcess"/>'s composition. The prefix matters: rar
        /// stores entry names relative to its working directory, so running the same switches against
        /// an absolute path would archive different names. Falls back to
        /// <see cref="ExeAndArguments"/> when the invocation details are unknown (Phase-1 rows). The
        /// input directory is the run's temp working copy and may be cleaned up after the run.
        /// </summary>
        public string FullCommandLine
        {
            get
            {
                if (string.IsNullOrEmpty(VersionDirectory))
                {
                    return Arguments;
                }

                if (InputDirectory.Length == 0 || OutputFilePath.Length == 0)
                {
                    return ExeAndArguments;
                }

                // Compose with the EXECUTED argument string (engine-added -cfg-/-ds/-ma4/-vn/-z included): the
                // display form omits switches that change the produced bytes — e.g. rar 5.50-6.x
                // defaults to RAR5 format without -ma4 — so pasting it would silently build a
                // different archive than the run this line claims to reproduce.
                string effectiveArguments = ExecutedArguments.Length > 0 ? ExecutedArguments : Arguments;
                string invocation = $"\"{RarExecutable.ResolveIn(VersionDirectory)}\" {effectiveArguments}";

                // The tail reproduces the ACTUAL input rar was given: the explicit SRR-ordered file list
                // (already whole-token quoted, like ExecutedArguments) when this attempt used one, else
                // today's platform input mask unchanged — pasting the mask on a machine whose rarfiles.lst
                // or name-sort default differs could otherwise reorder a solid set's contents.
                string tail = InputFileArguments.Length > 0
                    ? InputFileArguments
                    : OperatingSystem.IsWindows() ? ".\\*" : "'./*'";
                return OperatingSystem.IsWindows()
                    ? $"pushd \"{InputDirectory}\" && {invocation} \"{OutputFilePath}\" {tail}"
                    : $"cd \"{InputDirectory}\" && {invocation} \"{OutputFilePath}\" {tail}";
            }
        }

        // ── Timing ──
        // StartedAt is stamped when the row is created (the tracker constructs a row exactly when
        // its test begins). EndedAt is stamped once, when Status first leaves "Testing".

        /// <summary>When this test started (row construction time).</summary>
        public DateTime StartedAt { get; } = DateTime.Now;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(EndText))]
        [NotifyPropertyChangedFor(nameof(DurationText))]
        public partial DateTime? EndedAt { get; set; }

        /// <summary>Wall-clock start time, e.g. "22:13:28".</summary>
        public string StartText => StartedAt.ToString("HH:mm:ss");

        /// <summary>Wall-clock end time, or empty while the test is still running.</summary>
        public string EndText => EndedAt?.ToString("HH:mm:ss") ?? string.Empty;

        /// <summary>
        /// Elapsed test time: counts up live while the test runs, then freezes at the final duration
        /// once the row finishes. Driven once per second by <see cref="RefreshLiveDuration"/>.
        /// </summary>
        public string DurationText =>
            ReconstructorFormatting.FormatTimeSpan((EndedAt ?? DateTime.Now) - StartedAt);

        /// <summary>Raises a change for <see cref="DurationText"/> so the live value re-renders.</summary>
        public void RefreshLiveDuration() => OnPropertyChanged(nameof(DurationText));

        // Stamp the end time the moment the row leaves "Testing" (Complete / Cancelled / Error all
        // flow through this setter, set by the tracker). The null guard makes it idempotent.
        partial void OnStatusChanged(string value)
        {
            if (value != "Testing" && EndedAt is null)
            {
                EndedAt = DateTime.Now;
            }
        }
    }

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

    /// <summary>Last folder scan result, reused by import/config reconcile without re-hitting disk.</summary>
    private IReadOnlyList<InstalledRARVersion> _lastScan = [];

    /// <summary>Explicit version list from a config load, consumed by the next scanned reconcile.</summary>
    private List<int>? _pendingVersionSelection;

    /// <summary>Latest-wins guard for overlapping async scans.</summary>
    private int _scanToken;

    /// <summary>Suppresses tree→major sync while the VM is programmatically rebuilding the tree.</summary>
    private bool _suppressGroupSync;

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
    private void RescanVersions() => TriggerVersionScan();

    [RelayCommand]
    private void SelectAllVersions() => SetAllLeaves(true);

    [RelayCommand]
    private void SelectNoVersions() => SetAllLeaves(false);

    private void SetAllLeaves(bool value)
    {
        _suppressGroupSync = true;
        foreach (RARVersionGroup group in VersionGroups)
        {
            foreach (RARVersionLeaf leaf in group.Leaves)
            {
                leaf.IsChecked = value;
            }
        }

        _suppressGroupSync = false;
        SyncMajorsFromTree();
    }

    /// <summary>The most recent folder-scan Task, exposed so tests can await scan completion
    /// deterministically (production is fire-and-forget and marshals results to the UI thread).</summary>
    internal Task? LastVersionScan { get; private set; }

    /// <summary>Kicks off a folder scan: synchronous empty result for an invalid folder (keeps tests
    /// deterministic), otherwise off-thread with a latest-wins token.</summary>
    private void TriggerVersionScan()
    {
        string folder = WinRARPath;
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
        {
            // Bump the token so a still-running async scan of a previous folder cannot land later
            // and repopulate the tree (with HasScannedVersions=true) against the now-invalid path.
            _scanToken++;
            ApplyScanResult([], folderScanned: false);
            LastVersionScan = Task.CompletedTask;
            return;
        }

        LastVersionScan = RunVersionScanAsync(folder);
    }

    private async Task RunVersionScanAsync(string folder)
    {
        int token = ++_scanToken;
        IReadOnlyList<InstalledRARVersion> installed;
        try
        {
            installed = await Task.Run(() => WinRARVersionScanner.Scan(folder)).ConfigureAwait(false);
        }
        catch
        {
            installed = [];
        }

        _uiDispatcher.Invoke(() =>
        {
            if (token != _scanToken)
            {
                return;
            }

            ApplyScanResult(installed, folderScanned: installed.Count > 0 || Directory.Exists(folder));
        });
    }

    /// <summary>Stores a scan result and reconciles the tree. Also the test seam for the async scan.</summary>
    internal void ApplyScanResult(IReadOnlyList<InstalledRARVersion> installed, bool folderScanned)
    {
        _lastScan = installed;
        HasScannedVersions = folderScanned;
        ApplyReconcile();
    }

    /// <summary>Sets the pending explicit selection (config load) and reconciles against the last scan.</summary>
    internal void LoadPendingVersionSelection(IReadOnlyList<int>? explicitVersions)
    {
        _pendingVersionSelection = explicitVersions?.ToList();
        ApplyReconcile();
    }

    private void ApplyReconcile()
    {
        HashSet<int> enabledMajors = EnabledMajors();
        HashSet<int> ticked = VersionSelectionReconciler.ComputeTicked(_lastScan, _pendingVersionSelection, enabledMajors);

        // The pending explicit selection is consumed only once a real scan has materialised the tree.
        if (_pendingVersionSelection is not null && HasScannedVersions)
        {
            _pendingVersionSelection = null;
        }

        RebuildVersionGroups(_lastScan, ticked);
        SyncMajorsFromTree();
        ShowNoVersionsHint = VersionGroups.Count == 0;
    }

    private void RebuildVersionGroups(IReadOnlyList<InstalledRARVersion> installed, HashSet<int> ticked)
    {
        _suppressGroupSync = true;
        foreach (RARVersionGroup group in VersionGroups)
        {
            group.SelectionChanged -= OnGroupSelectionChanged;
            group.Detach();
        }

        VersionGroups.Clear();
        foreach (IGrouping<int, InstalledRARVersion> majorGroup in installed.GroupBy(v => v.Version / 100).OrderBy(g => g.Key))
        {
            List<RARVersionLeaf> leaves = [.. majorGroup
                .OrderBy(v => v.Version)
                .Select(v => new RARVersionLeaf(v.Version, v.FolderName, v.Tag) { IsChecked = ticked.Contains(v.Version) })];
            RARVersionGroup group = new(majorGroup.Key, leaves);
            group.SelectionChanged += OnGroupSelectionChanged;
            VersionGroups.Add(group);
        }

        _suppressGroupSync = false;
    }

    private void OnGroupSelectionChanged(object? sender, EventArgs e)
    {
        if (_suppressGroupSync)
        {
            return;
        }

        SyncMajorsFromTree();
    }

    /// <summary>Mirrors "any leaf in this major ticked" onto the coarse major bools — but only when a
    /// tree exists; with no scan the bools remain the fallback/coarse intent.</summary>
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

    // ── Compression Method ──

    [ObservableProperty] public partial bool SwitchM0 { get; set; }
    [ObservableProperty] public partial bool SwitchM1 { get; set; }
    [ObservableProperty] public partial bool SwitchM2 { get; set; }
    [ObservableProperty] public partial bool SwitchM3 { get; set; } = true;
    [ObservableProperty] public partial bool SwitchM4 { get; set; }
    [ObservableProperty] public partial bool SwitchM5 { get; set; }

    // ── Archive Format ──

    [ObservableProperty] public partial bool SwitchMA4 { get; set; }
    [ObservableProperty] public partial bool SwitchMA5 { get; set; }

    // ── Dictionary Size ──

    [ObservableProperty] public partial bool SwitchMD64K { get; set; }
    [ObservableProperty] public partial bool SwitchMD128K { get; set; }
    [ObservableProperty] public partial bool SwitchMD256K { get; set; }
    [ObservableProperty] public partial bool SwitchMD512K { get; set; }
    [ObservableProperty] public partial bool SwitchMD1024K { get; set; }
    [ObservableProperty] public partial bool SwitchMD2048K { get; set; }
    [ObservableProperty] public partial bool SwitchMD4096K { get; set; } = true;
    [ObservableProperty] public partial bool SwitchMD8M { get; set; }
    [ObservableProperty] public partial bool SwitchMD16M { get; set; }
    [ObservableProperty] public partial bool SwitchMD32M { get; set; }
    [ObservableProperty] public partial bool SwitchMD64M { get; set; }
    [ObservableProperty] public partial bool SwitchMD128M { get; set; }
    [ObservableProperty] public partial bool SwitchMD256M { get; set; }
    [ObservableProperty] public partial bool SwitchMD512M { get; set; }
    [ObservableProperty] public partial bool SwitchMD1G { get; set; }

    // ── Timestamps ──

    [ObservableProperty] public partial bool SwitchTSM0 { get; set; }
    [ObservableProperty] public partial bool SwitchTSM1 { get; set; }
    [ObservableProperty] public partial bool SwitchTSM2 { get; set; }
    [ObservableProperty] public partial bool SwitchTSM3 { get; set; }
    [ObservableProperty] public partial bool SwitchTSM4 { get; set; }

    [ObservableProperty] public partial bool SwitchTSC0 { get; set; }
    [ObservableProperty] public partial bool SwitchTSC1 { get; set; }
    [ObservableProperty] public partial bool SwitchTSC2 { get; set; }
    [ObservableProperty] public partial bool SwitchTSC3 { get; set; }
    [ObservableProperty] public partial bool SwitchTSC4 { get; set; }

    [ObservableProperty] public partial bool SwitchTSA0 { get; set; }
    [ObservableProperty] public partial bool SwitchTSA1 { get; set; }
    [ObservableProperty] public partial bool SwitchTSA2 { get; set; }
    [ObservableProperty] public partial bool SwitchTSA3 { get; set; }
    [ObservableProperty] public partial bool SwitchTSA4 { get; set; }

    // ── Other Options ──

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsFileAttributesEnabled))]
    public partial bool SwitchAI { get; set; }

    [ObservableProperty] public partial bool SwitchR { get; set; } = true;
    [ObservableProperty] public partial bool SwitchDS { get; set; }
    [ObservableProperty] public partial bool SwitchS { get; set; }
    [ObservableProperty] public partial bool SwitchSDash { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsMTRangeEnabled))]
    public partial bool SwitchMT { get; set; }

    [ObservableProperty] public partial int SwitchMTStart { get; set; } = 1;
    [ObservableProperty] public partial int SwitchMTEnd { get; set; } = Environment.ProcessorCount;

    // Volume
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsVolumeOptionsEnabled))]
    public partial bool SwitchV { get; set; }

    [ObservableProperty] public partial string VolumeSize { get; set; } = DefaultVolumeSizeKb.ToString();
    [ObservableProperty] public partial int VolumeSizeUnitIndex { get; set; } = 1; // default KB
    [ObservableProperty] public partial bool UseOldVolumeNaming { get; set; }

    public static string[] VolumeSizeUnits { get; } = ["Bytes", "KB", "MB", "GB", "KiB", "MiB", "GiB"];

    // File attributes (null = Indeterminate)
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSwitchAIEnabled))]
    public partial bool? FileA { get; set; } = false;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSwitchAIEnabled))]
    public partial bool? FileI { get; set; } = false;

    // Output options
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDeleteDuplicateCRCEnabled))]
    public partial bool DeleteRARFiles { get; set; }

    [ObservableProperty] public partial bool DeleteDuplicateCRCFiles { get; set; } = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsRenameEnabled))]
    public partial bool StopOnFirstMatch { get; set; } = true;

    [ObservableProperty] public partial bool CompleteAllVolumes { get; set; }
    [ObservableProperty] public partial bool RenameToReleaseNames { get; set; } = true;

    /// <summary>
    /// The rename option requires Stop-after-first-match, so when it is turned off it is cleared
    /// (not left checked-but-greyed). It cannot be turned on while it is off — the sub-item is
    /// disabled — so no reverse coupling is needed.
    /// </summary>
    partial void OnStopOnFirstMatchChanged(bool value)
    {
        if (!value)
        {
            RenameToReleaseNames = false;
        }
    }

    partial void OnSwitchSChanged(bool value)
    {
        if (value)
        {
            SwitchSDash = false;
        }
    }

    partial void OnSwitchSDashChanged(bool value)
    {
        if (value)
        {
            SwitchS = false;
        }
    }

    /// <summary>
    /// Clamps to 0..64 (the highest thread count any WinRAR version accepts) so an unbounded or
    /// pasted-in value (e.g. int.MaxValue) can never reach <see cref="RARCommandLineBuilder"/>.
    /// The builder itself re-clamps defensively, but catching it here gives immediate UI feedback.
    /// </summary>
    partial void OnSwitchMTStartChanged(int value)
    {
        int clamped = Math.Clamp(value, 0, RARCommandLineBuilder.MaxThreadCount);
        if (clamped != value)
        {
            SwitchMTStart = clamped;
        }
    }

    partial void OnSwitchMTEndChanged(int value)
    {
        int clamped = Math.Clamp(value, 0, RARCommandLineBuilder.MaxThreadCount);
        if (clamped != value)
        {
            SwitchMTEnd = clamped;
        }
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
            _pendingVersionSelection = null;
            ApplyReconcile();

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

        // ── Path validation ──

        if (string.IsNullOrWhiteSpace(WinRARPath))
        {
            Log(LogTarget.System, "Invalid WinRAR directory.");
            _fileDialog.ShowError("Validation Error", "Invalid WinRAR directory.");
            return;
        }

        if (!Directory.Exists(WinRARPath))
        {
            Log(LogTarget.System, "WinRAR directory does not exist.");
            _fileDialog.ShowError("Validation Error", "WinRAR directory does not exist.");
            return;
        }

        // A real scan that found zero valid version subfolders — block with a clear message so the
        // user knows to add a version subfolder. The no-scan fallback (HasScannedVersions == false)
        // still uses the broad major-version range and must not be blocked here.
        if (HasScannedVersions && VersionGroups.Count == 0)
        {
            Log(LogTarget.System, "No WinRAR versions found in the selected folder.");
            _fileDialog.ShowError("Validation Error",
                $"No WinRAR versions were found in the WinRAR versions folder. Add a version subfolder containing {RarExecutable.FileName}, then click Rescan.");
            return;
        }

        // A materialised tree with nothing ticked would brute-force zero versions — block it with a
        // clear message. The no-scan case (empty tree) is unaffected and uses the broad fallback.
        if (VersionGroups.Count > 0 && VersionGroups.SelectMany(g => g.Leaves).All(l => !l.IsChecked))
        {
            Log(LogTarget.System, "No WinRAR versions selected.");
            _fileDialog.ShowError("Validation Error", "Select at least one WinRAR version.");
            return;
        }

        if (string.IsNullOrWhiteSpace(ReleasePath))
        {
            Log(LogTarget.System, "Invalid release directory.");
            _fileDialog.ShowError("Validation Error", "Invalid release directory.");
            return;
        }

        if (!Directory.Exists(ReleasePath))
        {
            Log(LogTarget.System, "Release directory does not exist.");
            _fileDialog.ShowError("Validation Error", "Release directory does not exist.");
            return;
        }

        if (string.IsNullOrWhiteSpace(OutputPath))
        {
            Log(LogTarget.System, "Invalid output directory.");
            _fileDialog.ShowError("Validation Error", "Invalid output directory.");
            return;
        }

        // ── Plan before mutate ──
        //
        // Make every reject-the-run decision (multi-set custom packer, reserved-root distinctness,
        // live-input overlap, and — with no archive file list — release/output self-inclusion) BEFORE
        // the destructive output cleanup below and before any confirm dialog, so an already-known
        // unsupported run never erases existing output (#3, #1, #17).
        if (EvaluateRunPreflight() is { } rejection)
        {
            Log(LogTarget.System, $"Cannot start: {rejection}");
            _fileDialog.ShowError("Validation Error", rejection);
            return;
        }

        // ── Subdirectory timestamp warning ──

        bool releaseHasSubdirectories;
        try
        {
            releaseHasSubdirectories = Directory.EnumerateDirectories(ReleasePath).Any();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log(LogTarget.System, $"Could not inspect the release directory: {ex.Message}");
            _fileDialog.ShowError("Validation Error", $"Could not inspect the release directory:\n{ex.Message}");
            return;
        }

        if (releaseHasSubdirectories && _import.DirTimestamps.Count == 0)
        {
            bool proceed = subdirTimestampsConfirmed || await _fileDialog.ShowConfirmAsync("Warning: modified date",
                SubdirTimestampWarningText);
            if (!proceed)
            {
                Log(LogTarget.System, "Cancelled: subdirectory timestamp warning.");
                return;
            }
        }

        // ── Verification file validation ──
        //
        // Parsed once, here, into an immutable snapshot — BEFORE the output-directory cleanup below
        // (which deletes the file if it happens to sit inside OutputPath) and before any per-set
        // work-dir cleanup. Every downstream verification read (per-set CRCs, first-volume gate
        // hashes, flat-set fallback names) draws from this snapshot; the file itself is never
        // re-read after this point (#14).

        if (string.IsNullOrWhiteSpace(VerificationPath))
        {
            Log(LogTarget.System, "Invalid verification file path.");
            _fileDialog.ShowError("Validation Error", "Invalid verification file path.");
            return;
        }

        if (!File.Exists(VerificationPath))
        {
            Log(LogTarget.System, "Verification file does not exist.");
            _fileDialog.ShowError("Validation Error", "Verification file does not exist.");
            return;
        }

        string verificationExt = Path.GetExtension(VerificationPath).ToLowerInvariant();
        if (verificationExt is not ".sfv" and not ".sha1")
        {
            Log(LogTarget.System, "Invalid verification file type.");
            _fileDialog.ShowError("Validation Error", "Invalid verification file type. Use .sfv or .sha1 files.");
            return;
        }

        VerificationSnapshot snapshot;
        try
        {
            snapshot = VerificationSnapshot.Load(VerificationPath);
        }
        catch (Exception ex)
        {
            Log(LogTarget.System, $"Failed to parse verification file: {ex.Message}");
            _fileDialog.ShowError("Validation Error", $"Failed to parse verification file:\n{ex.Message}");
            return;
        }

        if (snapshot.Entries.Count == 0)
        {
            Log(LogTarget.System, "No hashes found in verification file.");
            _fileDialog.ShowError("Validation Error", "No hashes found in verification file.");
            return;
        }

        _verificationSnapshot = snapshot;

        // ── Input file existence check ──
        //
        // The verify file (.sfv/.sha1) lists the OUTPUT archives we're trying to produce,
        // so it isn't useful as an input check. The imported SRR's archived files ARE the
        // expected input contents — verify those exist in the release directory. If no SRR
        // has been imported, skip this pre-flight; Manager.ValidateInputFiles will run later.
        if (_import.ArchiveFiles.Count > 0)
        {
            try
            {
                var missingFiles = new List<string>();
                foreach (string archiveFile in _import.ArchiveFiles)
                {
                    string fullPath = Path.Combine(ReleasePath, archiveFile);
                    if (!File.Exists(fullPath))
                    {
                        missingFiles.Add(archiveFile);
                    }
                }

                if (missingFiles.Count > 0)
                {
                    string fileList = string.Join("\n", missingFiles);
                    Log(LogTarget.System, $"Missing {missingFiles.Count} input file(s) in release directory.");
                    _fileDialog.ShowWarning(
                        "Missing Input Files",
                        $"The following {missingFiles.Count} file(s) listed in the imported SRR are missing from the release directory:\n\n{fileList}\n\nThe release directory should contain the unpacked archive contents (the files that originally went into the RARs).");
                    return;
                }
            }
            catch (Exception ex)
            {
                Log(LogTarget.System, $"Failed to validate input files: {ex.Message}");
            }
        }

        // ── Output directory validation & cleanup ──
        //
        // Reconstruction only ever writes into (and only ever clears) the two reserved subtrees under
        // OutputPath — the final `output` tree and the `.rescene-work` scratch tree. Unrelated files at
        // the OutputPath root are preserved (#4).

        if (!Directory.Exists(OutputPath))
        {
            try
            {
                Directory.CreateDirectory(OutputPath);
                Log(LogTarget.System, $"Created output directory: {OutputPath}");
            }
            catch (Exception ex)
            {
                Log(LogTarget.System, $"Failed to create output directory: {ex.Message}");
                _fileDialog.ShowError("Validation Error", $"Failed to create output directory:\n{ex.Message}");
                return;
            }
        }
        else if (OutputHasReconstructionArtifacts())
        {
            bool proceed = outputNotEmptyConfirmed || await _fileDialog.ShowConfirmAsync("Output Directory Not Empty",
                OutputCleanupConfirmText(OutputPath));
            if (!proceed)
            {
                Log(LogTarget.System, "Cancelled: output directory not empty.");
                return;
            }

            if (!ClearReservedSubtrees())
            {
                return;
            }
        }

        // ── Start brute-force ──

        IsRunning = true;
        LastRunSucceeded = false;
        ShowProgress = true;
        ProgressPercent = 0;
        ProgressMessage = "Starting...";
        BeginNewLogGeneration();
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

            await RunArchiveSetsAsync(token);

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
            DrainLogQueue();
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

    internal Task RunArchiveSetsForTestAsync(CancellationToken token) => RunArchiveSetsAsync(token);

    private async Task RunArchiveSetsAsync(CancellationToken token)
    {
        // Await any in-flight version scan (e.g. a manual Rescan whose Task hasn't landed yet) BEFORE
        // capturing the shared settings below — RescanVersions does not clear HasScannedVersions for a
        // valid folder, so without this a still-running rescan's stale _lastScan could be captured
        // even though HasScannedVersions correctly reads true.
        await (LastVersionScan ?? Task.CompletedTask);

        // Run-scoped capture: a mid-run settings save must not flip cleanup behaviour between sets.
        _cleanupWorkFilesThisRun = _settingsService?.Load().CleanupReconstructionWorkFiles ?? false;

        SharedReconstructionSettings shared = await BuildSharedSettingsAsync(token);

        // For the legacy / no-SRR single flat set the original RAR names may be empty; fall back to
        // the verification snapshot's RAR-volume entries so output renaming still works (matches the
        // old ResolveOutputRenameNames behaviour). When an SRR was imported its names take precedence.
        IReadOnlyList<string> flatNames = _import.OriginalRARFileNames.Count > 0
            ? _import.OriginalRARFileNames
            : shared.Verification.VolumeNames;

        IReadOnlyList<SRRArchiveSet> sets = ArchiveSetPlanner.ResolveSets(
            _import.ArchiveSets, _import.SRRFilePath, flatNames, _import.ArchiveFiles);

        var outcomes = new List<SetOutcome>();
        WinningCombo? seed = null;

        if (sets.Count > 1)
        {
            Log(LogTarget.System, $"Reconstructing {sets.Count} archive sets independently.");
        }

        for (int i = 0; i < sets.Count; i++)
        {
            SRRArchiveSet set = sets[i];
            string label = string.IsNullOrEmpty(set.Key) ? "(release)" : set.Key;
            if (sets.Count > 1)
            {
                Log(LogTarget.System, $"=== Set {i + 1}/{sets.Count}: {label} ===");
            }

            byte[]? embedded = LoadEmbeddedSfvBytes(set);
            Dictionary<string, string> expected = ArchiveSetPlanner.BuildExpectedVolumeCrcs(set, embedded, shared.Verification);

            // Full-volume verification needs a per-volume CRC for every volume; without them we
            // cannot honestly verify the set, so skip it rather than report a false success.
            // Note: SHA1 runs (no per-volume CRC source) and zero-coverage cases are NOT skipped —
            // the engine still runs and gates on the first-volume hash. Only partial CRC32 coverage
            // (some volumes have CRCs but not all) is an honest skip.
            if (ArchiveSetPlanner.ShouldSkipUnverifiableSet(shared.CompleteAllVolumes, shared.HashType, expected.Count, set.VolumeNames.Count))
            {
                Log(LogTarget.System, $"Set {label}: no per-volume CRCs to verify; supply its .sfv. Skipping.");
                outcomes.Add(new SetOutcome(set, label, Success: false, Skipped: true));
                continue;
            }

            // The work-root path is resolved before the per-set try (it never depends on the set's own
            // command/version matrix). Its own guarded resolution can throw a path-resolution error
            // (e.g. a keyed set's scratch child real-resolves through an un-inspectable or junction-
            // redirected reserved root): keep that scoped to THIS set — the loop records a failing set
            // and continues — instead of letting it abort every remaining set. The `continue` runs
            // BEFORE the outer try/finally below, so the finally never sees (nor tries to clean) an
            // uncomputed work root. A per-set matrix failure (#6 — no selected WinRAR version can
            // produce this set's format) is likewise raised INSIDE the try and handled there.
            string workRoot;
            try
            {
                workRoot = ArchiveSetPlanner.WorkRootFor(shared, set);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                Log(LogTarget.System, $"Set {label} failed: {ex.Message}");
                _progress.CompleteActiveVersion("No Match");
                outcomes.Add(new SetOutcome(set, label, Success: false, Skipped: false));
                continue;
            }

            bool committed = false;
            bool preserveScratch = false;
            try
            {
                BruteForceRunResult result;
                try
                {
                    // Build this set's own per-set command/version matrix off the UI thread (#6) —
                    // it can rebuild the full cartesian matrix via RARCommandLineBuilder, matching how
                    // BuildSharedSettingsAsync already offloads the global build.
                    BruteForceOptions options = await Task.Run(
                        () => ArchiveSetPlanner.BuildOptionsForSet(set, shared, expected, token), token);

                    // Tell the progress tracker which set is active so new rows are stamped with the label.
                    _progress.SetActiveSet(sets.Count > 1 ? label : string.Empty);

                    result = await RunSingleSetAsync(label, options, seed, i + 1, sets.Count, token);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    // A set's own failure (e.g. an InvalidDataException from input-CRC validation, or
                    // an InvalidOperationException from an unsatisfiable per-set format/version
                    // requirement) must not abort the whole run — record it and move on to the next set.
                    Log(LogTarget.System, $"Set {label} failed: {ex.Message}");
                    // Finalize THIS set's own row now, from its own outcome (#23) — a later set's
                    // progress events must never be the ones that decide whether this row reads Match.
                    _progress.CompleteActiveVersion("No Match");
                    outcomes.Add(new SetOutcome(set, label, Success: false, Skipped: false));
                    continue;
                }

                if (token.IsCancellationRequested)
                {
                    break;
                }

                if (!result.Success)
                {
                    _progress.CompleteActiveVersion("No Match");
                    outcomes.Add(new SetOutcome(set, label, Success: false, Skipped: false));
                    continue;
                }

                seed ??= result.Combo;

                // Relocate the verified volumes out of the guarded scratch work-root into the real
                // output tree. Only a successful relocation counts as a committed set; a relocation
                // failure whose rollback could not complete preserves the scratch for recovery.
                (bool relocated, preserveScratch) = RelocateVerifiedOutput(workRoot, set, sets.Count, result);
                committed = relocated;
                _progress.CompleteActiveVersion(relocated ? "Match" : "No Match");
                outcomes.Add(new SetOutcome(set, label, relocated, Skipped: false));
            }
            finally
            {
                // A committed set's scratch was already handled by the relocation (cleared or kept per
                // the work-files setting); a set whose rollback could not complete keeps its scratch
                // (recoverable output). Everything else — a failed, errored, or cancelled set — goes
                // through the same setting-gated CleanupWorkRoot here.
                if (!committed && !preserveScratch)
                {
                    CleanupWorkRoot(workRoot, set);
                }
            }
        }

        ReportSetSummary(outcomes, sets.Count, token.IsCancellationRequested);
    }

    /// <summary>
    /// Runs one set's brute force. For later sets a captured winning combo is tried first (seeding);
    /// only if it fails (and the run was not cancelled) is the full option matrix run. Returns the full
    /// run result (success, winning combo for seeding, and the committed/custom-packer file paths the
    /// relocation moves out of the scratch work-root).
    /// </summary>
    private async Task<BruteForceRunResult> RunSingleSetAsync(
        string label, BruteForceOptions options, WinningCombo? seed, int setIndex, int setCount, CancellationToken token)
    {
        BruteForceRunResult result;
        if (seed is not null && setCount > 1)
        {
            // Label this set's progress as the seeded attempt so its high-% progress and the full
            // attempt's fresh low-% progress read as distinct stages, not a rewind within the set (#24).
            _setStageLabel = new SetStageLabel(setIndex, setCount, "seed");
            BruteForceOptions narrowed = ArchiveSetPlanner.NarrowToCombo(options, seed);
            result = await Task.Run(() => _bruteForceService.RunAsync(narrowed, token), token);
            if (!result.Success && !token.IsCancellationRequested)
            {
                Log(LogTarget.System, $"Seed combo did not reproduce {label}; running full search.");
                _setStageLabel = new SetStageLabel(setIndex, setCount, "full");
                result = await Task.Run(() => _bruteForceService.RunAsync(options, token), token);
            }
        }
        else
        {
            _setStageLabel = new SetStageLabel(setIndex, setCount, "full");
            result = await Task.Run(() => _bruteForceService.RunAsync(options, token), token);
        }

        return result;
    }

    /// <summary>One archive set's reconstruction outcome.</summary>
    private readonly record struct SetOutcome(SRRArchiveSet Set, string Label, bool Success, bool Skipped);

    /// <summary>
    /// Captures the non-per-set toggles, version ranges, command-line matrix, and release-wide SRR
    /// data. The matrix build is bounded (checked cardinality cap) but can still be tens of
    /// thousands of iterations, so it runs off the UI thread via <see cref="Task.Run{TResult}(Func{TResult}, CancellationToken)"/>.
    /// </summary>
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
            InstalledVersions = HasScannedVersions ? [.. _lastScan] : [],
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
    /// Reads the embedded SFV bytes for a set from the imported SRR's stored files. For a single
    /// flat set (empty key) any stored .sfv matches. Otherwise a stored .sfv matches this set when
    /// either its archive-set key equals the set key (handles directory-prefixed stored names such
    /// as "DVD1\aln-re4a.sfv" → key "DVD1/aln-re4a"), OR its base name equals the set's base name
    /// (handles a flat "aln-re4a.sfv" matched to key "DVD1/aln-re4a"). Returns null when no SRR
    /// was imported or no stored .sfv matches.
    /// </summary>
    private byte[]? LoadEmbeddedSfvBytes(SRRArchiveSet set)
    {
        string? srrPath = _import.SRRFilePath;
        if (string.IsNullOrWhiteSpace(srrPath) || !File.Exists(srrPath))
        {
            return null;
        }

        try
        {
            var srr = SRRFile.Load(srrPath);
            return srr.ReadStoredFile(srrPath, name => EmbeddedSfvMatchesSet(name, set));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            Log(LogTarget.System, $"Could not read embedded SFV for {set.Key}: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Whether a stored file is the .sfv for the given set. See <see cref="LoadEmbeddedSfvBytes"/>
    /// for the matching rules. Shared with the embedded-SFV resolution test so both use one predicate.
    /// </summary>
    internal static bool EmbeddedSfvMatchesSet(string storedName, SRRArchiveSet set)
    {
        if (!storedName.EndsWith(".sfv", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        // Single flat set: any stored .sfv is its SFV.
        if (string.IsNullOrEmpty(set.Key))
        {
            return true;
        }

        // Key match: handles a directory-prefixed stored name (e.g. "DVD1\aln-re4a.sfv").
        if (RARVolumeIdentifier.GetArchiveSetKey(storedName).Equals(set.Key, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // Base-name match: handles a flat stored name (e.g. "aln-re4a.sfv") whose set key carries a
        // directory prefix. The set's base name is the last '/'-segment of its key.
        string setBaseName = set.Key[(set.Key.LastIndexOf('/') + 1)..];
        string storedBaseName = Path.GetFileNameWithoutExtension(storedName);
        return storedBaseName.Equals(setBaseName, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Relocates a set's verified volumes out of its guarded scratch work-root into the real output
    /// tree (<c>OutputPath\output\…</c>) via <see cref="VerifiedOutputRelocator"/>, then clears the
    /// now-emptied scratch — or keeps it, per the work-files setting (see
    /// <see cref="CleanupWorkRoot"/>). The legacy single-root set (empty key, work dir == OutputPath) is a no-op:
    /// its output already sits at <c>OutputPath\output\</c>, byte-identical to before.
    /// </summary>
    /// <returns>
    /// <c>Relocated</c> is true when the verified volumes reached their final location (or for the
    /// legacy no-op set); <c>ScratchPreserved</c> is true when a failed relocation could not fully roll
    /// back, so the caller must NOT delete the scratch work-root (recoverable output still lives there).
    /// </returns>
    private (bool Relocated, bool ScratchPreserved) RelocateVerifiedOutput(
        string workRoot, SRRArchiveSet set, int setCount, BruteForceRunResult result)
    {
        // Legacy single-root set: its brute-force output is already at OutputPath\output — nothing to move.
        if (string.IsNullOrEmpty(set.Key))
        {
            return (true, false);
        }

        bool custom = result.CustomPackerFiles.Count > 0;
        VerifiedOutputRelocator.Branch branch = custom
            ? VerifiedOutputRelocator.Branch.CustomPacker
            : VerifiedOutputRelocator.Branch.BruteForce;
        IReadOnlyList<string> files = custom
            ? result.CustomPackerFiles
            : (result.Matches.Count > 0 ? result.Matches[0].Files : []);

        VerifiedOutputRelocator.RelocationOutcome outcome = VerifiedOutputRelocator.Relocate(
            OutputPath, workRoot, set, setCount, branch, CompleteAllVolumes, files, _fileMover,
            message => Log(LogTarget.System, message));

        if (outcome.Success)
        {
            CleanupWorkRoot(workRoot, set); // clear or keep the now-emptied scratch per the work-files setting
            return (true, false);
        }

        return (false, outcome.ScratchPreserved);
    }

    /// <summary>
    /// Removes a set's guarded scratch work-root (a strict descendant of the reserved
    /// <c>.rescene-work</c> tree) — but only when the user opted into clearing work files
    /// (<see cref="AppSettings.CleanupReconstructionWorkFiles"/>, captured at run start); by default
    /// the work-root is KEPT for diagnostics and its path is logged. No-op for the legacy single-root
    /// set (empty key) whose work dir is <c>OutputPath</c> itself, and for a work-root a junction
    /// would redirect outside the reserved scratch tree (fail-closed).
    /// </summary>
    private void CleanupWorkRoot(string workRoot, SRRArchiveSet set)
    {
        if (string.IsNullOrEmpty(set.Key))
        {
            return;
        }

        if (!_cleanupWorkFilesThisRun)
        {
            // Only log a path that actually exists: a set can fail before its scratch is ever created
            // (e.g. an unsatisfiable per-set version requirement throws in BuildOptionsForSet), and
            // pointing the user at a non-existent diagnostics folder would mislead.
            if (Directory.Exists(workRoot))
            {
                Log(LogTarget.System, $"Work files kept: {workRoot}");
            }

            return;
        }

        try
        {
            string scratchRoot = ReconstructionPathGuard.ResolveScratchRoot(OutputPath);
            if (Directory.Exists(workRoot) && ReconstructionPathGuard.IsStrictDescendant(scratchRoot, workRoot))
            {
                Directory.Delete(workRoot, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            Log(LogTarget.System, $"Failed to clean up work dir for {set.Key}: {ex.Message}");
        }
    }

    /// <summary>
    /// The confirmation shown before the pre-run cleanup: it clears only the two reserved subtrees
    /// (<c>output</c> and <c>.rescene-work</c>) under <paramref name="outputPath"/>, preserving unrelated
    /// root files. Shared verbatim by the Start command and the Beginner wizard so the two never drift.
    /// </summary>
    public static string OutputCleanupConfirmText(string outputPath) =>
        $"The output directory already contains reconstruction output:\n\n{outputPath}\n\n" +
        $"Its '{ReconstructionPathGuard.OutputDirName}' and '{ReconstructionPathGuard.ScratchDirName}' subfolders " +
        "— including any kept work files — will be cleared before starting (other files are left untouched). Continue?";

    /// <summary>
    /// Whether either reserved subtree under <c>OutputPath</c> currently holds content the pre-run
    /// cleanup would clear. Shared by Start and the Beginner wizard so both prompt on the same
    /// condition. Fails closed (returns true → prompt) if the roots cannot be resolved.
    /// </summary>
    public bool OutputHasReconstructionArtifacts()
    {
        try
        {
            (string outputRoot, string scratchRoot) = ReconstructionPathGuard.ResolveReservedRoots(OutputPath);
            return HasContent(outputRoot) || HasContent(scratchRoot);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return true;
        }

        static bool HasContent(string dir) => Directory.Exists(dir) && Directory.EnumerateFileSystemEntries(dir).Any();
    }

    /// <summary>
    /// Clears the two reserved subtrees (<c>output</c> + <c>.rescene-work</c>) under <c>OutputPath</c>,
    /// resolved through the path guard so a junction cannot redirect the delete. Unrelated files at the
    /// OutputPath root are untouched (#4). Returns false (after surfacing the error) if the delete fails.
    /// </summary>
    internal bool ClearReservedSubtrees()
    {
        try
        {
            (string outputRoot, string scratchRoot) = ReconstructionPathGuard.ResolveReservedRoots(OutputPath);
            DeleteIfExists(outputRoot);
            DeleteIfExists(scratchRoot);
            Log(LogTarget.System, "Output directory cleaned.");
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            Log(LogTarget.System, $"Failed to clean output directory: {ex.Message}");
            _fileDialog.ShowError("Error", $"Failed to clean output directory:\n{ex.Message}");
            return false;
        }

        static void DeleteIfExists(string dir)
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
    }

    /// <summary>
    /// Logs a per-set pass/fail/skip/cancelled summary and sets the overall progress message and
    /// <see cref="LastRunSucceeded"/>. Overall success requires every set to have passed with none
    /// skipped and no cancellation.
    /// </summary>
    private void ReportSetSummary(IReadOnlyList<SetOutcome> outcomes, int totalSets, bool cancelled)
    {
        bool multi = totalSets > 1;

        if (multi)
        {
            Log(LogTarget.System, "=== Reconstruction summary ===");
            foreach (SetOutcome o in outcomes)
            {
                string mark = o.Skipped ? "skipped" : o.Success ? "OK" : "failed";
                Log(LogTarget.System, $"  [{mark}] {o.Label}");
            }

            int notAttempted = totalSets - outcomes.Count;
            if (notAttempted > 0)
            {
                Log(LogTarget.System, $"  [not attempted] {notAttempted} set(s)");
            }
        }

        if (cancelled)
        {
            // The outer cancellation handler owns the final version-row status and progress message.
            return;
        }

        ProgressPercent = 100;
        ProgressPercentText = "100%";
        if (_progress.LastOperationSize > 0)
        {
            TestCountText = $"Test {_progress.LastOperationSize:N0} of {_progress.LastOperationSize:N0}";
        }

        // Each set's own row was already finalized from its own outcome at set completion (#23) — no
        // per-row relabeling here. This method only owns the run-wide aggregate below.
        bool attemptedAll = outcomes.Count == totalSets;
        bool allOk = attemptedAll && outcomes.All(o => o is { Success: true, Skipped: false });

        // Surface the count of combinations the engine could not run (e.g. a rar binary without the
        // execute bit) in the completion heading — a run-wide "existence of errors" aggregate (WCAG
        // 4.1.3) that a blind user would otherwise have to hunt cell-by-cell, and that gives sighted
        // users an at-a-glance signal too. The heading is a Polite live region, so this announces once
        // at completion.
        int errorCount = VersionEntries.Count(v => v.Status == "Error");
        string errorSuffix = errorCount > 0 ? $" ({errorCount} could not run)" : string.Empty;

        ProgressMessage = allOk ? "Match found!" : "No match found.";
        PhaseDescription = (allOk ? "Complete — Match Found!" : "Complete — No Match") + errorSuffix;
        LastRunSucceeded = allOk;
        Log(LogTarget.System, allOk
            ? "Brute-force completed: all sets matched!"
            : "Brute-force completed: not all sets matched.");

        // Existence-of-errors aggregate: the scannable "did anything fail?" marker at the end of the
        // log, matching the completion heading's "(N could not run)". The per-failure [P2] WARNINGs sit
        // earlier in the same merged log, so the line points up rather than at a separate pane.
        if (errorCount > 0)
        {
            Log(LogTarget.System,
                $"{errorCount} combination(s) could not run — each failure is logged above.");
        }
    }

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

    // ── Event Handlers ──

    private void OnFileCopyProgress(object? _, FileCopyProgressEventArgs e)
    {
        _uiDispatcher.Post(() =>
        {
            // A queued progress event can arrive after a cancelled run already cleaned up;
            // re-raising IsCopying then would re-open (and strand) the copy progress window.
            if (!IsRunning)
            {
                return;
            }

            if (!IsCopying)
            {
                IsCopying = true;
                _progress.StartCopy();
            }

            CopyProgressUpdate u = _progress.ApplyCopyProgress(e);
            CopyHeadingText = u.HeadingText;
            CopySourceText = u.SourceText;
            CopyDestText = u.DestText;
            CopyProgressPercent = u.ProgressPercent;
            CopyProgressPercentText = u.ProgressPercentText;
            CopyCurrentFileText = u.CurrentFileText;
            CopyRemainingText = u.RemainingText;
            CopyElapsedText = u.ElapsedText;
            if (u.HasSpeed)
            {
                CopySpeedText = u.SpeedText;
                if (u.HasEta)
                {
                    CopyTimeRemainingText = u.TimeRemainingText;
                    CopyEtaText = u.EtaText;
                }
            }

            if (u.IsComplete)
            {
                IsCopying = false;
            }
        });
    }

    private void OnCRCValidationProgress(object? _, CRCValidationProgressEventArgs e)
    {
        _uiDispatcher.Post(() =>
        {
            // A queued progress event can arrive after a cancelled run already cleaned up;
            // re-raising IsVerifying then would re-open (and strand) the CRC progress window.
            if (!IsRunning)
            {
                return;
            }

            if (!IsVerifying)
            {
                IsVerifying = true;
                _progress.StartVerify();
            }

            VerifyProgressUpdate u = _progress.ApplyVerifyProgress(e);
            VerifyHeadingText = u.HeadingText;
            VerifyProgressPercent = u.ProgressPercent;
            VerifyProgressPercentText = u.ProgressPercentText;
            VerifyCurrentFileText = u.CurrentFileText;
            VerifyRemainingText = u.RemainingText;
            VerifyElapsedText = u.ElapsedText;
            if (u.HasSpeed)
            {
                VerifySpeedText = u.SpeedText;
                if (u.HasEta)
                {
                    VerifyTimeRemainingText = u.TimeRemainingText;
                    VerifyEtaText = u.EtaText;
                }
            }

            if (u.IsComplete)
            {
                IsVerifying = false;
            }
        });
    }

    private void OnProgress(object? _, BruteForceProgressEventArgs e)
    {
        _uiDispatcher.Invoke(() =>
        {
            BruteForceProgressUpdate u = _progress.ApplyProgress(e);

            ProgressPercent = u.ProgressPercent;
            PhaseDescription = u.PhaseDescription;
            ProgressMessage = ComposeProgressMessage(u.ProgressMessage);
            TestCountText = u.TestCountText;
            ProgressPercentText = u.ProgressPercentText;
            CurrentDetailText = u.CurrentDetailText;
            ElapsedText = u.ElapsedText;
            if (u.HasTiming)
            {
                RemainingText = u.RemainingText;
                SpeedText = u.SpeedText;
                EtaText = u.EtaText;
            }
        });
    }

    private void OnElapsedTimerTick()
    {
        ElapsedTick tick = _progress.Tick();
        ElapsedText = tick.ElapsedText;

        if (tick.HasTiming)
        {
            RemainingText = tick.RemainingText;
            EtaText = tick.EtaText;
        }

        if (VersionEntries.Count > 0)
        {
            VersionEntries[^1].RefreshLiveDuration();
        }
    }

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
    private void AppendLog(LogTarget target, string message)
    {
        string tag = target switch
        {
            LogTarget.Phase1 => "[P1] ",
            LogTarget.Phase2 => "[P2] ",
            _ => string.Empty,
        };
        string line = $"{DateTime.Now:HH:mm:ss} {tag}{message}";
        _logQueue.Enqueue(new PendingLogLine(line, Volatile.Read(ref _logGeneration)));
        ScheduleLogFlush();
    }

    /// <summary>
    /// Schedules exactly one UI-thread flush per pending batch: the atomic flag flips 0→1 only for the
    /// first enqueue after a drain, so a burst of log events collapses into a single dispatch (#20).
    /// </summary>
    private void ScheduleLogFlush()
    {
        if (Interlocked.Exchange(ref _logFlushScheduled, 1) == 0)
        {
            _uiDispatcher.Post(FlushLogQueue);
        }
    }

    /// <summary>
    /// Runs on the UI thread. Releases the flush flag first (so lines enqueued during the drain
    /// schedule the next flush), then applies the queued batch.
    /// </summary>
    private void FlushLogQueue()
    {
        Interlocked.Exchange(ref _logFlushScheduled, 0);
        DrainLogQueue();
    }

    /// <summary>
    /// Drains the queue onto the bound log collection, dropping any line whose generation is not the
    /// current one — so a stale flush queued by a prior run cannot repopulate a log the next run has
    /// already cleared (#20). Also called synchronously from the run's finally as the final drain.
    /// </summary>
    private void DrainLogQueue()
    {
        int generation = Volatile.Read(ref _logGeneration);
        while (_logQueue.TryDequeue(out PendingLogLine entry))
        {
            if (entry.Generation != generation)
            {
                continue;
            }

            LogEntries.Add(entry.Line);
        }
    }

    /// <summary>
    /// Clears the visible log and starts a new log generation for a run. Bumping the generation makes
    /// any lines still queued from a prior run drop on their (stale) flush, and resetting the flush flag
    /// ensures this run's first line schedules a fresh dispatch (#20).
    /// </summary>
    private void BeginNewLogGeneration()
    {
        LogEntries.Clear();
        Interlocked.Increment(ref _logGeneration);
        Interlocked.Exchange(ref _logFlushScheduled, 0);
    }

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

    internal void BeginNewLogGenerationForTest() => BeginNewLogGeneration();

    /// <summary>One queued log line, tagged with the run generation it belongs to (#20).</summary>
    private readonly record struct PendingLogLine(string Line, int Generation);

    /// <summary>The active set/attempt label for progress messages: <c>Set X/N · &lt;stage&gt;</c> (#24).</summary>
    private sealed record SetStageLabel(int SetIndex, int SetCount, string Stage)
    {
        public string Format() => $"Set {SetIndex}/{SetCount} · {Stage}";
    }

    // ── SRR Import Helpers ──

    private void SetRARVersionsFromSRR(SRRFile srr)
    {
        if (!srr.RARVersion.HasValue)
        {
            return;
        }

        int unpVer = srr.RARVersion.Value;
        Version2 = Version3 = Version4 = Version5 = Version6 = Version7 = false;

        if (unpVer >= 70)
        {
            Version7 = true;
            Log(LogTarget.System, "RAR versions: 7.x");
        }
        else if (unpVer >= 50)
        {
            Version5 = true;
            Version6 = true;
            Log(LogTarget.System, "RAR versions: 5.x, 6.x");
        }
        else if (srr.DictionarySize.HasValue && srr.DictionarySize.Value > 4096)
        {
            Version5 = true;
            Version6 = true;
            Log(LogTarget.System, $"Large dictionary ({srr.DictionarySize.Value} KB) — RAR 5.x, 6.x");
        }
        else
        {
            bool isRAR2 = unpVer <= 29;
            bool isRAR3 = unpVer is >= 20 and <= 36;
            bool isRAR4 = unpVer is >= 26 and <= 36;

            if (srr.HasFirstVolumeFlag == true || srr.HasUnicodeNames == true)
            {
                isRAR2 = false;
            }

            if (unpVer == 36)
            {
                isRAR2 = false;
                isRAR3 = true;
                isRAR4 = true;
            }

            Version2 = isRAR2;
            Version3 = isRAR3;
            Version4 = isRAR4;
            Version5 = true; // Can create RAR4 format with -ma4
            Version6 = true;

            List<string> selected = [];
            if (isRAR2)
            {
                selected.Add("2.x");
            }

            if (isRAR3)
            {
                selected.Add("3.x");
            }

            if (isRAR4)
            {
                selected.Add("4.x");
            }

            selected.Add("5.x");
            selected.Add("6.x");
            Log(LogTarget.System, $"RAR versions: {string.Join(", ", selected)}");
        }
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
        if (sizeBytes <= 0)
        {
            return;
        }

        SwitchV = true;

        if (sizeBytes % 1_000_000_000 == 0)
        {
            VolumeSize = (sizeBytes / 1_000_000_000).ToString();
            VolumeSizeUnitIndex = 3;
        }
        else if (sizeBytes % 1_000_000 == 0)
        {
            VolumeSize = (sizeBytes / 1_000_000).ToString();
            VolumeSizeUnitIndex = 2;
        }
        else if (sizeBytes % 1_000 == 0)
        {
            VolumeSize = (sizeBytes / 1_000).ToString();
            VolumeSizeUnitIndex = 1;
        }
        else if (sizeBytes % (1024L * 1024 * 1024) == 0)
        {
            VolumeSize = (sizeBytes / (1024L * 1024 * 1024)).ToString();
            VolumeSizeUnitIndex = 6;
        }
        else if (sizeBytes % (1024L * 1024) == 0)
        {
            VolumeSize = (sizeBytes / (1024L * 1024)).ToString();
            VolumeSizeUnitIndex = 5;
        }
        else if (sizeBytes % 1024 == 0)
        {
            VolumeSize = (sizeBytes / 1024).ToString();
            VolumeSizeUnitIndex = 4;
        }
        else
        {
            VolumeSize = sizeBytes.ToString();
            VolumeSizeUnitIndex = 0;
        }

        Log(LogTarget.System, $"Volume size: {VolumeSize} {VolumeSizeUnits[VolumeSizeUnitIndex]}");
    }

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
