using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ReScene.Core;
using ReScene.Core.Cryptography;
using ReScene.Core.IO;
using ReScene.NET.Helpers;
using ReScene.NET.Models;
using ReScene.NET.Services;
using ReScene.NET.ViewModels.Reconstruction;
using ReScene.RAR;
using ReScene.SRR;

namespace ReScene.NET.ViewModels;

public partial class ReconstructorViewModel : ViewModelBase
{
    private const long DefaultVolumeSizeKb = 15000;

    private readonly IBruteForceService _bruteForceService;
    private readonly IFileDialogService _fileDialog;
    private readonly IAppSettingsService? _settingsService;
    private readonly IUiDispatcher _uiDispatcher;
    private CancellationTokenSource? _cts;

    // Elapsed timer — ticks every second so the clock doesn't freeze between progress events
    private readonly DispatcherTimer _elapsedTimer;

    // Per-run progress bookkeeping (timing + version table + copy/verify timing).
    private readonly ReconstructionProgressTracker<VersionEntry> _progress;

    // ── Imported SRR state ──
    // All reconstruction state captured from an imported SRR lives in one holder so the options
    // builder and config capture/restore can pass it around as a unit.
    private ReconstructionImportState _import = new();

    // Timestamp-preservation failures accumulated during the current run.
    // Surfaced as a single MessageBox when the operation completes so the
    // user is aware that the resulting RAR's File Time (DOS) may not match
    // the original for those files.
    private readonly List<TimestampPreservationFailedEventArgs> _timestampFailures = [];

