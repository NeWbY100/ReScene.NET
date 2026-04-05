using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ReScene.NET.Services;
using ReScene.SRS;

namespace ReScene.NET.ViewModels;

public partial class SrsReconstructorViewModel : ViewModelBase
{
    private readonly ISrsReconstructionService _service;
    private readonly IFileDialogService _fileDialog;
    private CancellationTokenSource? _cts;
    private string? _extractedTempFile;

    public SrsReconstructorViewModel(ISrsReconstructionService service, IFileDialogService fileDialog)
    {
        _service = service;
        _fileDialog = fileDialog;

        _service.Progress += OnProgress;
        _service.ScanProgress += OnScanProgress;
    }

    // SRS file
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RebuildCommand))]
    private string _srsFilePath = string.Empty;

    // Media file
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RebuildCommand))]
    private string _mediaFilePath = string.Empty;

    // ISO support
    [ObservableProperty]
    private bool _isIsoSource;

    [ObservableProperty]
    private string _isoFilePath = string.Empty;

    // Output
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RebuildCommand))]
    private string _outputPath = string.Empty;

    // Progress
    [ObservableProperty]
    private int _progressPercent;

    [ObservableProperty]
    private string _progressMessage = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RebuildCommand))]
    private bool _isRebuilding;

    [ObservableProperty]
    private bool _showProgress;

    // ISO progress (for modal window)
    [ObservableProperty]
    private string _isoProgressHeading = string.Empty;

    [ObservableProperty]
    private int _isoOverallPercent;

    [ObservableProperty]
    private string _isoFileCountText = string.Empty;

    [ObservableProperty]
    private int _isoCurrentPercent;

    [ObservableProperty]
    private string _isoCurrentFileText = string.Empty;

    [ObservableProperty]
    private string _isoProcessedText = string.Empty;

    [ObservableProperty]
    private string _isoRemainingText = string.Empty;

    [ObservableProperty]
    private string _isoCurrentSizeText = string.Empty;

    [ObservableProperty]
    private string _isoSpeedText = string.Empty;

    [ObservableProperty]
    private string _isoEtaText = string.Empty;

    [ObservableProperty]
    private bool _isoProcessing;

    private Stopwatch? _isoStopwatch;
    private bool _scanModalActive;

    // Result
    [ObservableProperty]
    private string _resultSummary = string.Empty;

    [ObservableProperty]
    private bool _showResult;

    [ObservableProperty]
    private bool _resultSuccess;

    // Log
    public ObservableCollection<string> LogEntries { get; } = [];

    [RelayCommand]
    private async Task BrowseSrsAsync()
    {
        string? path = await _fileDialog.OpenFileAsync("Select SRS File",
            ["SRS Files|*.srs", "All Files|*.*"]);

        if (path is not null)
        {
            SrsFilePath = path;
            AutoSetOutputPath(path);
        }
    }

    [RelayCommand]
    private async Task BrowseMediaAsync()
    {
        string? path = await _fileDialog.OpenFileAsync("Select Media File",
        [
            "Video Files|*.avi;*.mkv;*.mp4;*.wmv;*.m4v;*.mov",
            "Audio Files|*.flac;*.mp3",
            "Stream Files|*.vob;*.m2ts;*.ts;*.mpg;*.mpeg;*.evo;*.m2v",
            "ISO Images|*.iso;*.img",
            "All Files|*.*"
        ]);

        if (path is null)
        {
            return;
        }

        if (IsoMediaExtractor.IsIsoFile(path))
        {
            IsIsoSource = true;
            IsoFilePath = path;
            MediaFilePath = path;
        }
        else
        {
            IsIsoSource = false;
            IsoFilePath = string.Empty;
            MediaFilePath = path;
        }
    }

    [RelayCommand]
    private async Task BrowseOutputAsync()
    {
        string? path = await _fileDialog.SaveFileAsync(
            "Save Reconstructed Sample", ".*",
            ["All Files|*.*"],
            string.IsNullOrWhiteSpace(OutputPath) ? null : Path.GetFileName(OutputPath));

        if (path is not null)
        {
            OutputPath = path;
        }
    }

    private bool CanRebuild()
    {
        if (IsRebuilding || string.IsNullOrWhiteSpace(SrsFilePath) || string.IsNullOrWhiteSpace(OutputPath))
        {
            return false;
        }

        if (IsIsoSource)
        {
            return true; // Auto-detection will find the right VOB set
        }

        return !string.IsNullOrWhiteSpace(MediaFilePath);
    }

    [RelayCommand(CanExecute = nameof(CanRebuild))]
    private async Task RebuildAsync()
    {
        IsRebuilding = true;
        ShowProgress = true;
        ShowResult = false;
        ProgressPercent = 0;
        ProgressMessage = "Starting...";
        LogEntries.Clear();

        _cts = new CancellationTokenSource();
        var sw = Stopwatch.StartNew();

        try
        {
            Log("Starting SRS reconstruction...");
            Log($"SRS file:   {SrsFilePath}");

            string mediaPath;

            // If ISO source, auto-detect matching VOB set or extract selected file
            if (IsIsoSource)
            {
                Log($"ISO image:  {IsoFilePath}");

                string tempDir = Path.Combine(Path.GetTempPath(), "ReScene.NET", Guid.NewGuid().ToString("N")[..8]);
                Directory.CreateDirectory(tempDir);
                string tempFile = Path.Combine(tempDir, "media.vob");
                _extractedTempFile = tempFile;

                // Show ISO progress modal
                IsoProgressHeading = "Scanning ISO";
                IsoOverallPercent = 0;
                IsoCurrentPercent = 0;
                IsoFileCountText = "Initializing...";
                IsoCurrentFileText = "Looking for matching VOB title set...";
                IsoProcessedText = string.Empty;
                IsoRemainingText = string.Empty;
                IsoSpeedText = string.Empty;
                IsoEtaText = string.Empty;
                _isoStopwatch = Stopwatch.StartNew();
                IsoProcessing = true;

                Log("Scanning ISO for matching VOB title set using SRS track signatures...");
                bool found = await IsoMediaExtractor.ExtractMatchingVobSetAsync(
                    IsoFilePath, SrsFilePath, tempFile,
                    p => Application.Current.Dispatcher.BeginInvoke(() =>
                    {
                        IsoProgressHeading = p.Phase == "Extracting" ? "Extracting from ISO" : "Scanning ISO";
                        IsoOverallPercent = p.OverallPercent;
                        IsoCurrentPercent = p.CurrentPercent;
                        IsoFileCountText = $"File {p.FileIndex} of {p.FileCount}";
                        IsoCurrentFileText = p.CurrentFile;
                        if (p.CurrentBytesTotal > 0)
                        {
                            IsoCurrentSizeText = $"{FormatSize(p.CurrentBytesProcessed)} / {FormatSize(p.CurrentBytesTotal)}";
                        }
                        else
                        {
                            IsoCurrentSizeText = string.Empty;
                        }
                        UpdateIsoStats(p.BytesProcessed, p.BytesTotal);
                    }), _cts.Token);

                IsoProcessing = false;

                if (found)
                {
                    long size = new FileInfo(tempFile).Length;
                    Log($"Matching VOB set found and extracted ({size:N0} bytes).");
                }
                else
                {
                    IsoProcessing = false;
                    throw new InvalidOperationException("No matching VOB set found in ISO. The sample track signature was not found in any VOB title set.");
                }

                mediaPath = tempFile;
            }
            else
            {
                Log($"Media file: {MediaFilePath}");
                mediaPath = MediaFilePath;
            }

            Log($"Output:     {OutputPath}");

            // Show scanning modal during signature search
            IsoProgressHeading = "Scanning Media File";
            IsoCurrentFileText = Path.GetFileName(mediaPath);
            IsoOverallPercent = 0;
            IsoCurrentPercent = 0;
            IsoFileCountText = "Searching for track signatures...";
            IsoCurrentSizeText = string.Empty;
            IsoProcessedText = string.Empty;
            IsoRemainingText = string.Empty;
            IsoSpeedText = string.Empty;
            IsoEtaText = string.Empty;
            _isoStopwatch = Stopwatch.StartNew();
            _scanModalActive = true;
            IsoProcessing = true;

            // Yield to let the dispatcher open the modal before heavy work starts
            await Task.Yield();

            SrsReconstructionResult result = await _service.RebuildAsync(
                SrsFilePath, mediaPath, OutputPath, _cts.Token);

            sw.Stop();

            if (result.Success)
            {
                Log($"Reconstruction complete in {sw.Elapsed.TotalSeconds:F1}s");
                Log($"  Expected CRC: {result.ExpectedCrc:X8}");
                Log($"  Actual CRC:   {result.ActualCrc:X8}");
                Log($"  CRC match:    YES");
                Log($"  Expected size: {result.ExpectedSize:N0} bytes");
                Log($"  Actual size:   {result.ActualSize:N0} bytes");

                ProgressPercent = 100;
                ProgressMessage = "Complete!";
                ResultSuccess = true;
                ResultSummary = $"CRC32 match: {result.ActualCrc:X8} ({result.ActualSize:N0} bytes)";
            }
            else
            {
                Log($"Reconstruction failed after {sw.Elapsed.TotalSeconds:F1}s");
                Log($"  Error: {result.ErrorMessage}");

                ProgressMessage = "Failed.";
                ResultSuccess = false;
                ResultSummary = result.ErrorMessage ?? "Unknown error";
            }

            ShowResult = true;
        }
        catch (Exception ex)
        {
            ProgressMessage = "Error.";
            ResultSuccess = false;
            ResultSummary = ex.Message;
            ShowResult = true;
            Log($"ERROR: {ex.Message}");
        }
        finally
        {
            if (_scanModalActive)
            {
                _scanModalActive = false;
                IsoProcessing = false;
            }

            IsRebuilding = false;
            _cts?.Dispose();
            _cts = null;
            CleanupTempFile();
        }
    }

    [RelayCommand]
    private void CancelRebuild()
    {
        _cts?.Cancel();
        Log("Cancellation requested...");
    }

    private void UpdateIsoStats(long processed, long total)
    {
        if (total <= 0 || _isoStopwatch is null)
        {
            return;
        }

        double elapsed = _isoStopwatch.Elapsed.TotalSeconds;
        IsoProcessedText = $"{FormatSize(processed)} / {FormatSize(total)}";

        long remaining = total - processed;
        IsoRemainingText = FormatSize(remaining);

        if (elapsed > 0.5 && processed > 0)
        {
            double bytesPerSec = processed / elapsed;
            IsoSpeedText = $"{FormatSize((long)bytesPerSec)}/s";

            double secondsRemaining = remaining / bytesPerSec;
            if (secondsRemaining < 60)
            {
                IsoEtaText = $"{secondsRemaining:F0}s";
            }
            else
            {
                IsoEtaText = $"{(int)(secondsRemaining / 60)}m {(int)(secondsRemaining % 60)}s";
            }
        }
    }

    private static string FormatSize(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double size = bytes;
        int i = 0;

        while (size >= 1024 && i < units.Length - 1)
        {
            size /= 1024;
            i++;
        }

        return $"{size:0.##} {units[i]}";
    }

    #region ISO Support

    private void CleanupTempFile()
    {
        if (_extractedTempFile is null)
        {
            return;
        }

        try
        {
            string? tempDir = Path.GetDirectoryName(_extractedTempFile);
            if (File.Exists(_extractedTempFile))
            {
                File.Delete(_extractedTempFile);
            }

            if (tempDir is not null && Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, true);
            }
        }
        catch
        {
            // Best-effort cleanup
        }

        _extractedTempFile = null;
    }

    #endregion

    private void OnScanProgress(object? _, SrsScanProgressEventArgs e)
    {
        Application.Current.Dispatcher.BeginInvoke(() =>
        {
            if (!_scanModalActive)
            {
                return;
            }

            IsoOverallPercent = e.Percent;
            IsoCurrentPercent = e.Percent;
            IsoCurrentFileText = e.Phase;
            UpdateIsoStats(e.BytesScanned, e.BytesTotal);
        });
    }

    private void OnProgress(object? _, SrsReconstructionProgressEventArgs e)
    {
        Application.Current.Dispatcher.BeginInvoke(() =>
        {
            // Close scan modal when rebuilding starts (scanning is done)
            if (_scanModalActive && e.Phase is "Rebuilding" or "Verifying CRC" or "Complete")
            {
                _scanModalActive = false;
                IsoProcessing = false;
            }

            int percent = (int)e.ProgressPercent;
            ProgressPercent = percent;
            string msg = e.TotalTracks > 0
                ? $"{e.Phase} (track {e.TrackNumber}/{e.TotalTracks})"
                : e.Phase;
            ProgressMessage = msg;
            Log(msg);
        });
    }

    private void Log(string message)
    {
        string entry = $"{DateTime.Now:HH:mm:ss} {message}";
        LogEntries.Add(entry);
    }

    private void AutoSetOutputPath(string srsPath)
    {
        if (!string.IsNullOrWhiteSpace(OutputPath))
        {
            return;
        }

        // Try to infer the sample file name from the SRS
        try
        {
            var srs = SRSFile.Load(srsPath);
            if (srs.FileData is { } fd && !string.IsNullOrWhiteSpace(fd.FileName))
            {
                string dir = Path.GetDirectoryName(srsPath) ?? ".";
                OutputPath = Path.Combine(dir, fd.FileName);
                return;
            }
        }
        catch
        {
            // Fall through to extension-based default
        }

        // Fallback: same directory, .avi extension
        string fallbackDir = Path.GetDirectoryName(srsPath) ?? ".";
        string name = Path.GetFileNameWithoutExtension(srsPath);
        OutputPath = Path.Combine(fallbackDir, name + ".avi");
    }
}
