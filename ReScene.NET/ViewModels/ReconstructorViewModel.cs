using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RAR;
using ReScene.NET.Services;
using SRR;
using Core;
using Core.Cryptography;
using Core.Diagnostics;
using Core.IO;

namespace ReScene.NET.ViewModels;

public partial class ReconstructorViewModel : ViewModelBase
{
    private readonly IBruteForceService _bruteForceService;
    private readonly IFileDialogService _fileDialog;
    private CancellationTokenSource? _cts;

    // Elapsed timer — ticks every second so the clock doesn't freeze between progress events
    private readonly DispatcherTimer _elapsedTimer;
    private readonly Stopwatch _stopwatch = new();
    private double _lastSecondsPerOperation; // cached rate from last progress event
    private long _lastOperationRemaining;    // cached remaining count from last progress event

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
    private string? _importedSrrFilePath;

    public ReconstructorViewModel(IBruteForceService bruteForceService, IFileDialogService fileDialog)
    {
        _bruteForceService = bruteForceService;
        _fileDialog = fileDialog;

        _bruteForceService.Progress += OnProgress;
        _bruteForceService.StatusChanged += OnStatusChanged;
        _bruteForceService.LogMessage += OnLogMessage;
        _bruteForceService.FileCopyProgress += OnFileCopyProgress;
        _bruteForceService.CrcValidationProgress += OnCrcValidationProgress;

        _elapsedTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _elapsedTimer.Tick += (_, _) => OnElapsedTimerTick();
    }