    public ReconstructorViewModel(IBruteForceService bruteForceService, IFileDialogService fileDialog, IAppSettingsService? settingsService = null, IUiDispatcher? uiDispatcher = null)
    {
        _bruteForceService = bruteForceService;
        _fileDialog = fileDialog;
        _settingsService = settingsService;
        _uiDispatcher = uiDispatcher ?? new WpfDispatcher();

        _bruteForceService.Progress += OnProgress;
        _bruteForceService.StatusChanged += OnStatusChanged;
        _bruteForceService.LogMessage += OnLogMessage;
        _bruteForceService.FileCopyProgress += OnFileCopyProgress;
        _bruteForceService.CRCValidationProgress += OnCRCValidationProgress;
        _bruteForceService.TimestampPreservationFailed += OnTimestampPreservationFailed;

        _progress = new ReconstructionProgressTracker<VersionEntry>(
            VersionEntries,
            createRow: (label, args, dir) => new VersionEntry { VersionName = label, Arguments = args, VersionDirectory = dir },
            setStatus: (row, status) => row.Status = status,
            setResult: (row, result) => row.Result = result,
            getFullCommandLine: row => row.FullCommandLine,
            appendLog: AppendLog);

        _elapsedTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _elapsedTimer.Tick += (_, _) => OnElapsedTimerTick();

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
        if (string.IsNullOrWhiteSpace(WinRarPath) && !string.IsNullOrWhiteSpace(settings.ReconstructWinRarPath))
        {
            WinRarPath = settings.ReconstructWinRarPath;
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

    /// <summary>True once an SRR has been successfully imported (drives the Beginner wizard's step gating).</summary>
    [ObservableProperty]
    public partial bool HasImportedSrr { get; set; }

    // ── Imported SRR details (shown after import) ──

    [ObservableProperty]
    public partial string ImportedSrrName { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string ImportedSrrAppName { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string ImportedRarVolumeText { get; set; } = string.Empty;

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
    public partial string WinRarPath { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartCommand))]
    [NotifyPropertyChangedFor(nameof(PathsNeedAttention))]
    public partial string ReleasePath { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PathsNeedAttention))]
    public partial string VerificationPath { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartCommand))]
    [NotifyPropertyChangedFor(nameof(PathsNeedAttention))]
    public partial string OutputPath { get; set; } = string.Empty;

    // ── Path status ──

    [ObservableProperty]
    public partial FieldStatus WinRarStatus { get; set; } = FieldStatus.None;

    [ObservableProperty]
    public partial FieldStatus ReleaseStatus { get; set; } = FieldStatus.None;

    [ObservableProperty]
    public partial FieldStatus VerifyStatus { get; set; } = FieldStatus.None;

    [ObservableProperty]
    public partial FieldStatus OutputStatus { get; set; } = FieldStatus.None;

    partial void OnWinRarPathChanged(string value) =>
        WinRarStatus = ReconstructorFieldGuidance.EvaluateWinRarPath(value);

    partial void OnReleasePathChanged(string value) =>
        ReleaseStatus = ReconstructorFieldGuidance.EvaluateReleasePath(value);

    partial void OnVerificationPathChanged(string value) =>
        VerifyStatus = ReconstructorFieldGuidance.EvaluateVerificationPath(value);

    partial void OnOutputPathChanged(string value) =>
        OutputStatus = ReconstructorFieldGuidance.EvaluateOutputPath(value);

    /// <summary>
    /// Recomputes all four path statuses from the current path values. Called at construction and
    /// after <see cref="Reset"/> so a blank field shows its "Required" marker immediately — the
    /// per-property change hooks only fire when a value actually changes.
    /// </summary>
    private void RefreshPathStatuses()
    {
        WinRarStatus = ReconstructorFieldGuidance.EvaluateWinRarPath(WinRarPath);
        ReleaseStatus = ReconstructorFieldGuidance.EvaluateReleasePath(ReleasePath);
        VerifyStatus = ReconstructorFieldGuidance.EvaluateVerificationPath(VerificationPath);
        OutputStatus = ReconstructorFieldGuidance.EvaluateOutputPath(OutputPath);
    }

    /// <summary>
    /// True while any required path (WinRAR, Release, Verify, Output) is empty or invalid —
    /// drives the warning glyph on the Paths sub-tab header.
    /// </summary>
    public bool PathsNeedAttention =>
        ReconstructorFieldGuidance.PathsNeedAttention(WinRarPath, ReleasePath, VerificationPath, OutputPath);

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

        /// <summary>
        /// Directory of the WinRAR version this entry tested; the run executes rar.exe inside it.
        /// </summary>
        public string VersionDirectory { get; set; } = "";

        /// <summary>
        /// The complete command line as executed: the quoted rar.exe path followed by the arguments.
        /// </summary>
        public string FullCommandLine => string.IsNullOrEmpty(VersionDirectory)
            ? Arguments
            : $"\"{Path.Combine(VersionDirectory, "rar.exe")}\" {Arguments}";
    }

    // ── Logs ──

    [ObservableProperty] public partial string SystemLog { get; set; } = string.Empty;
    [ObservableProperty] public partial string Phase1Log { get; set; } = string.Empty;
    [ObservableProperty] public partial string Phase2Log { get; set; } = string.Empty;

    // ── RAR Versions ──

    [ObservableProperty] public partial bool Version2 { get; set; }
    [ObservableProperty] public partial bool Version3 { get; set; } = true;
    [ObservableProperty] public partial bool Version4 { get; set; } = true;
    [ObservableProperty] public partial bool Version5 { get; set; } = true;
    [ObservableProperty] public partial bool Version6 { get; set; } = true;
    [ObservableProperty] public partial bool Version7 { get; set; }

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
    [NotifyPropertyChangedFor(nameof(IsRenameToOriginalEnabled))]
    [NotifyPropertyChangedFor(nameof(IsRenameToSfvEnabled))]
    public partial bool StopOnFirstMatch { get; set; } = true;

    [ObservableProperty] public partial bool CompleteAllVolumes { get; set; }
    [ObservableProperty] public partial bool RenameToOriginal { get; set; }
    [ObservableProperty] public partial bool RenameToSfvNames { get; set; } = true;

    // ── Computed enable/disable ──

    public bool IsMTRangeEnabled => SwitchMT;
    public bool IsVolumeOptionsEnabled => SwitchV;
    public bool IsSwitchAIEnabled => FileA == false && FileI == false;
    public bool IsFileAttributesEnabled => !SwitchAI;
    public bool IsDeleteDuplicateCRCEnabled => !DeleteRARFiles;
    public bool IsRenameToOriginalEnabled => StopOnFirstMatch;
    public bool IsRenameToSfvEnabled => StopOnFirstMatch;

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
        WinRarPath = string.Empty;
        ReleasePath = string.Empty;
        VerificationPath = string.Empty;
        OutputPath = string.Empty;

        // Import gating + warning
        HasImportedSrr = false;
        CustomPackerWarning = null;
        LastRunSucceeded = false;

        // Imported SRR details
        ImportedSrrName = string.Empty;
        ImportedSrrAppName = string.Empty;
        ImportedRarVolumeText = string.Empty;
        ImportedArchivedFilesText = string.Empty;
        ImportedCompressionText = string.Empty;
        ImportedStoredFilesText = string.Empty;

        // Imported SRR + detected header state — back to empty/null
        _import.Clear();

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

        // Logs
        SystemLog = string.Empty;
        Phase1Log = string.Empty;
        Phase2Log = string.Empty;

        // The brute-force option toggles (versions, compression, dictionary, timestamps,
        // volume, etc.) are intentionally left untouched: they are re-applied wholesale by
        // the mandatory Import-from-SRR step that opens the reconstruct wizard.

        // The paths were just cleared; pre-fill the configured defaults again.
        ApplyPathDefaultsFromSettings();
        RefreshPathStatuses();
    }

    // ── Browse Commands ──

    [RelayCommand]
    private async Task BrowseWinRarAsync()
    {
        string? path = await _fileDialog.OpenFolderAsync("Select WinRAR Installations Directory");
        if (path is not null)
        {
            WinRarPath = path;
        }
    }

    [RelayCommand]
    private async Task BrowseReleaseAsync()
    {
        string? path = await _fileDialog.OpenFolderAsync("Select Release Directory");
        if (path is not null)
        {
            ReleasePath = path;
        }
    }

    [RelayCommand]
    private async Task BrowseVerificationAsync()
    {
        string? path = await _fileDialog.OpenFileAsync("Select Verification File",
            FileDialogFilters.VerificationFiles);
        if (path is not null)
        {
            VerificationPath = path;
        }
    }

    [RelayCommand]
    private async Task BrowseOutputAsync()
    {
        string? path = await _fileDialog.OpenFolderAsync("Select Output Directory");
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
            FileDialogFilters.SRRFiles);
        if (path is null)
        {
            return;
        }

        HasImportedSrr = false;

        try
        {
            Log(LogTarget.System, $"=== SRR Import: {Path.GetFileName(path)} ===");

            var srr = SRRFile.Load(path);
            Log(LogTarget.System, "SRR loaded successfully");

            // Pure parse: imported/detected state, custom-packer detection, and display strings.
            ImportedSrrInfo info = SrrImportParser.Parse(srr, path);

            // Detect SRRs that carry no RAR reconstruction information
            // (no RAR volume entries, no archived-file metadata, no detected
            // compression method). These can't drive automatic option setup,
            // so warn the user that they'll need to configure things manually.
            if (!info.HasRarReconstructionInfo)
            {
                Log(LogTarget.System,
                    "WARNING: SRR contains no RAR reconstruction information.");
                _fileDialog.ShowInfo(
                    "No RAR Reconstruction Info",
                    "This SRR file does not contain RAR reconstruction information " +
                    "(no RAR volume entries, archived files, or compression metadata).\n\n" +
                    "You will need to configure the RAR options manually before reconstructing.");
            }

            // Custom packer detection
            if (srr.HasCustomPackerHeaders)
            {
                Log(LogTarget.System, $"Custom RAR packer detected: {srr.CustomPackerDetected}");
                _import.CustomPackerType = info.CustomPackerType;
                _import.SRRFilePath = path;
                string warning = info.CustomPackerWarning ?? string.Empty;
                CustomPackerWarning = warning;

                _fileDialog.ShowWarning("Custom RAR Packer Detected", warning);
            }
            else
            {
                _import.CustomPackerType = CustomPackerType.None;
                _import.SRRFilePath = null;
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
            _import.OriginalRarFileNames = info.OriginalRarFileNames;
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
                bool isRarUnix = srr.DetectedHostOS == 3;
                bool isRarWindows = srr.DetectedHostOS == 2;
                if ((isCurrentWindows && isRarUnix) || (!isCurrentWindows && isRarWindows))
                {
                    EnableHostOSPatching = true;
                    Log(LogTarget.System, "Host OS patching enabled (platform mismatch)");
                }
            }

            // Pure switch mapping: only the toggles the SRR actually specifies (partial diff —
            // unspecified groups stay null and the corresponding toggles are left untouched).
            SrrSwitchMapper.SwitchDiff switches = SrrSwitchMapper.Map(srr);
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

            // Volume size
            if (srr.RARFiles.Count > 1 && srr.VolumeSizeBytes.HasValue)
            {
                ApplyVolumeSize(srr.VolumeSizeBytes.Value);
            }
            else if (srr.IsVolumeArchive == true)
            {
                SwitchV = true;
                Log(LogTarget.System, "Multi-volume: Yes (size unknown)");
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

            // Extract stored SFV for verification
            TryExtractStoredSFV(path, srr);

            Log(LogTarget.System, "=== SRR Import Complete ===");

            PopulateImportedSrrDetails(info);
            HasImportedSrr = true;
        }
        catch (Exception ex)
        {
            Log(LogTarget.System, $"Failed to import SRR: {ex.Message}");
        }
    }

    /// <summary>Maps the parsed SRR summary onto the bound display properties shown on the wizard's import step.</summary>
    private void PopulateImportedSrrDetails(ImportedSrrInfo info)
    {
        ImportedSrrName = info.DisplayName;
        ImportedSrrAppName = info.DisplayAppName;
        ImportedRarVolumeText = info.DisplayRarVolumeText;
        ImportedArchivedFilesText = info.DisplayArchivedFilesText;
        ImportedCompressionText = info.DisplayCompressionText;
        ImportedStoredFilesText = info.DisplayStoredFilesText;
    }

    // ── Import / Export Configuration ──

    private static readonly System.Text.Json.JsonSerializerOptions _configSerializerOptions = new() { WriteIndented = true };

    [RelayCommand]
    private async Task ImportConfigAsync()
    {
        string? path = await _fileDialog.OpenFileAsync("Select Reconstructor Configuration",
            FileDialogFilters.ReconstructorConfig);
        if (path is null)
        {
            return;
        }

        try
        {
            string json = await File.ReadAllTextAsync(path);
            var config = System.Text.Json.JsonSerializer.Deserialize<ReconstructorConfig>(json);
            if (config is null)
            {
                Log(LogTarget.System, "Failed to import configuration: file is empty or invalid");
                return;
            }

            ApplyConfig(config);
            Log(LogTarget.System, $"Configuration imported from {Path.GetFileName(path)}");
        }
        catch (Exception ex)
        {
            Log(LogTarget.System, $"Failed to import configuration: {ex.Message}");
        }
    }

    [RelayCommand]
    private async Task ExportConfigAsync()
    {
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
        }
        catch (Exception ex)
        {
            Log(LogTarget.System, $"Failed to export configuration: {ex.Message}");
        }
    }

    private ReconstructorConfig CaptureConfig()
    {
        ReconstructorConfig config = ReconstructorConfigMapper.Capture(this);
        config.ImportedSrr = CaptureImportedSrrState();
        return config;
    }

    private ImportedSrrState? CaptureImportedSrrState() =>
        ImportedSrrStateMapper.Capture(_import, CustomPackerWarning);

    private void ApplyConfig(ReconstructorConfig c)
    {
        ReconstructorConfigMapper.Apply(this, c);
        ApplyImportedSrrState(c.ImportedSrr);
    }

    private void ApplyImportedSrrState(ImportedSrrState? s)
    {
        // Always reset — an absent block means "no SRR imported"
        _import = ImportedSrrStateMapper.Apply(s);
        CustomPackerWarning = s?.CustomPackerWarning;

        if (s is not null)
        {
            Log(LogTarget.System, $"Restored SRR state: {_import.ArchiveFiles.Count} files, mtime={_import.FileTimestamps.Count}, CRCs={_import.ArchiveFileCrcs.Count}, CMT={(_import.CmtCompressedData?.Length ?? 0)} bytes");
        }
    }

    // ── Start / Stop ──

    private bool CanStart() => !IsRunning
        && !string.IsNullOrWhiteSpace(WinRarPath)
        && !string.IsNullOrWhiteSpace(ReleasePath)
        && !string.IsNullOrWhiteSpace(OutputPath);

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

        if (string.IsNullOrWhiteSpace(WinRarPath))
        {
            Log(LogTarget.System, "Invalid WinRAR directory.");
            _fileDialog.ShowError("Validation Error", "Invalid WinRAR directory.");
            return;
        }

        if (!Directory.Exists(WinRarPath))
        {
            Log(LogTarget.System, "WinRAR directory does not exist.");
            _fileDialog.ShowError("Validation Error", "WinRAR directory does not exist.");
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

        // ── Subdirectory timestamp warning ──

        if (Directory.EnumerateDirectories(ReleasePath).Any() && _import.DirTimestamps.Count == 0)
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

        int hashCount;
        try
        {
            hashCount = verificationExt == ".sfv"
                ? SFVFile.ReadFile(VerificationPath).Entries.Count
                : SHA1File.ReadFile(VerificationPath).Entries.Count;
        }
        catch (Exception ex)
        {
            Log(LogTarget.System, $"Failed to parse verification file: {ex.Message}");
            _fileDialog.ShowError("Validation Error", $"Failed to parse verification file:\n{ex.Message}");
            return;
        }

        if (hashCount == 0)
        {
            Log(LogTarget.System, "No hashes found in verification file.");
            _fileDialog.ShowError("Validation Error", "No hashes found in verification file.");
            return;
        }

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

        if (string.IsNullOrWhiteSpace(OutputPath))
        {
            Log(LogTarget.System, "Invalid output directory.");
            _fileDialog.ShowError("Validation Error", "Invalid output directory.");
            return;
        }

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
        else if (Directory.EnumerateFileSystemEntries(OutputPath).Any())
        {
            bool proceed = outputNotEmptyConfirmed || await _fileDialog.ShowConfirmAsync("Output Directory Not Empty",
                $"The output directory is not empty:\n\n{OutputPath}\n\nIts contents will be deleted before starting. Continue?");
            if (!proceed)
            {
                Log(LogTarget.System, "Cancelled: output directory not empty.");
                return;
            }

            try
            {
                foreach (string file in Directory.GetFiles(OutputPath))
                {
                    File.Delete(file);
                }

                foreach (string dir in Directory.GetDirectories(OutputPath))
                {
                    Directory.Delete(dir, true);
                }

                Log(LogTarget.System, "Output directory cleaned.");
            }
            catch (Exception ex)
            {
                Log(LogTarget.System, $"Failed to clean output directory: {ex.Message}");
                _fileDialog.ShowError("Error", $"Failed to clean output directory:\n{ex.Message}");
                return;
            }
        }

        // ── Start brute-force ──

        IsRunning = true;
        LastRunSucceeded = false;
        ShowProgress = true;
        ProgressPercent = 0;
        ProgressMessage = "Starting...";
        SystemLog = string.Empty;
        Phase1Log = string.Empty;
        Phase2Log = string.Empty;
        _timestampFailures.Clear();

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

        try
        {
            BruteForceOptions options = BuildBruteForceOptions();

            Log(LogTarget.System, "Starting brute-force...");
            Log(LogTarget.System, $"WinRAR: {WinRarPath}");
            Log(LogTarget.System, $"Release: {ReleasePath}");
            Log(LogTarget.System, $"Output: {OutputPath}");

            // Run entirely on a background thread so the UI stays responsive
            // during setup (directory enumeration, input validation, etc.)
            bool success = await Task.Run(() => _bruteForceService.RunAsync(options, token), token);

            // A Stop during RAR execution cancels the run but returns normally (the library
            // swallows the process's OperationCanceledException), so detect the cancelled token
            // here and report "Cancelled" rather than the misleading "No match found".
            token.ThrowIfCancellationRequested();

            // Mark final version entry
            _progress.CompleteActiveVersion(success ? "Match" : "No Match");

            ProgressMessage = success ? "Match found!" : "No match found.";
            PhaseDescription = success ? "Complete \u2014 Match Found!" : "Complete \u2014 No Match";
            ProgressPercent = 100;
            ProgressPercentText = "100%";
            if (_progress.LastOperationSize > 0)
            {
                TestCountText = $"Test {_progress.LastOperationSize:N0} of {_progress.LastOperationSize:N0}";
            }
            Log(LogTarget.System, success ? "Brute-force completed: match found!" : "Brute-force completed: no match.");
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

            _cts?.Dispose();
            _cts = null;
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
                Process.Start(new ProcessStartInfo { FileName = folder, UseShellExecute = true });
            }
        }
        catch (Exception ex)
        {
            Log(LogTarget.System, $"Could not open output folder: {ex.Message}");
        }
    }

    [RelayCommand]
    private async Task SaveLogAsync()
    {
        bool hasContent = SystemLog.Length > 0 || Phase1Log.Length > 0 || Phase2Log.Length > 0;

        if (!hasContent)
        {
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
            var lines = new List<string>();

            if (SystemLog.Length > 0)
            {
                lines.Add("=== System ===");
                lines.AddRange(SystemLog.Split(Environment.NewLine));
            }

            if (Phase1Log.Length > 0)
            {
                if (lines.Count > 0)
                {
                    lines.Add(string.Empty);
                }

                lines.Add("=== Phase 1 ===");
                lines.AddRange(Phase1Log.Split(Environment.NewLine));
            }

            if (Phase2Log.Length > 0)
            {
                if (lines.Count > 0)
                {
                    lines.Add(string.Empty);
                }

                lines.Add("=== Phase 2 ===");
                lines.AddRange(Phase2Log.Split(Environment.NewLine));
            }

            await LogExporter.SaveAsync(lines, path);
            Log(LogTarget.System, $"Log saved to {Path.GetFileName(path)}");
        }
        catch (Exception ex)
        {
            Log(LogTarget.System, $"ERROR saving log: {ex.Message}");
        }
    }

    // ── Build Options ──

    private BruteForceOptions BuildBruteForceOptions()
    {
        var options = new BruteForceOptions(WinRarPath, ReleasePath, OutputPath)
        {
            RAROptions = BuildRAROptions()
        };

        // Load hashes from verification file
        if (!string.IsNullOrWhiteSpace(VerificationPath))
        {
            string ext = Path.GetExtension(VerificationPath).ToLowerInvariant();
            if (ext == ".sfv")
            {
                var sfv = SFVFile.ReadFile(VerificationPath);
                foreach (SFVFileEntry entry in sfv.Entries)
                {
                    options.Hashes.Add(entry.CRC);
                }

                options.HashType = HashType.CRC32;
            }
            else if (ext == ".sha1")
            {
                var sha1 = SHA1File.ReadFile(VerificationPath);
                foreach (SHA1FileEntry entry in sha1.Entries)
                {
                    options.Hashes.Add(entry.SHA1);
                }

                options.HashType = HashType.SHA1;
            }
        }

        return options;
    }

    private RAROptions BuildRAROptions()
    {
        RarSwitchSettings switches = BuildSwitchSettings();
        List<VersionRange> rarVersions = RarCommandLineBuilder.BuildVersionRanges(switches);

        (bool renameOutput, List<string> renameNames) = ResolveOutputRenameNames();

        return new()
        {
            SetFileArchiveAttribute = ToTriState(FileA),
            SetFileNotContentIndexedAttribute = ToTriState(FileI),
            CommandLineArguments = RarCommandLineBuilder.BuildCommandLineArguments(switches),
            RARVersions = rarVersions,
            DeleteRARFiles = DeleteRARFiles,
            DeleteDuplicateCRCFiles = DeleteDuplicateCRCFiles,
            StopOnFirstMatch = StopOnFirstMatch,
            CompleteAllVolumes = CompleteAllVolumes,
            RenameToOriginalNames = renameOutput,
            OriginalRarFileNames = renameNames,
            ArchiveFileCrcs = new Dictionary<string, string>(_import.ArchiveFileCrcs, StringComparer.OrdinalIgnoreCase),
            ArchiveFilePaths = new HashSet<string>(_import.ArchiveFiles, StringComparer.OrdinalIgnoreCase),
            ArchiveDirectoryPaths = new HashSet<string>(_import.ArchiveDirectories, StringComparer.OrdinalIgnoreCase),
            DirectoryTimestamps = new Dictionary<string, DateTime>(_import.DirTimestamps, StringComparer.OrdinalIgnoreCase),
            DirectoryCreationTimes = new Dictionary<string, DateTime>(_import.DirCreationTimes, StringComparer.OrdinalIgnoreCase),
            DirectoryAccessTimes = new Dictionary<string, DateTime>(_import.DirAccessTimes, StringComparer.OrdinalIgnoreCase),
            FileTimestamps = new Dictionary<string, DateTime>(_import.FileTimestamps, StringComparer.OrdinalIgnoreCase),
            FileCreationTimes = new Dictionary<string, DateTime>(_import.FileCreationTimes, StringComparer.OrdinalIgnoreCase),
            FileAccessTimes = new Dictionary<string, DateTime>(_import.FileAccessTimes, StringComparer.OrdinalIgnoreCase),
            ArchiveComment = _import.ArchiveComment,
            ArchiveCommentBytes = _import.ArchiveCommentBytes,
            CmtCompressedData = _import.CmtCompressedData,
            CmtCompressionMethod = _import.CmtCompressionMethod,
            EnableHostOSPatching = EnableHostOSPatching,
            DetectedFileHostOS = _import.DetectedFileHostOS,
            DetectedFileAttributes = _import.DetectedFileAttributes,
            DetectedCmtHostOS = _import.DetectedCmtHostOS,
            DetectedCmtFileTime = _import.DetectedCmtFileTime,
            DetectedCmtFileAttributes = _import.DetectedCmtFileAttributes,
            DetectedLargeFlag = _import.DetectedLargeFlag,
            DetectedHighPackSize = _import.DetectedHighPackSize,
            DetectedHighUnpSize = _import.DetectedHighUnpSize,
            UseOldVolumeNaming = UseOldVolumeNaming,
            CustomPackerDetected = _import.CustomPackerType,
            SRRFilePath = _import.SRRFilePath
        };
    }

    /// <summary>
    /// Picks the names the matched output volumes are renamed to. Either rename option uses the
    /// SRR's original RAR names when an SRR is imported (they are exact, in volume order); with
    /// "Rename to SFV file names" checked and no SRR, the RAR volume entries of the verification
    /// .sfv are used (in SFV order).
    /// </summary>
    private (bool Rename, List<string> Names) ResolveOutputRenameNames()
    {
        if ((RenameToOriginal || RenameToSfvNames) && _import.OriginalRarFileNames.Count > 0)
        {
            return (true, _import.OriginalRarFileNames);
        }

        if (RenameToSfvNames
            && !string.IsNullOrWhiteSpace(VerificationPath)
            && Path.GetExtension(VerificationPath).Equals(".sfv", StringComparison.OrdinalIgnoreCase)
            && File.Exists(VerificationPath))
        {
            try
            {
                List<string> sfvNames = SFVFile.ReadFile(VerificationPath).Entries
                    .Select(e => e.FileName)
                    .Where(RARVolumeIdentifier.IsRarVolume)
                    .ToList();

                if (sfvNames.Count > 0)
                {
                    return (true, sfvNames);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Log(LogTarget.System, $"Could not read SFV for output renaming: {ex.Message}");
            }
        }

        return (RenameToOriginal, _import.OriginalRarFileNames);
    }

    /// <summary>Captures the current RAR switch toggles for <see cref="RarCommandLineBuilder"/>.</summary>
    private RarSwitchSettings BuildSwitchSettings() => new()
    {
        Version2 = Version2,
        Version3 = Version3,
        Version4 = Version4,
        Version5 = Version5,
        Version6 = Version6,
        Version7 = Version7,

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
            ProgressMessage = u.ProgressMessage;
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
    }

    private void OnStatusChanged(object? _, BruteForceStatusChangedEventArgs e)
    {
        _uiDispatcher.Invoke(() =>
        {
            if (e.NewStatus == OperationStatus.Completed)
            {
                ProgressMessage = e.CompletionStatus switch
                {
                    OperationCompletionStatus.Success => "Completed successfully!",
                    OperationCompletionStatus.Error => "Failed.",
                    OperationCompletionStatus.Cancelled => "Cancelled.",
                    _ => "Completed."
                };

                LastRunSucceeded = e.CompletionStatus == OperationCompletionStatus.Success;

                ShowTimestampFailureWarningIfAny();
            }
        });
    }

    private void OnTimestampPreservationFailed(object? _, TimestampPreservationFailedEventArgs e)
    {
        // The library already logs a Warning via its logger (routed through
        // OnLogMessage). Track the failure here so we can show a single
        // summary MessageBox when the run finishes.
        _timestampFailures.Add(e);
    }

    private void ShowTimestampFailureWarningIfAny()
    {
        if (_timestampFailures.Count == 0)
        {
            return;
        }

        const int MaxFilesToList = 10;
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("Could not copy the source file's modification time onto the working copy " +
                      "for the following file(s):");
        sb.AppendLine();

        int shown = Math.Min(_timestampFailures.Count, MaxFilesToList);
        for (int i = 0; i < shown; i++)
        {
            TimestampPreservationFailedEventArgs f = _timestampFailures[i];
            sb.AppendLine($"  • {f.DestinationPath}");
            sb.AppendLine($"      ({f.ErrorMessage})");
        }

        if (_timestampFailures.Count > MaxFilesToList)
        {
            sb.AppendLine($"  … and {_timestampFailures.Count - MaxFilesToList} more.");
        }

        sb.AppendLine();
        sb.AppendLine("WinRAR will pack these files with the copy time instead of the original " +
                      "modification time, so the resulting RAR's File Time (DOS) may differ " +
                      "from the original release.");

        _fileDialog.ShowWarning("Timestamp Preservation Failed", sb.ToString());
    }

    private void OnLogMessage(object? _, LogEventArgs e) => _uiDispatcher.Invoke(() => AppendLog(e.Target, e.Message));

    private void Log(LogTarget target, string message) => AppendLog(target, message);

    private void AppendLog(LogTarget target, string message)
    {
        string line = $"{DateTime.Now:HH:mm:ss} {message}";
        switch (target)
        {
            case LogTarget.Phase1:
                Phase1Log = Phase1Log.Length == 0 ? line : Phase1Log + Environment.NewLine + line;
                break;
            case LogTarget.Phase2:
                Phase2Log = Phase2Log.Length == 0 ? line : Phase2Log + Environment.NewLine + line;
                break;
            default:
                SystemLog = SystemLog.Length == 0 ? line : SystemLog + Environment.NewLine + line;
                break;
        }
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
            bool isRar2 = unpVer <= 29;
            bool isRar3 = unpVer is >= 20 and <= 36;
            bool isRar4 = unpVer is >= 26 and <= 36;

            if (srr.HasFirstVolumeFlag == true || srr.HasUnicodeNames == true)
            {
                isRar2 = false;
            }

            if (unpVer == 36)
            {
                isRar2 = false;
                isRar3 = true;
                isRar4 = true;
            }

            Version2 = isRar2;
            Version3 = isRar3;
            Version4 = isRar4;
            Version5 = true; // Can create RAR4 format with -ma4
            Version6 = true;

            List<string> selected = [];
            if (isRar2)
            {
                selected.Add("2.x");
            }

            if (isRar3)
            {
                selected.Add("3.x");
            }

            if (isRar4)
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
    /// Applies the partial switch diff produced by <see cref="SrrSwitchMapper"/> onto the bound
    /// option toggles, emitting the same log lines in the same order as the original inline mapping.
    /// Groups left null by the mapper (no SRR information) are skipped, so their toggles keep their
    /// current values rather than being reset.
    /// </summary>
    private void ApplySwitchDiff(SrrSwitchMapper.SwitchDiff diff)
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
                case SrrSwitchMapper.DictionarySwitch.MD64K:
                    SwitchMD64K = true;
                    break;
                case SrrSwitchMapper.DictionarySwitch.MD128K:
                    SwitchMD128K = true;
                    break;
                case SrrSwitchMapper.DictionarySwitch.MD256K:
                    SwitchMD256K = true;
                    break;
                case SrrSwitchMapper.DictionarySwitch.MD512K:
                    SwitchMD512K = true;
                    break;
                case SrrSwitchMapper.DictionarySwitch.MD1024K:
                    SwitchMD1024K = true;
                    break;
                case SrrSwitchMapper.DictionarySwitch.MD2048K:
                    SwitchMD2048K = true;
                    break;
                case SrrSwitchMapper.DictionarySwitch.MD4096K:
                    SwitchMD4096K = true;
                    break;
            }

            Log(LogTarget.System, $"Dictionary: {dictionary.SizeKb} KB");
        }

        // Solid archive
        if (diff.SwitchSDash is { } switchSDash)
        {
            SwitchSDash = switchSDash;
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

    private void TryExtractStoredSFV(string srrFilePath, SRRFile srr)
    {
        if (srr.StoredFiles.Count == 0)
        {
            return;
        }

        try
        {
            string tempDir = Path.Combine(Path.GetTempPath(), "ReScene.NET", "srr-import",
                $"{Path.GetFileNameWithoutExtension(srrFilePath)}_{Guid.NewGuid():N}");

            string? extracted = srr.ExtractStoredFile(srrFilePath, tempDir,
                fileName => Path.GetExtension(fileName).Equals(".sfv", StringComparison.OrdinalIgnoreCase));

            if (!string.IsNullOrEmpty(extracted))
            {
                VerificationPath = extracted;
                Log(LogTarget.System, $"Stored SFV extracted: {Path.GetFileName(extracted)}");
            }
        }
        catch (Exception ex)
        {
            Log(LogTarget.System, $"Failed to extract stored SFV: {ex.Message}");
        }
    }
}
