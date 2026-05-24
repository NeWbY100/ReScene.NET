using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ReScene.Core;
using ReScene.Core.Cryptography;
using ReScene.Core.Diagnostics;
using ReScene.Core.IO;
using ReScene.NET.Helpers;
using ReScene.NET.Models;
using ReScene.NET.Services;
using ReScene.RAR;
using ReScene.SRR;

namespace ReScene.NET.ViewModels;

public partial class ReconstructorViewModel : ViewModelBase
{
    private const long DefaultVolumeSizeKb = 15000;

    private readonly IBruteForceService _bruteForceService;
    private readonly IFileDialogService _fileDialog;
    private CancellationTokenSource? _cts;

    // Elapsed timer — ticks every second so the clock doesn't freeze between progress events
    private readonly DispatcherTimer _elapsedTimer;
    private readonly Stopwatch _stopwatch = new();
    private double _lastSecondsPerOperation; // cached rate from last progress event
    private long _lastOperationRemaining;    // cached remaining count from last progress event
    private long _lastOperationSize;         // cached total count from last progress event

    // ── Imported SRR state ──
    private HashSet<string> _importedArchiveFiles = new(StringComparer.OrdinalIgnoreCase);
    private HashSet<string> _importedArchiveDirectories = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, DateTime> _importedDirTimestamps = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, DateTime> _importedDirCreationTimes = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, DateTime> _importedDirAccessTimes = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, DateTime> _importedFileTimestamps = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, DateTime> _importedFileCreationTimes = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, DateTime> _importedFileAccessTimes = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, string> _importedArchiveFileCrcs = new(StringComparer.OrdinalIgnoreCase);
    private string? _importedArchiveComment;
    private byte[]? _importedArchiveCommentBytes;
    private byte[]? _importedCmtCompressedData;
    private byte? _importedCmtCompressionMethod;
    private byte? _detectedFileHostOS;
    private uint? _detectedFileAttributes;
    private byte? _detectedCmtHostOS;
    private uint? _detectedCmtFileTime;
    private uint? _detectedCmtFileAttributes;
    private bool? _detectedLargeFlag;
    private uint? _detectedHighPackSize;
    private uint? _detectedHighUnpSize;
    private List<string> _importedOriginalRarFileNames = [];
    private CustomPackerType _importedCustomPackerType = CustomPackerType.None;
    private string? _importedSRRFilePath;

    // Timestamp-preservation failures accumulated during the current run.
    // Surfaced as a single MessageBox when the operation completes so the
    // user is aware that the resulting RAR's File Time (DOS) may not match
    // the original for those files.
    private readonly List<TimestampPreservationFailedEventArgs> _timestampFailures = [];

    public ReconstructorViewModel(IBruteForceService bruteForceService, IFileDialogService fileDialog)
    {
        _bruteForceService = bruteForceService;
        _fileDialog = fileDialog;

        _bruteForceService.Progress += OnProgress;
        _bruteForceService.StatusChanged += OnStatusChanged;
        _bruteForceService.LogMessage += OnLogMessage;
        _bruteForceService.FileCopyProgress += OnFileCopyProgress;
        _bruteForceService.CRCValidationProgress += OnCRCValidationProgress;
        _bruteForceService.TimestampPreservationFailed += OnTimestampPreservationFailed;

        _elapsedTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _elapsedTimer.Tick += (_, _) => OnElapsedTimerTick();
    }