    // ── Warning ──

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasCustomPackerWarning))]
    private string? _customPackerWarning;

    public bool HasCustomPackerWarning => !string.IsNullOrEmpty(CustomPackerWarning);

    // ── Paths ──

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartCommand))]
    private string _winRarPath = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartCommand))]
    private string _releasePath = string.Empty;

    [ObservableProperty]
    private string _verificationPath = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartCommand))]
    private string _outputPath = string.Empty;

    // ── Progress ──

    [ObservableProperty]
    private double _progressPercent;

    [ObservableProperty]
    private string _progressMessage = string.Empty;

    [ObservableProperty]
    private string _phaseDescription = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartCommand))]
    private bool _isRunning;

    [ObservableProperty]
    private bool _showProgress;

    // ── Progress window state ──

    [ObservableProperty] private string _testCountText = string.Empty;
    [ObservableProperty] private string _progressPercentText = string.Empty;
    [ObservableProperty] private string _currentDetailText = string.Empty;
    [ObservableProperty] private string _elapsedText = string.Empty;
    [ObservableProperty] private string _remainingText = string.Empty;
    [ObservableProperty] private string _speedText = string.Empty;
    [ObservableProperty] private string _etaText = string.Empty;

    public ObservableCollection<VersionEntry> VersionEntries { get; } = [];

    // ── File copy progress window state ──

    private readonly Stopwatch _copyStopwatch = new();

    [ObservableProperty] private bool _isCopying;
    [ObservableProperty] private string _copyHeadingText = string.Empty;
    [ObservableProperty] private string _copySourceText = string.Empty;
    [ObservableProperty] private string _copyDestText = string.Empty;
    [ObservableProperty] private double _copyProgressPercent;
    [ObservableProperty] private string _copyProgressPercentText = string.Empty;
    [ObservableProperty] private string _copyCurrentFileText = string.Empty;
    [ObservableProperty] private string _copyRemainingText = string.Empty;
    [ObservableProperty] private string _copyElapsedText = string.Empty;
    [ObservableProperty] private string _copySpeedText = string.Empty;
    [ObservableProperty] private string _copyTimeRemainingText = string.Empty;
    [ObservableProperty] private string _copyEtaText = string.Empty;

    // ── CRC validation progress window state ──

    private readonly Stopwatch _verifyStopwatch = new();

    [ObservableProperty] private bool _isVerifying;
    [ObservableProperty] private string _verifyHeadingText = string.Empty;
    [ObservableProperty] private double _verifyProgressPercent;
    [ObservableProperty] private string _verifyProgressPercentText = string.Empty;
    [ObservableProperty] private string _verifyCurrentFileText = string.Empty;
    [ObservableProperty] private string _verifyRemainingText = string.Empty;
    [ObservableProperty] private string _verifyElapsedText = string.Empty;
    [ObservableProperty] private string _verifySpeedText = string.Empty;
    [ObservableProperty] private string _verifyTimeRemainingText = string.Empty;
    [ObservableProperty] private string _verifyEtaText = string.Empty;

    private string _lastPhaseDescription = "";
    private int _activeVersionIndex = -1;
    private string _activeVersionKey = "";

    [GeneratedRegex(@"(?:win)?(?:rar|wr)(?:-x64|-x32)?-?(\d+)(b\d+)?", RegexOptions.IgnoreCase)]
    private static partial Regex VersionLabelRegex();

    public partial class VersionEntry : ObservableObject
    {
        [ObservableProperty] private string _versionName = "";
        [ObservableProperty] private string _status = "Testing";
        [ObservableProperty] private string _arguments = "";
        [ObservableProperty] private string _result = "";
    }

    // ── Logs ──

    [ObservableProperty] private string _systemLog = string.Empty;
    [ObservableProperty] private string _phase1Log = string.Empty;
    [ObservableProperty] private string _phase2Log = string.Empty;

    // ── RAR Versions ──

    [ObservableProperty] private bool _version2;
    [ObservableProperty] private bool _version3 = true;
    [ObservableProperty] private bool _version4 = true;
    [ObservableProperty] private bool _version5 = true;
    [ObservableProperty] private bool _version6 = true;
    [ObservableProperty] private bool _version7;

    // ── Compression Method ──

    [ObservableProperty] private bool _switchM0;
    [ObservableProperty] private bool _switchM1;
    [ObservableProperty] private bool _switchM2;
    [ObservableProperty] private bool _switchM3 = true;
    [ObservableProperty] private bool _switchM4;
    [ObservableProperty] private bool _switchM5;

    // ── Archive Format ──

    [ObservableProperty] private bool _switchMA4;
    [ObservableProperty] private bool _switchMA5;

    // ── Dictionary Size ──

    [ObservableProperty] private bool _switchMD64K;
    [ObservableProperty] private bool _switchMD128K;
    [ObservableProperty] private bool _switchMD256K;
    [ObservableProperty] private bool _switchMD512K;
    [ObservableProperty] private bool _switchMD1024K;
    [ObservableProperty] private bool _switchMD2048K;
    [ObservableProperty] private bool _switchMD4096K = true;
    [ObservableProperty] private bool _switchMD8M;
    [ObservableProperty] private bool _switchMD16M;
    [ObservableProperty] private bool _switchMD32M;
    [ObservableProperty] private bool _switchMD64M;
    [ObservableProperty] private bool _switchMD128M;
    [ObservableProperty] private bool _switchMD256M;
    [ObservableProperty] private bool _switchMD512M;
    [ObservableProperty] private bool _switchMD1G;

    // ── Timestamps ──

    [ObservableProperty] private bool _switchTSM0;
    [ObservableProperty] private bool _switchTSM1;
    [ObservableProperty] private bool _switchTSM2;
    [ObservableProperty] private bool _switchTSM3;
    [ObservableProperty] private bool _switchTSM4;

    [ObservableProperty] private bool _switchTSC0;
    [ObservableProperty] private bool _switchTSC1;
    [ObservableProperty] private bool _switchTSC2;
    [ObservableProperty] private bool _switchTSC3;
    [ObservableProperty] private bool _switchTSC4;

    [ObservableProperty] private bool _switchTSA0;
    [ObservableProperty] private bool _switchTSA1;
    [ObservableProperty] private bool _switchTSA2;
    [ObservableProperty] private bool _switchTSA3;
    [ObservableProperty] private bool _switchTSA4;

    // ── Other Options ──

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsFileAttributesEnabled))]
    private bool _switchAI;

    [ObservableProperty] private bool _switchR = true;
    [ObservableProperty] private bool _switchDS;
    [ObservableProperty] private bool _switchSDash;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsMTRangeEnabled))]
    private bool _switchMT;

    [ObservableProperty] private int _switchMTStart = 1;
    [ObservableProperty] private int _switchMTEnd = Environment.ProcessorCount;

    // Volume
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsVolumeOptionsEnabled))]
    private bool _switchV;

    [ObservableProperty] private string _volumeSize = "15000";
    [ObservableProperty] private int _volumeSizeUnitIndex = 1; // default KB
    [ObservableProperty] private bool _useOldVolumeNaming;

    public static string[] VolumeSizeUnits { get; } = ["Bytes", "KB", "MB", "GB", "KiB", "MiB", "GiB"];

    // File attributes (null = Indeterminate)
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSwitchAIEnabled))]
    private bool? _fileA = false;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSwitchAIEnabled))]
    private bool? _fileI = false;

    // Output options
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDeleteDuplicateCRCEnabled))]
    private bool _deleteRARFiles;

    [ObservableProperty] private bool _deleteDuplicateCRCFiles = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsRenameToOriginalEnabled))]
    private bool _stopOnFirstMatch = true;

    [ObservableProperty] private bool _completeAllVolumes;
    [ObservableProperty] private bool _renameToOriginal;

    // ── Computed enable/disable ──

    public bool IsMTRangeEnabled => SwitchMT;
    public bool IsVolumeOptionsEnabled => SwitchV;
    public bool IsSwitchAIEnabled => FileA == false && FileI == false;
    public bool IsFileAttributesEnabled => !SwitchAI;
    public bool IsDeleteDuplicateCRCEnabled => !DeleteRARFiles;
    public bool IsRenameToOriginalEnabled => StopOnFirstMatch;

    // Host OS patching
    [ObservableProperty] private bool _enableHostOSPatching;

    // ── Browse Commands ──

    [RelayCommand]
    private async Task BrowseWinRarAsync()
    {
        string? path = await _fileDialog.OpenFolderAsync("Select WinRAR Installations Directory");
        if (path != null)
            WinRarPath = path;
    }

    [RelayCommand]
    private async Task BrowseReleaseAsync()
    {
        string? path = await _fileDialog.OpenFolderAsync("Select Release Directory");
        if (path != null)
            ReleasePath = path;
    }

    [RelayCommand]
    private async Task BrowseVerificationAsync()
    {
        string? path = await _fileDialog.OpenFileAsync("Select Verification File",
            ["SFV Files|*.sfv", "SHA1 Files|*.sha1", "All Files|*.*"]);
        if (path != null)
            VerificationPath = path;
    }

    [RelayCommand]
    private async Task BrowseOutputAsync()
    {
        string? path = await _fileDialog.OpenFolderAsync("Select Output Directory");
        if (path != null)
            OutputPath = path;
    }

    // ── Import SRR ──

    [RelayCommand]
    private async Task ImportSrrAsync()
    {
        string? path = await _fileDialog.OpenFileAsync("Select SRR File",
            ["SRR Files|*.srr", "All Files|*.*"]);
        if (path == null) return;

        try
        {
            Log(LogTarget.System, $"=== SRR Import: {Path.GetFileName(path)} ===");

            var srr = SRRFile.Load(path);
            Log(LogTarget.System, "SRR loaded successfully");

            // Custom packer detection
            if (srr.HasCustomPackerHeaders)
            {
                Log(LogTarget.System, $"Custom RAR packer detected: {srr.CustomPackerDetected}");
                _importedCustomPackerType = srr.CustomPackerDetected;
                _importedSrrFilePath = path;

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
                _importedSrrFilePath = null;
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
            _importedOriginalRarFileNames = srr.RarFiles.Select(r => r.FileName).ToList();
            _importedArchiveComment = srr.ArchiveComment;
            _importedArchiveCommentBytes = srr.ArchiveCommentBytes;
            _importedCmtCompressedData = srr.CmtCompressedData;
            _importedCmtCompressionMethod = srr.CmtCompressionMethod;

            if (_importedArchiveFiles.Count > 0 || _importedArchiveDirectories.Count > 0)
            {
                string dirSuffix = _importedArchiveDirectories.Count > 0 ? $", {_importedArchiveDirectories.Count} dirs" : "";
                Log(LogTarget.System, $"Archive entries: {_importedArchiveFiles.Count} files{dirSuffix}");
            }

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
                if (method >= 0 && method <= 5)
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
                    case 64: SwitchMD64K = true; break;
                    case 128: SwitchMD128K = true; break;
                    case 256: SwitchMD256K = true; break;
                    case 512: SwitchMD512K = true; break;
                    case 1024: SwitchMD1024K = true; break;
                    case 2048: SwitchMD2048K = true; break;
                    case 4096: SwitchMD4096K = true; break;
                }
                Log(LogTarget.System, $"Dictionary: {srr.DictionarySize.Value} KB");
            }

            // Solid archive
            if (srr.IsSolidArchive.HasValue)
                SwitchSDash = !srr.IsSolidArchive.Value;

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
            var mtimePrecision = srr.FileMtimePrecision ?? srr.CmtMtimePrecision;
            var ctimePrecision = srr.FileCtimePrecision ?? srr.CmtCtimePrecision;
            var atimePrecision = srr.FileAtimePrecision ?? srr.CmtAtimePrecision;

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
            if (srr.RarFiles.Count > 1 && srr.VolumeSizeBytes.HasValue)
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
            SetRARVersionsFromSrr(srr);

            // Extract stored SFV for verification
            TryExtractStoredSfv(path, srr);

            Log(LogTarget.System, "=== SRR Import Complete ===");
        }
        catch (Exception ex)
        {
            Log(LogTarget.System, $"Failed to import SRR: {ex.Message}");
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
        if (verificationExt != ".sfv" && verificationExt != ".sha1")
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
                    File.Delete(file);
                foreach (string dir in Directory.GetDirectories(OutputPath))
                    Directory.Delete(dir, true);
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
            var options = BuildBruteForceOptions();

            Log(LogTarget.System, "Starting brute-force...");
            Log(LogTarget.System, $"WinRAR: {WinRarPath}");
            Log(LogTarget.System, $"Release: {ReleasePath}");
            Log(LogTarget.System, $"Output: {OutputPath}");

            // Run entirely on a background thread so the UI stays responsive
            // during setup (directory enumeration, input validation, etc.)
            bool success = await Task.Run(() => _bruteForceService.RunAsync(options, _cts.Token));

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
            Log(LogTarget.System, success ? "Brute-force completed: match found!" : "Brute-force completed: no match.");
        }
        catch (OperationCanceledException)
        {
            if (_activeVersionIndex >= 0 && _activeVersionIndex < VersionEntries.Count)
                VersionEntries[_activeVersionIndex].Status = "Cancelled";
            ProgressMessage = "Cancelled.";
            PhaseDescription = "Cancelled";
            Log(LogTarget.System, "Brute-force cancelled by user.");
        }
        catch (Exception ex)
        {
            if (_activeVersionIndex >= 0 && _activeVersionIndex < VersionEntries.Count)
                VersionEntries[_activeVersionIndex].Status = "Error";
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
                foreach (var entry in sfv.Entries)
                    options.Hashes.Add(entry.CRC);
                options.HashType = HashType.CRC32;
            }
            else if (ext == ".sha1")
            {
                var sha1 = SHA1File.ReadFile(VerificationPath);
                foreach (var entry in sha1.Entries)
                    options.Hashes.Add(entry.SHA1);
                options.HashType = HashType.SHA1;
            }
        }

        return options;
    }

    private RAROptions BuildRAROptions()
    {
        List<VersionRange> rarVersions = [];
        if (Version2) rarVersions.Add(new(200, 300));
        if (Version3) rarVersions.Add(new(300, 400));
        if (Version4) rarVersions.Add(new(400, 500));
        if (Version5) rarVersions.Add(new(500, 600));
        if (Version6) rarVersions.Add(new(600, 700));
        if (Version7) rarVersions.Add(new(700, 800));

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
            SrrFilePath = _importedSrrFilePath
        };
    }

    private List<RARCommandLineArgument[]> BuildCommandLineArguments()
    {
        List<RARCommandLineArgument> compressionLevels = [];
        if (SwitchM0) compressionLevels.Add(new("-m0", 200));
        if (SwitchM1) compressionLevels.Add(new("-m1", 200));
        if (SwitchM2) compressionLevels.Add(new("-m2", 200));
        if (SwitchM3) compressionLevels.Add(new("-m3", 200));
        if (SwitchM4) compressionLevels.Add(new("-m4", 200));
        if (SwitchM5) compressionLevels.Add(new("-m5", 200));

        List<RARCommandLineArgument> archiveFormats = [];
        if (SwitchMA4) archiveFormats.Add(new("-ma4", 500, 699));
        if (SwitchMA5) archiveFormats.Add(new("-ma5", 500, 699));

        List<RARCommandLineArgument> dictSizes = [];
        if (SwitchMD64K) dictSizes.Add(new("-md64k", 200, RARArchiveVersion.RAR4));
        if (SwitchMD128K) dictSizes.Add(new("-md128k", 200, RARArchiveVersion.RAR4 | RARArchiveVersion.RAR5 | RARArchiveVersion.RAR7));
        if (SwitchMD256K) dictSizes.Add(new("-md256k", 200, RARArchiveVersion.RAR4 | RARArchiveVersion.RAR5 | RARArchiveVersion.RAR7));
        if (SwitchMD512K) dictSizes.Add(new("-md512k", 200, RARArchiveVersion.RAR4 | RARArchiveVersion.RAR5 | RARArchiveVersion.RAR7));
        if (SwitchMD1024K) dictSizes.Add(new("-md1024k", 200, RARArchiveVersion.RAR4 | RARArchiveVersion.RAR5 | RARArchiveVersion.RAR7));
        if (SwitchMD2048K) dictSizes.Add(new("-md2048k", 200, RARArchiveVersion.RAR4 | RARArchiveVersion.RAR5 | RARArchiveVersion.RAR7));
        if (SwitchMD4096K) dictSizes.Add(new("-md4096k", 200, RARArchiveVersion.RAR4 | RARArchiveVersion.RAR5 | RARArchiveVersion.RAR7));
        if (SwitchMD8M) dictSizes.Add(new("-md8m", 500, RARArchiveVersion.RAR5 | RARArchiveVersion.RAR7));
        if (SwitchMD16M) dictSizes.Add(new("-md16m", 500, RARArchiveVersion.RAR5 | RARArchiveVersion.RAR7));
        if (SwitchMD32M) dictSizes.Add(new("-md32m", 500, RARArchiveVersion.RAR5 | RARArchiveVersion.RAR7));
        if (SwitchMD64M) dictSizes.Add(new("-md64m", 500, RARArchiveVersion.RAR5 | RARArchiveVersion.RAR7));
        if (SwitchMD128M) dictSizes.Add(new("-md128m", 500, RARArchiveVersion.RAR5 | RARArchiveVersion.RAR7));
        if (SwitchMD256M) dictSizes.Add(new("-md256m", 500, RARArchiveVersion.RAR5 | RARArchiveVersion.RAR7));
        if (SwitchMD512M) dictSizes.Add(new("-md512m", 500, RARArchiveVersion.RAR5 | RARArchiveVersion.RAR7));
        if (SwitchMD1G) dictSizes.Add(new("-md1g", 500, RARArchiveVersion.RAR5 | RARArchiveVersion.RAR7));

        List<RARCommandLineArgument> mtimes = [];
        if (SwitchTSM0) mtimes.Add(new("-tsm0", 320, RARArchiveVersion.RAR4 | RARArchiveVersion.RAR5 | RARArchiveVersion.RAR7));
        if (SwitchTSM1) mtimes.Add(new("-tsm1", 320, RARArchiveVersion.RAR4 | RARArchiveVersion.RAR5 | RARArchiveVersion.RAR7));
        if (SwitchTSM2) mtimes.Add(new("-tsm2", 320, RARArchiveVersion.RAR4));
        if (SwitchTSM3) mtimes.Add(new("-tsm3", 320, RARArchiveVersion.RAR4));
        if (SwitchTSM4) mtimes.Add(new("-tsm4", 320, RARArchiveVersion.RAR4));

        List<RARCommandLineArgument> ctimes = [];
        if (SwitchTSC0) ctimes.Add(new("-tsc0", 320, RARArchiveVersion.RAR4 | RARArchiveVersion.RAR5 | RARArchiveVersion.RAR7));
        if (SwitchTSC1) ctimes.Add(new("-tsc1", 320, RARArchiveVersion.RAR4 | RARArchiveVersion.RAR5 | RARArchiveVersion.RAR7));
        if (SwitchTSC2) ctimes.Add(new("-tsc2", 320, RARArchiveVersion.RAR4));
        if (SwitchTSC3) ctimes.Add(new("-tsc3", 320, RARArchiveVersion.RAR4));
        if (SwitchTSC4) ctimes.Add(new("-tsc4", 320, RARArchiveVersion.RAR4));

        List<RARCommandLineArgument> atimes = [];
        if (SwitchTSA0) atimes.Add(new("-tsa0", 320, RARArchiveVersion.RAR4 | RARArchiveVersion.RAR5 | RARArchiveVersion.RAR7));
        if (SwitchTSA1) atimes.Add(new("-tsa1", 320, RARArchiveVersion.RAR4 | RARArchiveVersion.RAR5 | RARArchiveVersion.RAR7));
        if (SwitchTSA2) atimes.Add(new("-tsa2", 320, RARArchiveVersion.RAR4));
        if (SwitchTSA3) atimes.Add(new("-tsa3", 320, RARArchiveVersion.RAR4));
        if (SwitchTSA4) atimes.Add(new("-tsa4", 320, RARArchiveVersion.RAR4));

        List<RARCommandLineArgument[]> result = [];

        for (int a = 0; a < Math.Max(compressionLevels.Count, 1); a++)
        for (int b = 0; b < Math.Max(archiveFormats.Count, 1); b++)
        for (int c = 0; c < Math.Max(dictSizes.Count, 1); c++)
        for (int d = 0; d < Math.Max(mtimes.Count, 1); d++)
        for (int e = 0; e < Math.Max(ctimes.Count, 1); e++)
        for (int f = 0; f < Math.Max(atimes.Count, 1); f++)
        for (int x = 0; x < (SwitchAI ? 2 : 1); x++)
        for (int z = SwitchMT ? SwitchMTStart : 0; z < (SwitchMT ? SwitchMTEnd + 1 : 1); z++)
        {
            List<RARCommandLineArgument> switches = [new("a", 200)];

            if (x == 0 && SwitchAI) switches.Add(new("-ai", 390));
            if (SwitchR) switches.Add(new("-r", 200));
            if (SwitchDS) switches.Add(new("-ds", 200));
            if (SwitchSDash) switches.Add(new("-s-", 201));

            if (compressionLevels.Count > 0) switches.Add(compressionLevels[a]);
            if (archiveFormats.Count > 0) switches.Add(archiveFormats[b]);
            if (dictSizes.Count > 0) switches.Add(dictSizes[c]);
            if (mtimes.Count > 0) switches.Add(mtimes[d]);
            if (ctimes.Count > 0) switches.Add(ctimes[e]);
            if (atimes.Count > 0) switches.Add(atimes[f]);

            if (SwitchV)
            {
                string volumeArg = BuildVolumeArgument();
                switches.Add(new(volumeArg, 200));
                if (UseOldVolumeNaming)
                    switches.Add(new("-vn", 300, 699));
            }

            if (SwitchMT)
                switches.Add(new($"-mt{z}", 360));

            result.Add([.. switches]);
        }

        return result;
    }

    private string BuildVolumeArgument()
    {
        if (!long.TryParse(VolumeSize, out long sizeValue))
            sizeValue = 15000;

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

            CopyHeadingText = $"Copying {e.TotalFiles} items ({FormatSize(e.TotalBytes)})";
            CopySourceText = e.SourceDirectory;
            CopyDestText = e.DestinationDirectory;
            CopyProgressPercent = e.TotalBytes > 0 ? (double)e.BytesCopied / e.TotalBytes * 100.0 : 0;
            CopyProgressPercentText = $"{CopyProgressPercent:F0}%";
            CopyCurrentFileText = e.FileName;

            int remaining = e.TotalFiles - e.FilesCopied;
            long remainingBytes = e.TotalBytes - e.BytesCopied;
            CopyRemainingText = $"Items remaining: {remaining} ({FormatSize(remainingBytes)})";

            // Timing stats
            var elapsed = _copyStopwatch.Elapsed;
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

    private static string FormatSize(long bytes)
    {
        string[] suffixes = ["B", "KB", "MB", "GB", "TB"];
        int i = 0;
        double size = bytes;
        while (size >= 1024 && i < suffixes.Length - 1)
        {
            size /= 1024;
            i++;
        }
        return $"{size:0.##} {suffixes[i]}";
    }

    private static string FormatSpeed(double bytesPerSec)
    {
        if (bytesPerSec >= 1024 * 1024)
            return $"{bytesPerSec / (1024 * 1024):F1} MB/s";
        return $"{bytesPerSec / 1024:F1} KB/s";
    }

    private void OnCrcValidationProgress(object? _, CrcValidationProgressEventArgs e)
    {
        Application.Current.Dispatcher.BeginInvoke(() =>
        {
            if (!IsVerifying)
            {
                IsVerifying = true;
                _verifyStopwatch.Restart();
            }

            VerifyHeadingText = $"Verifying {e.TotalFiles} items ({FormatSize(e.TotalBytes)})";
            VerifyProgressPercent = e.TotalBytes > 0 ? (double)e.BytesVerified / e.TotalBytes * 100.0 : 0;
            VerifyProgressPercentText = $"{VerifyProgressPercent:F0}%";
            VerifyCurrentFileText = e.FileName;

            int remaining = e.TotalFiles - e.FilesVerified;
            long remainingBytes = e.TotalBytes - e.BytesVerified;
            VerifyRemainingText = $"Items remaining: {remaining} ({FormatSize(remainingBytes)})";

            // Timing stats
            var elapsed = _verifyStopwatch.Elapsed;
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
            }
        });
    }

    private void OnLogMessage(object? _, LogEventArgs e)
    {
        Application.Current.Dispatcher.Invoke(() => AppendLog(e.Target, e.Message));
    }

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

    private void SetRARVersionsFromSrr(SRRFile srr)
    {
        if (!srr.RARVersion.HasValue) return;

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
            bool isRar3 = unpVer >= 20 && unpVer <= 36;
            bool isRar4 = unpVer >= 26 && unpVer <= 36;

            if (srr.HasFirstVolumeFlag == true || srr.HasUnicodeNames == true)
                isRar2 = false;

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
            if (isRar2) selected.Add("2.x");
            if (isRar3) selected.Add("3.x");
            if (isRar4) selected.Add("4.x");
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
        if (sizeBytes <= 0) return;
        SwitchV = true;

        if (sizeBytes % 1_000_000_000 == 0) { VolumeSize = (sizeBytes / 1_000_000_000).ToString(); VolumeSizeUnitIndex = 3; }
        else if (sizeBytes % 1_000_000 == 0) { VolumeSize = (sizeBytes / 1_000_000).ToString(); VolumeSizeUnitIndex = 2; }
        else if (sizeBytes % 1_000 == 0) { VolumeSize = (sizeBytes / 1_000).ToString(); VolumeSizeUnitIndex = 1; }
        else if (sizeBytes % (1024L * 1024 * 1024) == 0) { VolumeSize = (sizeBytes / (1024L * 1024 * 1024)).ToString(); VolumeSizeUnitIndex = 6; }
        else if (sizeBytes % (1024L * 1024) == 0) { VolumeSize = (sizeBytes / (1024L * 1024)).ToString(); VolumeSizeUnitIndex = 5; }
        else if (sizeBytes % 1024 == 0) { VolumeSize = (sizeBytes / 1024).ToString(); VolumeSizeUnitIndex = 4; }
        else { VolumeSize = sizeBytes.ToString(); VolumeSizeUnitIndex = 0; }

        Log(LogTarget.System, $"Volume size: {VolumeSize} {VolumeSizeUnits[VolumeSizeUnitIndex]}");
    }

    private static string FormatVersionLabel(string dirName)
    {
        Match m = VersionLabelRegex().Match(dirName);
        if (!m.Success)
            return dirName;

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
            return $"{(int)ts.TotalHours}:{ts.Minutes:D2}:{ts.Seconds:D2}";
        return $"{ts.Minutes:D2}:{ts.Seconds:D2}";
    }

    private void TryExtractStoredSfv(string srrFilePath, SRRFile srr)
    {
        if (srr.StoredFiles.Count == 0) return;

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