    // ── Warning ──

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasCustomPackerWarning))]
    public partial string? CustomPackerWarning { get; set; }

    public bool HasCustomPackerWarning => !string.IsNullOrEmpty(CustomPackerWarning);

    // ── Paths ──

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartCommand))]
    public partial string WinRarPath { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartCommand))]
    public partial string ReleasePath { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string VerificationPath { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartCommand))]
    public partial string OutputPath { get; set; } = string.Empty;

    // ── Path status ──

    [ObservableProperty]
    public partial FieldStatus WinRarStatus { get; set; } = FieldStatus.None;

    [ObservableProperty]
    public partial FieldStatus ReleaseStatus { get; set; } = FieldStatus.None;

    [ObservableProperty]
    public partial FieldStatus VerifyStatus { get; set; } = FieldStatus.None;

    partial void OnWinRarPathChanged(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            WinRarStatus = FieldStatus.None;
            return;
        }

        if (!Directory.Exists(value))
        {
            WinRarStatus = FieldStatus.Error("This WinRAR directory does not exist.");
            return;
        }

        WinRarStatus = FieldStatus.Ok("WinRAR installations directory selected.");
    }

    partial void OnReleasePathChanged(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            ReleaseStatus = FieldStatus.None;
        }
        else if (!Directory.Exists(value) && !File.Exists(value))
        {
            ReleaseStatus = FieldStatus.Error("This path does not exist.");
        }
        else
        {
            ReleaseStatus = FieldStatus.Ok("Source files selected.");
        }
    }

    partial void OnVerificationPathChanged(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            VerifyStatus = FieldStatus.None;
        }
        else if (!File.Exists(value))
        {
            VerifyStatus = FieldStatus.Error("This .srr file does not exist.");
        }
        else
        {
            VerifyStatus = FieldStatus.Info("Reconstructed archives will be verified against this SRR.");
        }
    }

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

    private readonly Stopwatch _copyStopwatch = new();

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

    private readonly Stopwatch _verifyStopwatch = new();

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

    private string _lastPhaseDescription = "";
    private int _activeVersionIndex = -1;
    private string _activeVersionKey = "";

    [GeneratedRegex(@"(?:win)?(?:rar|wr)(?:-x64|-x32)?-?(\d+)(b\d+)?", RegexOptions.IgnoreCase)]
    private static partial Regex VersionLabelRegex();

    public partial class VersionEntry : ObservableObject
    {
        [ObservableProperty] public partial string VersionName { get; set; } = "";
        [ObservableProperty] public partial string Status { get; set; } = "Testing";
        [ObservableProperty] public partial string Arguments { get; set; } = "";
        [ObservableProperty] public partial string Result { get; set; } = "";
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
    public partial bool StopOnFirstMatch { get; set; } = true;

    [ObservableProperty] public partial bool CompleteAllVolumes { get; set; }
    [ObservableProperty] public partial bool RenameToOriginal { get; set; }

    // ── Computed enable/disable ──

    public bool IsMTRangeEnabled => SwitchMT;
    public bool IsVolumeOptionsEnabled => SwitchV;
    public bool IsSwitchAIEnabled => FileA == false && FileI == false;
    public bool IsFileAttributesEnabled => !SwitchAI;
    public bool IsDeleteDuplicateCRCEnabled => !DeleteRARFiles;
    public bool IsRenameToOriginalEnabled => StopOnFirstMatch;

    // Host OS patching
    [ObservableProperty] public partial bool EnableHostOSPatching { get; set; } = true;

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

        try
        {
            Log(LogTarget.System, $"=== SRR Import: {Path.GetFileName(path)} ===");

            var srr = SRRFile.Load(path);
            Log(LogTarget.System, "SRR loaded successfully");

            // Detect SRRs that carry no RAR reconstruction information
            // (no RAR volume entries, no archived-file metadata, no detected
            // compression method). These can't drive automatic option setup,
            // so warn the user that they'll need to configure things manually.
            bool hasRarReconstructionInfo = srr.RARFiles.Count > 0
                || srr.ArchivedFiles.Count > 0
                || srr.CompressionMethod.HasValue;

            if (!hasRarReconstructionInfo)
            {
                Log(LogTarget.System,
                    "WARNING: SRR contains no RAR reconstruction information.");
                MessageBox.Show(
                    "This SRR file does not contain RAR reconstruction information " +
                    "(no RAR volume entries, archived files, or compression metadata).\n\n" +
                    "You will need to configure the RAR options manually before reconstructing.",
                    "No RAR Reconstruction Info",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }

            // Custom packer detection
            if (srr.HasCustomPackerHeaders)
            {
                Log(LogTarget.System, $"Custom RAR packer detected: {srr.CustomPackerDetected}");
                _importedCustomPackerType = srr.CustomPackerDetected;
                _importedSRRFilePath = path;

                string groups = srr.CustomPackerDetected switch
                {
                    CustomPackerType.AllOnesWithLargeFlag => "RELOADED, HI2U, 0x0007, 0x0815",
                    CustomPackerType.MaxUint32WithoutLargeFlag => "QCF",
                    _ => "Unknown"
                };
                CustomPackerWarning = $"Custom RAR packer detected ({srr.CustomPackerDetected}) — brute-forcing is not possible. " +
                    $"Direct SRR reconstruction will be used instead. Known groups: {groups}.";

                MessageBox.Show(CustomPackerWarning, "Custom RAR Packer Detected",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            else
            {
                _importedCustomPackerType = CustomPackerType.None;
                _importedSRRFilePath = null;
                CustomPackerWarning = null;
            }

            // Store imported data
            _importedArchiveFiles = new HashSet<string>(srr.ArchivedFiles, StringComparer.OrdinalIgnoreCase);
            _importedArchiveDirectories = new HashSet<string>(srr.ArchivedDirectories, StringComparer.OrdinalIgnoreCase);
            _importedDirTimestamps = new Dictionary<string, DateTime>(srr.ArchivedDirectoryTimestamps, StringComparer.OrdinalIgnoreCase);
            _importedDirCreationTimes = new Dictionary<string, DateTime>(srr.ArchivedDirectoryCreationTimes, StringComparer.OrdinalIgnoreCase);
            _importedDirAccessTimes = new Dictionary<string, DateTime>(srr.ArchivedDirectoryAccessTimes, StringComparer.OrdinalIgnoreCase);
            _importedFileTimestamps = new Dictionary<string, DateTime>(srr.ArchivedFileTimestamps, StringComparer.OrdinalIgnoreCase);
            _importedFileCreationTimes = new Dictionary<string, DateTime>(srr.ArchivedFileCreationTimes, StringComparer.OrdinalIgnoreCase);
            _importedFileAccessTimes = new Dictionary<string, DateTime>(srr.ArchivedFileAccessTimes, StringComparer.OrdinalIgnoreCase);
            _importedArchiveFileCrcs = new Dictionary<string, string>(srr.ArchivedFileCrcs, StringComparer.OrdinalIgnoreCase);
            _importedOriginalRarFileNames = srr.RARFiles.Select(r => r.FileName).ToList();
            _importedArchiveComment = srr.ArchiveComment;
            _importedArchiveCommentBytes = srr.ArchiveCommentBytes;
            _importedCmtCompressedData = srr.CmtCompressedData;
            _importedCmtCompressionMethod = srr.CmtCompressionMethod;

            if (_importedArchiveFiles.Count > 0 || _importedArchiveDirectories.Count > 0)
            {
                string dirSuffix = _importedArchiveDirectories.Count > 0 ? $", {_importedArchiveDirectories.Count} dirs" : "";
                Log(LogTarget.System, $"Archive entries: {_importedArchiveFiles.Count} files{dirSuffix}");
            }

            Log(LogTarget.System, $"Per-file timestamps: mtime={_importedFileTimestamps.Count}, ctime={_importedFileCreationTimes.Count}, atime={_importedFileAccessTimes.Count}");

            if (_importedCmtCompressedData is { Length: > 0 })
            {
                Log(LogTarget.System, $"CMT data: {_importedCmtCompressedData.Length} bytes — Phase 1 enabled");
            }

            // Host OS
            _detectedFileHostOS = srr.DetectedHostOS;
            _detectedFileAttributes = srr.DetectedFileAttributes;
            _detectedCmtHostOS = srr.CmtHostOS;
            _detectedCmtFileTime = srr.CmtFileTimeDOS;
            _detectedCmtFileAttributes = srr.CmtFileAttributes;
            _detectedLargeFlag = srr.HasLargeFiles;
            _detectedHighPackSize = srr.DetectedHighPackSize;
            _detectedHighUnpSize = srr.DetectedHighUnpSize;

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

            // Compression method
            if (srr.CompressionMethod.HasValue)
            {
                int method = srr.CompressionMethod.Value;
                if (method is >= 0 and <= 5)
                {
                    SwitchM0 = method == 0;
                    SwitchM1 = method == 1;
                    SwitchM2 = method == 2;
                    SwitchM3 = method == 3;
                    SwitchM4 = method == 4;
                    SwitchM5 = method == 5;
                    string[] names = ["Store", "Fastest", "Fast", "Normal", "Good", "Best"];
                    Log(LogTarget.System, $"Compression: -m{method} ({names[method]})");
                }
            }

            // Dictionary size
            if (srr.DictionarySize.HasValue)
            {
                SwitchMD64K = SwitchMD128K = SwitchMD256K = SwitchMD512K = false;
                SwitchMD1024K = SwitchMD2048K = SwitchMD4096K = false;
                SwitchMD8M = SwitchMD16M = SwitchMD32M = SwitchMD64M = false;
                SwitchMD128M = SwitchMD256M = SwitchMD512M = SwitchMD1G = false;

                switch (srr.DictionarySize.Value)
                {
                    case 64:
                        SwitchMD64K = true;
                        break;
                    case 128:
                        SwitchMD128K = true;
                        break;
                    case 256:
                        SwitchMD256K = true;
                        break;
                    case 512:
                        SwitchMD512K = true;
                        break;
                    case 1024:
                        SwitchMD1024K = true;
                        break;
                    case 2048:
                        SwitchMD2048K = true;
                        break;
                    case 4096:
                        SwitchMD4096K = true;
                        break;
                }

                Log(LogTarget.System, $"Dictionary: {srr.DictionarySize.Value} KB");
            }

            // Solid archive
            if (srr.IsSolidArchive.HasValue)
            {
                SwitchSDash = !srr.IsSolidArchive.Value;
            }

            // Archive format
            if (srr.RARVersion.HasValue)
            {
                SwitchMA4 = false;
                SwitchMA5 = false;
                if (srr.RARVersion.Value < 50)
                {
                    SwitchMA4 = true;
                    Log(LogTarget.System, "Archive format: RAR4 (-ma4)");
                }
                else if (srr.RARVersion.Value < 70)
                {
                    SwitchMA5 = true;
                    Log(LogTarget.System, "Archive format: RAR5 (-ma5)");
                }
                else
                {
                    Log(LogTarget.System, "Archive format: RAR7");
                }
            }

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
        }
        catch (Exception ex)
        {
            Log(LogTarget.System, $"Failed to import SRR: {ex.Message}");
        }
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

    private ReconstructorConfig CaptureConfig() => new()
    {
        WinRarPath = WinRarPath,
        ReleasePath = ReleasePath,
        VerificationPath = VerificationPath,
        OutputPath = OutputPath,

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

        FileA = FileA,
        FileI = FileI,

        DeleteRARFiles = DeleteRARFiles,
        DeleteDuplicateCRCFiles = DeleteDuplicateCRCFiles,
        StopOnFirstMatch = StopOnFirstMatch,
        CompleteAllVolumes = CompleteAllVolumes,
        RenameToOriginal = RenameToOriginal,

        EnableHostOSPatching = EnableHostOSPatching,

        ImportedSrr = CaptureImportedSrrState()
    };

    private ImportedSrrState? CaptureImportedSrrState()
    {
        bool hasState = _importedArchiveFiles.Count > 0
            || _importedArchiveDirectories.Count > 0
            || _importedFileTimestamps.Count > 0
            || _importedArchiveFileCrcs.Count > 0
            || _importedSRRFilePath is not null
            || _importedCmtCompressedData is { Length: > 0 };

        if (!hasState)
        {
            return null;
        }

        return new ImportedSrrState
        {
            SRRFilePath = _importedSRRFilePath,
            ArchiveFiles = [.. _importedArchiveFiles],
            ArchiveDirectories = [.. _importedArchiveDirectories],
            DirTimestamps = new Dictionary<string, DateTime>(_importedDirTimestamps),
            DirCreationTimes = new Dictionary<string, DateTime>(_importedDirCreationTimes),
            DirAccessTimes = new Dictionary<string, DateTime>(_importedDirAccessTimes),
            FileTimestamps = new Dictionary<string, DateTime>(_importedFileTimestamps),
            FileCreationTimes = new Dictionary<string, DateTime>(_importedFileCreationTimes),
            FileAccessTimes = new Dictionary<string, DateTime>(_importedFileAccessTimes),
            ArchiveFileCrcs = new Dictionary<string, string>(_importedArchiveFileCrcs),
            OriginalRarFileNames = [.. _importedOriginalRarFileNames],
            ArchiveComment = _importedArchiveComment,
            ArchiveCommentBytes = _importedArchiveCommentBytes,
            CmtCompressedData = _importedCmtCompressedData,
            CmtCompressionMethod = _importedCmtCompressionMethod,
            DetectedFileHostOS = _detectedFileHostOS,
            DetectedFileAttributes = _detectedFileAttributes,
            DetectedCmtHostOS = _detectedCmtHostOS,
            DetectedCmtFileTime = _detectedCmtFileTime,
            DetectedCmtFileAttributes = _detectedCmtFileAttributes,
            DetectedLargeFlag = _detectedLargeFlag,
            DetectedHighPackSize = _detectedHighPackSize,
            DetectedHighUnpSize = _detectedHighUnpSize,
            CustomPackerType = _importedCustomPackerType.ToString(),
            CustomPackerWarning = CustomPackerWarning
        };
    }

    private void ApplyConfig(ReconstructorConfig c)
    {
        WinRarPath = c.WinRarPath;
        ReleasePath = c.ReleasePath;
        VerificationPath = c.VerificationPath;
        OutputPath = c.OutputPath;

        Version2 = c.Version2;
        Version3 = c.Version3;
        Version4 = c.Version4;
        Version5 = c.Version5;
        Version6 = c.Version6;
        Version7 = c.Version7;

        SwitchM0 = c.SwitchM0;
        SwitchM1 = c.SwitchM1;
        SwitchM2 = c.SwitchM2;
        SwitchM3 = c.SwitchM3;
        SwitchM4 = c.SwitchM4;
        SwitchM5 = c.SwitchM5;

        SwitchMA4 = c.SwitchMA4;
        SwitchMA5 = c.SwitchMA5;

        SwitchMD64K = c.SwitchMD64K;
        SwitchMD128K = c.SwitchMD128K;
        SwitchMD256K = c.SwitchMD256K;
        SwitchMD512K = c.SwitchMD512K;
        SwitchMD1024K = c.SwitchMD1024K;
        SwitchMD2048K = c.SwitchMD2048K;
        SwitchMD4096K = c.SwitchMD4096K;
        SwitchMD8M = c.SwitchMD8M;
        SwitchMD16M = c.SwitchMD16M;
        SwitchMD32M = c.SwitchMD32M;
        SwitchMD64M = c.SwitchMD64M;
        SwitchMD128M = c.SwitchMD128M;
        SwitchMD256M = c.SwitchMD256M;
        SwitchMD512M = c.SwitchMD512M;
        SwitchMD1G = c.SwitchMD1G;

        SwitchTSM0 = c.SwitchTSM0;
        SwitchTSM1 = c.SwitchTSM1;
        SwitchTSM2 = c.SwitchTSM2;
        SwitchTSM3 = c.SwitchTSM3;
        SwitchTSM4 = c.SwitchTSM4;
        SwitchTSC0 = c.SwitchTSC0;
        SwitchTSC1 = c.SwitchTSC1;
        SwitchTSC2 = c.SwitchTSC2;
        SwitchTSC3 = c.SwitchTSC3;
        SwitchTSC4 = c.SwitchTSC4;
        SwitchTSA0 = c.SwitchTSA0;
        SwitchTSA1 = c.SwitchTSA1;
        SwitchTSA2 = c.SwitchTSA2;
        SwitchTSA3 = c.SwitchTSA3;
        SwitchTSA4 = c.SwitchTSA4;

        SwitchAI = c.SwitchAI;
        SwitchR = c.SwitchR;
        SwitchDS = c.SwitchDS;
        SwitchSDash = c.SwitchSDash;
        SwitchMT = c.SwitchMT;
        SwitchMTStart = c.SwitchMTStart;
        SwitchMTEnd = c.SwitchMTEnd;

        SwitchV = c.SwitchV;
        VolumeSize = c.VolumeSize;
        VolumeSizeUnitIndex = c.VolumeSizeUnitIndex;
        UseOldVolumeNaming = c.UseOldVolumeNaming;

        FileA = c.FileA;
        FileI = c.FileI;

        DeleteRARFiles = c.DeleteRARFiles;
        DeleteDuplicateCRCFiles = c.DeleteDuplicateCRCFiles;
        StopOnFirstMatch = c.StopOnFirstMatch;
        CompleteAllVolumes = c.CompleteAllVolumes;
        RenameToOriginal = c.RenameToOriginal;

        EnableHostOSPatching = c.EnableHostOSPatching;

        ApplyImportedSrrState(c.ImportedSrr);
    }

    private void ApplyImportedSrrState(ImportedSrrState? s)
    {
        // Always reset — an absent block means "no SRR imported"
        _importedSRRFilePath = s?.SRRFilePath;
        _importedArchiveFiles = s is null
            ? new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(s.ArchiveFiles, StringComparer.OrdinalIgnoreCase);
        _importedArchiveDirectories = s is null
            ? new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(s.ArchiveDirectories, StringComparer.OrdinalIgnoreCase);

        _importedDirTimestamps = ToCi(s?.DirTimestamps);
        _importedDirCreationTimes = ToCi(s?.DirCreationTimes);
        _importedDirAccessTimes = ToCi(s?.DirAccessTimes);
        _importedFileTimestamps = ToCi(s?.FileTimestamps);
        _importedFileCreationTimes = ToCi(s?.FileCreationTimes);
        _importedFileAccessTimes = ToCi(s?.FileAccessTimes);

        _importedArchiveFileCrcs = s is null
            ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, string>(s.ArchiveFileCrcs, StringComparer.OrdinalIgnoreCase);

        _importedOriginalRarFileNames = s?.OriginalRarFileNames is { } names ? [.. names] : [];
        _importedArchiveComment = s?.ArchiveComment;
        _importedArchiveCommentBytes = s?.ArchiveCommentBytes;
        _importedCmtCompressedData = s?.CmtCompressedData;
        _importedCmtCompressionMethod = s?.CmtCompressionMethod;

        _detectedFileHostOS = s?.DetectedFileHostOS;
        _detectedFileAttributes = s?.DetectedFileAttributes;
        _detectedCmtHostOS = s?.DetectedCmtHostOS;
        _detectedCmtFileTime = s?.DetectedCmtFileTime;
        _detectedCmtFileAttributes = s?.DetectedCmtFileAttributes;
        _detectedLargeFlag = s?.DetectedLargeFlag;
        _detectedHighPackSize = s?.DetectedHighPackSize;
        _detectedHighUnpSize = s?.DetectedHighUnpSize;

        _importedCustomPackerType = Enum.TryParse(s?.CustomPackerType, out CustomPackerType packer) ? packer : CustomPackerType.None;
        CustomPackerWarning = s?.CustomPackerWarning;

        if (s is not null)
        {
            Log(LogTarget.System, $"Restored SRR state: {_importedArchiveFiles.Count} files, mtime={_importedFileTimestamps.Count}, CRCs={_importedArchiveFileCrcs.Count}, CMT={(_importedCmtCompressedData?.Length ?? 0)} bytes");
        }

        static Dictionary<string, DateTime> ToCi(Dictionary<string, DateTime>? src) =>
            src is null
                ? new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, DateTime>(src, StringComparer.OrdinalIgnoreCase);
    }

    // ── Start / Stop ──

    private bool CanStart() => !IsRunning
        && !string.IsNullOrWhiteSpace(WinRarPath)
        && !string.IsNullOrWhiteSpace(ReleasePath)
        && !string.IsNullOrWhiteSpace(OutputPath);

    [RelayCommand(CanExecute = nameof(CanStart))]
    private async Task StartAsync()
    {
        // ── Path validation ──

        if (string.IsNullOrWhiteSpace(WinRarPath))
        {
            Log(LogTarget.System, "Invalid WinRAR directory.");
            MessageBox.Show("Invalid WinRAR directory.", "Validation Error", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        if (!Directory.Exists(WinRarPath))
        {
            Log(LogTarget.System, "WinRAR directory does not exist.");
            MessageBox.Show("WinRAR directory does not exist.", "Validation Error", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        if (string.IsNullOrWhiteSpace(ReleasePath))
        {
            Log(LogTarget.System, "Invalid release directory.");
            MessageBox.Show("Invalid release directory.", "Validation Error", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        if (!Directory.Exists(ReleasePath))
        {
            Log(LogTarget.System, "Release directory does not exist.");
            MessageBox.Show("Release directory does not exist.", "Validation Error", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        // ── Subdirectory timestamp warning ──

        if (Directory.EnumerateDirectories(ReleasePath).Any() && _importedDirTimestamps.Count == 0)
        {
            bool proceed = await _fileDialog.ShowConfirmAsync("Warning: modified date",
                "Release directory contains one or more subdirectories.\n" +
                "RAR file(s) preserve the modified date of files and subdirectories.\n" +
                "This means that if one or more subdirectories have been created manually, " +
                "the modified date will be different than the modified date of the directory in the original archive.\n" +
                "In this case, there is no chance of properly recreating the RAR file(s).\n\n" +
                "Are you sure the modified date of the file(s) and subdirectories are correct?");
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
            MessageBox.Show("Invalid verification file path.", "Validation Error", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        if (!File.Exists(VerificationPath))
        {
            Log(LogTarget.System, "Verification file does not exist.");
            MessageBox.Show("Verification file does not exist.", "Validation Error", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        string verificationExt = Path.GetExtension(VerificationPath).ToLowerInvariant();
        if (verificationExt is not ".sfv" and not ".sha1")
        {
            Log(LogTarget.System, "Invalid verification file type.");
            MessageBox.Show("Invalid verification file type. Use .sfv or .sha1 files.", "Validation Error", MessageBoxButton.OK, MessageBoxImage.Error);
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
            MessageBox.Show($"Failed to parse verification file:\n{ex.Message}", "Validation Error", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        if (hashCount == 0)
        {
            Log(LogTarget.System, "No hashes found in verification file.");
            MessageBox.Show("No hashes found in verification file.", "Validation Error", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        // ── Input file existence check ──
        //
        // The verify file (.sfv/.sha1) lists the OUTPUT archives we're trying to produce,
        // so it isn't useful as an input check. The imported SRR's archived files ARE the
        // expected input contents — verify those exist in the release directory. If no SRR
        // has been imported, skip this pre-flight; Manager.ValidateInputFiles will run later.
        if (_importedArchiveFiles.Count > 0)
        {
            try
            {
                var missingFiles = new List<string>();
                foreach (string archiveFile in _importedArchiveFiles)
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
                    MessageBox.Show(
                        $"The following {missingFiles.Count} file(s) listed in the imported SRR are missing from the release directory:\n\n{fileList}\n\nThe release directory should contain the unpacked archive contents (the files that originally went into the RARs).",
                        "Missing Input Files",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);
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
            MessageBox.Show("Invalid output directory.", "Validation Error", MessageBoxButton.OK, MessageBoxImage.Error);
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
                MessageBox.Show($"Failed to create output directory:\n{ex.Message}", "Validation Error", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }
        }
        else if (Directory.EnumerateFileSystemEntries(OutputPath).Any())
        {
            bool proceed = await _fileDialog.ShowConfirmAsync("Output Directory Not Empty",
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
                MessageBox.Show($"Failed to clean output directory:\n{ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }
        }

        // ── Start brute-force ──

        IsRunning = true;
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
        _lastSecondsPerOperation = 0;
        _lastOperationRemaining = 0;
        _stopwatch.Restart();
        _elapsedTimer.Start();
        VersionEntries.Clear();
        _lastPhaseDescription = "";
        _activeVersionIndex = -1;
        _activeVersionKey = "";

        _cts = new CancellationTokenSource();

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
            bool success = await Task.Run(() => _bruteForceService.RunAsync(options));

            // Mark final version entry
            if (_activeVersionIndex >= 0 && _activeVersionIndex < VersionEntries.Count)
            {
                VersionEntries[_activeVersionIndex].Status = "Complete";
                VersionEntries[_activeVersionIndex].Result = success ? "Match" : "No Match";
            }

            ProgressMessage = success ? "Match found!" : "No match found.";
            PhaseDescription = success ? "Complete \u2014 Match Found!" : "Complete \u2014 No Match";
            ProgressPercent = 100;
            ProgressPercentText = "100%";
            if (_lastOperationSize > 0)
            {
                TestCountText = $"Test {_lastOperationSize:N0} of {_lastOperationSize:N0}";
            }
            Log(LogTarget.System, success ? "Brute-force completed: match found!" : "Brute-force completed: no match.");
        }
        catch (OperationCanceledException)
        {
            if (_activeVersionIndex >= 0 && _activeVersionIndex < VersionEntries.Count)
            {
                VersionEntries[_activeVersionIndex].Status = "Cancelled";
            }

            ProgressMessage = "Cancelled.";
            PhaseDescription = "Cancelled";
            Log(LogTarget.System, "Brute-force cancelled by user.");
        }
        catch (Exception ex)
        {
            if (_activeVersionIndex >= 0 && _activeVersionIndex < VersionEntries.Count)
            {
                VersionEntries[_activeVersionIndex].Status = "Error";
            }

            ProgressMessage = "Error.";
            PhaseDescription = "Error";
            Log(LogTarget.System, $"Error: {ex.Message}");
        }
        finally
        {
            _elapsedTimer.Stop();
            _stopwatch.Stop();
            ElapsedText = FormatTimeSpan(_stopwatch.Elapsed);
            IsRunning = false;
            _cts?.Dispose();
            _cts = null;
        }
    }

    [RelayCommand]
    private void Stop()
    {
        _cts?.Cancel();
        _bruteForceService.Stop();
        Log(LogTarget.System, "Cancellation requested...");
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
        List<VersionRange> rarVersions = [];
        if (Version2)
        {
            rarVersions.Add(new(200, 300));
        }

        if (Version3)
        {
            rarVersions.Add(new(300, 400));
        }

        if (Version4)
        {
            rarVersions.Add(new(400, 500));
        }

        if (Version5)
        {
            rarVersions.Add(new(500, 600));
        }

        if (Version6)
        {
            rarVersions.Add(new(600, 700));
        }

        if (Version7)
        {
            rarVersions.Add(new(700, 800));
        }

        return new()
        {
            SetFileArchiveAttribute = ToTriState(FileA),
            SetFileNotContentIndexedAttribute = ToTriState(FileI),
            CommandLineArguments = BuildCommandLineArguments(),
            RARVersions = rarVersions,
            DeleteRARFiles = DeleteRARFiles,
            DeleteDuplicateCRCFiles = DeleteDuplicateCRCFiles,
            StopOnFirstMatch = StopOnFirstMatch,
            CompleteAllVolumes = CompleteAllVolumes,
            RenameToOriginalNames = RenameToOriginal,
            OriginalRarFileNames = _importedOriginalRarFileNames,
            ArchiveFileCrcs = new Dictionary<string, string>(_importedArchiveFileCrcs, StringComparer.OrdinalIgnoreCase),
            ArchiveFilePaths = new HashSet<string>(_importedArchiveFiles, StringComparer.OrdinalIgnoreCase),
            ArchiveDirectoryPaths = new HashSet<string>(_importedArchiveDirectories, StringComparer.OrdinalIgnoreCase),
            DirectoryTimestamps = new Dictionary<string, DateTime>(_importedDirTimestamps, StringComparer.OrdinalIgnoreCase),
            DirectoryCreationTimes = new Dictionary<string, DateTime>(_importedDirCreationTimes, StringComparer.OrdinalIgnoreCase),
            DirectoryAccessTimes = new Dictionary<string, DateTime>(_importedDirAccessTimes, StringComparer.OrdinalIgnoreCase),
            FileTimestamps = new Dictionary<string, DateTime>(_importedFileTimestamps, StringComparer.OrdinalIgnoreCase),
            FileCreationTimes = new Dictionary<string, DateTime>(_importedFileCreationTimes, StringComparer.OrdinalIgnoreCase),
            FileAccessTimes = new Dictionary<string, DateTime>(_importedFileAccessTimes, StringComparer.OrdinalIgnoreCase),
            ArchiveComment = _importedArchiveComment,
            ArchiveCommentBytes = _importedArchiveCommentBytes,
            CmtCompressedData = _importedCmtCompressedData,
            CmtCompressionMethod = _importedCmtCompressionMethod,
            EnableHostOSPatching = EnableHostOSPatching,
            DetectedFileHostOS = _detectedFileHostOS,
            DetectedFileAttributes = _detectedFileAttributes,
            DetectedCmtHostOS = _detectedCmtHostOS,
            DetectedCmtFileTime = _detectedCmtFileTime,
            DetectedCmtFileAttributes = _detectedCmtFileAttributes,
            DetectedLargeFlag = _detectedLargeFlag,
            DetectedHighPackSize = _detectedHighPackSize,
            DetectedHighUnpSize = _detectedHighUnpSize,
            UseOldVolumeNaming = UseOldVolumeNaming,
            CustomPackerDetected = _importedCustomPackerType,
            SRRFilePath = _importedSRRFilePath
        };
    }

    private List<RARCommandLineArgument[]> BuildCommandLineArguments()
    {
        List<RARCommandLineArgument> compressionLevels = [];
        if (SwitchM0)
        {
            compressionLevels.Add(new("-m0", 200));
        }

        if (SwitchM1)
        {
            compressionLevels.Add(new("-m1", 200));
        }

        if (SwitchM2)
        {
            compressionLevels.Add(new("-m2", 200));
        }

        if (SwitchM3)
        {
            compressionLevels.Add(new("-m3", 200));
        }

        if (SwitchM4)
        {
            compressionLevels.Add(new("-m4", 200));
        }

        if (SwitchM5)
        {
            compressionLevels.Add(new("-m5", 200));
        }

        List<RARCommandLineArgument> archiveFormats = [];
        if (SwitchMA4)
        {
            archiveFormats.Add(new("-ma4", 500, 699));
        }

        if (SwitchMA5)
        {
            archiveFormats.Add(new("-ma5", 500, 699));
        }

        List<RARCommandLineArgument> dictSizes = [];
        if (SwitchMD64K)
        {
            dictSizes.Add(new("-md64k", 200, RARArchiveVersion.RAR4));
        }

        if (SwitchMD128K)
        {
            dictSizes.Add(new("-md128k", 200, RARArchiveVersion.RAR4 | RARArchiveVersion.RAR5 | RARArchiveVersion.RAR7));
        }

        if (SwitchMD256K)
        {
            dictSizes.Add(new("-md256k", 200, RARArchiveVersion.RAR4 | RARArchiveVersion.RAR5 | RARArchiveVersion.RAR7));
        }

        if (SwitchMD512K)
        {
            dictSizes.Add(new("-md512k", 200, RARArchiveVersion.RAR4 | RARArchiveVersion.RAR5 | RARArchiveVersion.RAR7));
        }

        if (SwitchMD1024K)
        {
            dictSizes.Add(new("-md1024k", 200, RARArchiveVersion.RAR4 | RARArchiveVersion.RAR5 | RARArchiveVersion.RAR7));
        }

        if (SwitchMD2048K)
        {
            dictSizes.Add(new("-md2048k", 200, RARArchiveVersion.RAR4 | RARArchiveVersion.RAR5 | RARArchiveVersion.RAR7));
        }

        if (SwitchMD4096K)
        {
            dictSizes.Add(new("-md4096k", 200, RARArchiveVersion.RAR4 | RARArchiveVersion.RAR5 | RARArchiveVersion.RAR7));
        }

        if (SwitchMD8M)
        {
            dictSizes.Add(new("-md8m", 500, RARArchiveVersion.RAR5 | RARArchiveVersion.RAR7));
        }

        if (SwitchMD16M)
        {
            dictSizes.Add(new("-md16m", 500, RARArchiveVersion.RAR5 | RARArchiveVersion.RAR7));
        }

        if (SwitchMD32M)
        {
            dictSizes.Add(new("-md32m", 500, RARArchiveVersion.RAR5 | RARArchiveVersion.RAR7));
        }

        if (SwitchMD64M)
        {
            dictSizes.Add(new("-md64m", 500, RARArchiveVersion.RAR5 | RARArchiveVersion.RAR7));
        }

        if (SwitchMD128M)
        {
            dictSizes.Add(new("-md128m", 500, RARArchiveVersion.RAR5 | RARArchiveVersion.RAR7));
        }

        if (SwitchMD256M)
        {
            dictSizes.Add(new("-md256m", 500, RARArchiveVersion.RAR5 | RARArchiveVersion.RAR7));
        }

        if (SwitchMD512M)
        {
            dictSizes.Add(new("-md512m", 500, RARArchiveVersion.RAR5 | RARArchiveVersion.RAR7));
        }

        if (SwitchMD1G)
        {
            dictSizes.Add(new("-md1g", 500, RARArchiveVersion.RAR5 | RARArchiveVersion.RAR7));
        }

        List<RARCommandLineArgument> mtimes = [];
        if (SwitchTSM0)
        {
            mtimes.Add(new("-tsm0", 320, RARArchiveVersion.RAR4 | RARArchiveVersion.RAR5 | RARArchiveVersion.RAR7));
        }

        if (SwitchTSM1)
        {
            mtimes.Add(new("-tsm1", 320, RARArchiveVersion.RAR4 | RARArchiveVersion.RAR5 | RARArchiveVersion.RAR7));
        }

        if (SwitchTSM2)
        {
            mtimes.Add(new("-tsm2", 320, RARArchiveVersion.RAR4));
        }

        if (SwitchTSM3)
        {
            mtimes.Add(new("-tsm3", 320, RARArchiveVersion.RAR4));
        }

        if (SwitchTSM4)
        {
            mtimes.Add(new("-tsm4", 320, RARArchiveVersion.RAR4));
        }

        List<RARCommandLineArgument> ctimes = [];
        if (SwitchTSC0)
        {
            ctimes.Add(new("-tsc0", 320, RARArchiveVersion.RAR4 | RARArchiveVersion.RAR5 | RARArchiveVersion.RAR7));
        }

        if (SwitchTSC1)
        {
            ctimes.Add(new("-tsc1", 320, RARArchiveVersion.RAR4 | RARArchiveVersion.RAR5 | RARArchiveVersion.RAR7));
        }

        if (SwitchTSC2)
        {
            ctimes.Add(new("-tsc2", 320, RARArchiveVersion.RAR4));
        }

        if (SwitchTSC3)
        {
            ctimes.Add(new("-tsc3", 320, RARArchiveVersion.RAR4));
        }

        if (SwitchTSC4)
        {
            ctimes.Add(new("-tsc4", 320, RARArchiveVersion.RAR4));
        }

        List<RARCommandLineArgument> atimes = [];
        if (SwitchTSA0)
        {
            atimes.Add(new("-tsa0", 320, RARArchiveVersion.RAR4 | RARArchiveVersion.RAR5 | RARArchiveVersion.RAR7));
        }

        if (SwitchTSA1)
        {
            atimes.Add(new("-tsa1", 320, RARArchiveVersion.RAR4 | RARArchiveVersion.RAR5 | RARArchiveVersion.RAR7));
        }

        if (SwitchTSA2)
        {
            atimes.Add(new("-tsa2", 320, RARArchiveVersion.RAR4));
        }

        if (SwitchTSA3)
        {
            atimes.Add(new("-tsa3", 320, RARArchiveVersion.RAR4));
        }

        if (SwitchTSA4)
        {
            atimes.Add(new("-tsa4", 320, RARArchiveVersion.RAR4));
        }

        List<RARCommandLineArgument[]> result = [];

        for (int a = 0; a < Math.Max(compressionLevels.Count, 1); a++)
        {
            for (int b = 0; b < Math.Max(archiveFormats.Count, 1); b++)
            {
                for (int c = 0; c < Math.Max(dictSizes.Count, 1); c++)
                {
                    for (int d = 0; d < Math.Max(mtimes.Count, 1); d++)
                    {
                        for (int e = 0; e < Math.Max(ctimes.Count, 1); e++)
                        {
                            for (int f = 0; f < Math.Max(atimes.Count, 1); f++)
                            {
                                for (int x = 0; x < (SwitchAI ? 2 : 1); x++)
                                {
                                    for (int z = SwitchMT ? SwitchMTStart : 0; z < (SwitchMT ? SwitchMTEnd + 1 : 1); z++)
                                    {
                                        List<RARCommandLineArgument> switches = [new("a", 200)];

                                        if (x == 0 && SwitchAI)
                                        {
                                            switches.Add(new("-ai", 390));
                                        }

                                        if (SwitchR)
                                        {
                                            switches.Add(new("-r", 200));
                                        }

                                        if (SwitchDS)
                                        {
                                            switches.Add(new("-ds", 200));
                                        }

                                        if (SwitchSDash)
                                        {
                                            switches.Add(new("-s-", 201));
                                        }

                                        if (compressionLevels.Count > 0)
                                        {
                                            switches.Add(compressionLevels[a]);
                                        }

                                        if (archiveFormats.Count > 0)
                                        {
                                            switches.Add(archiveFormats[b]);
                                        }

                                        if (dictSizes.Count > 0)
                                        {
                                            switches.Add(dictSizes[c]);
                                        }

                                        if (mtimes.Count > 0)
                                        {
                                            switches.Add(mtimes[d]);
                                        }

                                        if (ctimes.Count > 0)
                                        {
                                            switches.Add(ctimes[e]);
                                        }

                                        if (atimes.Count > 0)
                                        {
                                            switches.Add(atimes[f]);
                                        }

                                        if (SwitchV)
                                        {
                                            string volumeArg = BuildVolumeArgument();
                                            switches.Add(new(volumeArg, 200));
                                            if (UseOldVolumeNaming)
                                            {
                                                switches.Add(new("-vn", 300, 699));
                                            }
                                        }

                                        if (SwitchMT)
                                        {
                                            switches.Add(new($"-mt{z}", 360));
                                        }

                                        result.Add([.. switches]);
                                    }
                                }
                            }
                        }
                    }
                }
            }
        }

        return result;
    }

    private string BuildVolumeArgument()
    {
        if (!long.TryParse(VolumeSize, out long sizeValue))
        {
            sizeValue = DefaultVolumeSizeKb;
        }

        return VolumeSizeUnitIndex switch
        {
            0 => $"-v{sizeValue}b",       // Bytes
            1 => $"-v{sizeValue}",         // KB (no suffix, ×1000)
            2 => $"-v{sizeValue * 1000}",  // MB → KB
            3 => $"-v{sizeValue * 1000 * 1000}", // GB → KB
            4 => $"-v{sizeValue}k",        // KiB (k suffix, ×1024)
            5 => $"-v{sizeValue * 1024}k", // MiB → KiB
            6 => $"-v{sizeValue * 1024 * 1024}k", // GiB → KiB
            _ => $"-v{sizeValue}"
        };
    }

    private static TriState ToTriState(bool? value) => value switch
    {
        true => TriState.Checked,
        false => TriState.Unchecked,
        null => TriState.Indeterminate
    };

    // ── Event Handlers ──

    private void OnFileCopyProgress(object? _, FileCopyProgressEventArgs e)
    {
        Application.Current.Dispatcher.BeginInvoke(() =>
        {
            if (!IsCopying)
            {
                IsCopying = true;
                _copyStopwatch.Restart();
            }

            CopyHeadingText = $"Copying {e.TotalFiles} items ({FormatUtilities.FormatSize(e.TotalBytes)})";
            CopySourceText = e.SourceDirectory;
            CopyDestText = e.DestinationDirectory;
            CopyProgressPercent = e.TotalBytes > 0 ? (double)e.BytesCopied / e.TotalBytes * 100.0 : 0;
            CopyProgressPercentText = $"{CopyProgressPercent:F0}%";
            CopyCurrentFileText = e.FileName;

            int remaining = e.TotalFiles - e.FilesCopied;
            long remainingBytes = e.TotalBytes - e.BytesCopied;
            CopyRemainingText = $"Items remaining: {remaining} ({FormatUtilities.FormatSize(remainingBytes)})";

            // Timing stats
            TimeSpan elapsed = _copyStopwatch.Elapsed;
            CopyElapsedText = FormatTimeSpan(elapsed);
            if (e.BytesCopied > 0 && elapsed.TotalSeconds >= 0.5)
            {
                double bytesPerSec = e.BytesCopied / elapsed.TotalSeconds;
                CopySpeedText = FormatSpeed(bytesPerSec);
                if (bytesPerSec > 0 && remainingBytes > 0)
                {
                    var timeRemaining = TimeSpan.FromSeconds(remainingBytes / bytesPerSec);
                    CopyTimeRemainingText = FormatTimeSpan(timeRemaining);
                    CopyEtaText = DateTime.Now.Add(timeRemaining).ToString("HH:mm:ss");
                }
            }

            if (e.FilesCopied >= e.TotalFiles)
            {
                _copyStopwatch.Stop();
                IsCopying = false;
            }
        });
    }


    private static string FormatSpeed(double bytesPerSec)
    {
        if (bytesPerSec >= 1024 * 1024)
        {
            return $"{bytesPerSec / (1024 * 1024):F1} MB/s";
        }

        return $"{bytesPerSec / 1024:F1} KB/s";
    }

    private void OnCRCValidationProgress(object? _, CRCValidationProgressEventArgs e)
    {
        Application.Current.Dispatcher.BeginInvoke(() =>
        {
            if (!IsVerifying)
            {
                IsVerifying = true;
                _verifyStopwatch.Restart();
            }

            VerifyHeadingText = $"Verifying {e.TotalFiles} items ({FormatUtilities.FormatSize(e.TotalBytes)})";
            VerifyProgressPercent = e.TotalBytes > 0 ? (double)e.BytesVerified / e.TotalBytes * 100.0 : 0;
            VerifyProgressPercentText = $"{VerifyProgressPercent:F0}%";
            VerifyCurrentFileText = e.FileName;

            int remaining = e.TotalFiles - e.FilesVerified;
            long remainingBytes = e.TotalBytes - e.BytesVerified;
            VerifyRemainingText = $"Items remaining: {remaining} ({FormatUtilities.FormatSize(remainingBytes)})";

            // Timing stats
            TimeSpan elapsed = _verifyStopwatch.Elapsed;
            VerifyElapsedText = FormatTimeSpan(elapsed);
            if (e.BytesVerified > 0 && elapsed.TotalSeconds >= 0.5)
            {
                double bytesPerSec = e.BytesVerified / elapsed.TotalSeconds;
                VerifySpeedText = FormatSpeed(bytesPerSec);
                if (bytesPerSec > 0 && remainingBytes > 0)
                {
                    var timeRemaining = TimeSpan.FromSeconds(remainingBytes / bytesPerSec);
                    VerifyTimeRemainingText = FormatTimeSpan(timeRemaining);
                    VerifyEtaText = DateTime.Now.Add(timeRemaining).ToString("HH:mm:ss");
                }
            }

            if (e.FilesVerified >= e.TotalFiles)
            {
                _verifyStopwatch.Stop();
                IsVerifying = false;
            }
        });
    }

    private void OnProgress(object? _, BruteForceProgressEventArgs e)
    {
        Application.Current.Dispatcher.Invoke(() =>
        {
            ProgressPercent = e.Progress;
            PhaseDescription = e.PhaseDescription;

            string version = Path.GetFileName(e.RARVersionDirectoryPath);
            ProgressMessage = $"{e.PhaseDescription} | {version} | {e.RARCommandLineArguments} | {e.OperationProgressed}/{e.OperationSize}";

            // Progress window fields
            _lastOperationSize = e.OperationSize;
            TestCountText = $"Test {e.OperationProgressed:N0} of {e.OperationSize:N0}";
            ProgressPercentText = $"{e.Progress:F1}%";

            string versionLabel = FormatVersionLabel(version);
            CurrentDetailText = $"{versionLabel}  \u2014  {e.RARCommandLineArguments}";

            // Timing — cache the rate so the timer tick can extrapolate between events
            ElapsedText = FormatTimeSpan(_stopwatch.Elapsed);
            if (e.OperationProgressed > 0)
            {
                _lastSecondsPerOperation = e.TimeElapsed.TotalSeconds / e.OperationProgressed;
                _lastOperationRemaining = e.OperationRemaining;
                RemainingText = FormatTimeSpan(e.TimeRemaining);
                SpeedText = $"{e.OperationSpeed:N0} tests/s";
                EtaText = e.EstimatedFinishDateTime.ToString("HH:mm:ss");
            }

            // Version list tracking
            string phaseDesc = e.PhaseDescription ?? "";
            if (phaseDesc != _lastPhaseDescription)
            {
                VersionEntries.Clear();
                _activeVersionIndex = -1;
                _activeVersionKey = "";
                _lastPhaseDescription = phaseDesc;
            }

            string key = string.Concat(e.RARVersionDirectoryPath, "|", e.RARCommandLineArguments);
            if (key != _activeVersionKey)
            {
                if (_activeVersionIndex >= 0 && _activeVersionIndex < VersionEntries.Count)
                {
                    VersionEntries[_activeVersionIndex].Status = "Complete";
                    VersionEntries[_activeVersionIndex].Result = "No Match";
                }

                VersionEntries.Add(new VersionEntry
                {
                    VersionName = versionLabel,
                    Arguments = e.RARCommandLineArguments,
                });
                _activeVersionIndex = VersionEntries.Count - 1;
                _activeVersionKey = key;
            }
        });
    }

    private void OnElapsedTimerTick()
    {
        ElapsedText = FormatTimeSpan(_stopwatch.Elapsed);

        if (_lastSecondsPerOperation > 0 && _lastOperationRemaining > 0)
        {
            var remaining = TimeSpan.FromSeconds(_lastSecondsPerOperation * _lastOperationRemaining);
            RemainingText = FormatTimeSpan(remaining);
            EtaText = DateTime.Now.Add(remaining).ToString("HH:mm:ss");
        }
    }

    private void OnStatusChanged(object? _, BruteForceStatusChangedEventArgs e)
    {
        Application.Current.Dispatcher.Invoke(() =>
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

        MessageBox.Show(sb.ToString(),
            "Timestamp Preservation Failed",
            MessageBoxButton.OK,
            MessageBoxImage.Warning);
    }

    private void OnLogMessage(object? _, LogEventArgs e) => Application.Current.Dispatcher.Invoke(() => AppendLog(e.Target, e.Message));

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

    private static string FormatVersionLabel(string dirName)
    {
        Match m = VersionLabelRegex().Match(dirName);
        if (!m.Success)
        {
            return dirName;
        }

        string digits = m.Groups[1].Value;
        string beta = m.Groups[2].Value;
        string versionStr = digits.Length >= 3
            ? $"{digits[..^2]}.{digits[^2..]}"
            : $"{digits[..^1]}.{digits[^1..]}";

        return $"WinRAR {versionStr}{beta}";
    }

    private static string FormatTimeSpan(TimeSpan ts)
    {
        if (ts.TotalHours >= 1)
        {
            return $"{(int)ts.TotalHours}:{ts.Minutes:D2}:{ts.Seconds:D2}";
        }

        return $"{ts.Minutes:D2}:{ts.Seconds:D2}";
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
