using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ReScene.NET.Helpers;
using ReScene.NET.Services;
using ReScene.SRS;

namespace ReScene.NET.ViewModels;

public partial class SRSReconstructorViewModel : ViewModelBase
{
    private readonly ISrsReconstructionService _service;
    private readonly IFileDialogService _fileDialog;
    private readonly ITempDirectoryService _tempDir;
    private CancellationTokenSource? _cts;
    private string? _extractedTempFile;

    public SRSReconstructorViewModel(ISrsReconstructionService service, IFileDialogService fileDialog, ITempDirectoryService tempDir)
    {
        _service = service;
        _fileDialog = fileDialog;
        _tempDir = tempDir;

        _service.Progress += OnProgress;
        _service.ScanProgress += OnScanProgress;
    }

    // SRS file
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RebuildCommand))]
    public partial string SRSFilePath { get; set; } = string.Empty;

    // Media file
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RebuildCommand))]
    public partial string MediaFilePath { get; set; } = string.Empty;

    // ISO support
    [ObservableProperty]
    public partial bool IsISOSource { get; set; }

    [ObservableProperty]
    public partial string ISOFilePath { get; set; } = string.Empty;

    // Output
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RebuildCommand))]
    public partial string OutputPath { get; set; } = string.Empty;

    // Progress
    [ObservableProperty]
    public partial int ProgressPercent { get; set; }

    [ObservableProperty]
    public partial string ProgressMessage { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RebuildCommand))]
    public partial bool IsRebuilding { get; set; }

    [ObservableProperty]
    public partial bool ShowProgress { get; set; }

    // ISO progress (for modal window)
    [ObservableProperty]
    public partial string ISOProgressHeading { get; set; } = string.Empty;

    [ObservableProperty]
    public partial int ISOOverallPercent { get; set; }

    [ObservableProperty]
    public partial string ISOFileCountText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial int ISOCurrentPercent { get; set; }

    [ObservableProperty]
    public partial string ISOCurrentFileText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string ISOProcessedText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string ISORemainingText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string ISOCurrentSizeText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string ISOSpeedText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string ISOEtaText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool ISOProcessing { get; set; }

    private Stopwatch? _iSOStopwatch;
    private bool _scanModalActive;

    // Result
    [ObservableProperty]
    public partial string ResultSummary { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool ShowResult { get; set; }

    [ObservableProperty]
    public partial bool ResultSuccess { get; set; }

    // Log
    public ObservableCollection<string> LogEntries { get; } = [];

    [RelayCommand]
    private async Task BrowseSRSAsync()
    {
        string? path = await _fileDialog.OpenFileAsync("Select SRS File",
            FileDialogFilters.SRSFiles);

        if (path is not null)
        {
            SRSFilePath = path;
            AutoSetOutputPath(path);
        }
    }

    [RelayCommand]
    private async Task BrowseMediaAsync()
    {
        string? path = await _fileDialog.OpenFileAsync("Select Media File",
            FileDialogFilters.MediaFiles);

        if (path is null)
        {
            return;
        }

        if (ISOMediaExtractor.IsISOFile(path))
        {
            IsISOSource = true;
            ISOFilePath = path;
            MediaFilePath = path;
        }
        else
        {
            IsISOSource = false;
            ISOFilePath = string.Empty;
            MediaFilePath = path;
        }
    }

    [RelayCommand]
    private async Task BrowseOutputAsync()
    {
        string? path = await _fileDialog.SaveFileAsync(
            "Save Reconstructed Sample", ".*",
            FileDialogFilters.AllFiles,
            string.IsNullOrWhiteSpace(OutputPath) ? null : Path.GetFileName(OutputPath));

        if (path is not null)
        {
            OutputPath = path;
        }
    }

    private bool CanRebuild()
    {
        if (IsRebuilding || string.IsNullOrWhiteSpace(SRSFilePath) || string.IsNullOrWhiteSpace(OutputPath))
        {
            return false;
        }

        if (IsISOSource)
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
            Log($"SRS file:   {SRSFilePath}");

            string mediaPath;

            // If ISO source, auto-detect matching VOB set or extract selected file
            if (IsISOSource)
            {
                Log($"ISO image:  {ISOFilePath}");

                string tempDir = _tempDir.CreateTempDirectory();
                string tempFile = Path.Combine(tempDir, "media.vob");
                _extractedTempFile = tempFile;

                // Show ISO progress modal
                ISOProgressHeading = "Scanning ISO";
                ISOOverallPercent = 0;
                ISOCurrentPercent = 0;
                ISOFileCountText = "Initializing...";
                ISOCurrentFileText = "Looking for matching VOB title set...";
                ISOProcessedText = string.Empty;
                ISORemainingText = string.Empty;
                ISOSpeedText = string.Empty;
                ISOEtaText = string.Empty;
                _iSOStopwatch = Stopwatch.StartNew();
                ISOProcessing = true;

                Log("Scanning ISO for matching VOB title set using SRS track signatures...");
                bool found = await ISOMediaExtractor.ExtractMatchingVobSetAsync(
                    ISOFilePath, SRSFilePath, tempFile,
                    p => Application.Current.Dispatcher.BeginInvoke(() =>
                    {
                        ISOProgressHeading = p.Phase == "Extracting" ? "Extracting from ISO" : "Scanning ISO";
                        ISOOverallPercent = p.OverallPercent;
                        ISOCurrentPercent = p.CurrentPercent;
                        ISOFileCountText = $"File {p.FileIndex} of {p.FileCount}";
                        ISOCurrentFileText = p.CurrentFile;
                        if (p.CurrentBytesTotal > 0)
                        {
                            ISOCurrentSizeText = $"{FormatUtilities.FormatSize(p.CurrentBytesProcessed)} / {FormatUtilities.FormatSize(p.CurrentBytesTotal)}";
                        }
                        else
                        {
                            ISOCurrentSizeText = string.Empty;
                        }
                        UpdateISOStats(p.BytesProcessed, p.BytesTotal);
                    }), _cts.Token);

                ISOProcessing = false;

                if (found)
                {
                    long size = new FileInfo(tempFile).Length;
                    Log($"Matching VOB set found and extracted ({size:N0} bytes).");
                }
                else
                {
                    ISOProcessing = false;
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
            ISOProgressHeading = "Scanning Media File";
            ISOCurrentFileText = Path.GetFileName(mediaPath);
            ISOOverallPercent = 0;
            ISOCurrentPercent = 0;
            ISOFileCountText = "Searching for track signatures...";
            ISOCurrentSizeText = string.Empty;
            ISOProcessedText = string.Empty;
            ISORemainingText = string.Empty;
            ISOSpeedText = string.Empty;
            ISOEtaText = string.Empty;
            _iSOStopwatch = Stopwatch.StartNew();
            _scanModalActive = true;
            ISOProcessing = true;

            // Yield to let the dispatcher open the modal before heavy work starts
            await Task.Yield();

            SRSReconstructionResult result = await _service.RebuildAsync(
                SRSFilePath, mediaPath, OutputPath, _cts.Token);

            sw.Stop();

            if (result.Success)
            {
                Log($"Reconstruction complete in {sw.Elapsed.TotalSeconds:F1}s");
                Log($"  Expected CRC: {result.ExpectedCRC:X8}");
                Log($"  Actual CRC:   {result.ActualCRC:X8}");
                Log($"  CRC match:    YES");
                Log($"  Expected size: {result.ExpectedSize:N0} bytes");
                Log($"  Actual size:   {result.ActualSize:N0} bytes");

                ProgressPercent = 100;
                ProgressMessage = "Complete!";
                ResultSuccess = true;
                ResultSummary = $"CRC32 match: {result.ActualCRC:X8} ({result.ActualSize:N0} bytes)";
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
                ISOProcessing = false;
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

    [RelayCommand]
    private async Task SaveLogAsync()
    {
        if (LogEntries.Count == 0)
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
            await LogExporter.SaveAsync(LogEntries, path);
            Log($"Log saved to {Path.GetFileName(path)}");
        }
        catch (Exception ex)
        {
            Log($"ERROR saving log: {ex.Message}");
        }
    }

    private void UpdateISOStats(long processed, long total)
    {
        if (total <= 0 || _iSOStopwatch is null)
        {
            return;
        }

        double elapsed = _iSOStopwatch.Elapsed.TotalSeconds;
        ISOProcessedText = $"{FormatUtilities.FormatSize(processed)} / {FormatUtilities.FormatSize(total)}";

        long remaining = total - processed;
        ISORemainingText = FormatUtilities.FormatSize(remaining);

        if (elapsed > 0.5 && processed > 0)
        {
            double bytesPerSec = processed / elapsed;
            ISOSpeedText = $"{FormatUtilities.FormatSize((long)bytesPerSec)}/s";

            double secondsRemaining = remaining / bytesPerSec;
            if (secondsRemaining < 60)
            {
                ISOEtaText = $"{secondsRemaining:F0}s";
            }
            else
            {
                ISOEtaText = $"{(int)(secondsRemaining / 60)}m {(int)(secondsRemaining % 60)}s";
            }
        }
    }


    #region ISO Support

    private void CleanupTempFile()
    {
        if (_extractedTempFile is null)
        {
            return;
        }

        _tempDir.Cleanup(Path.GetDirectoryName(_extractedTempFile));
        _extractedTempFile = null;
    }

    #endregion

    private void OnScanProgress(object? _, SRSScanProgressEventArgs e)
    {
        Application.Current.Dispatcher.BeginInvoke(() =>
        {
            if (!_scanModalActive)
            {
                return;
            }

            ISOOverallPercent = e.Percent;
            ISOCurrentPercent = e.Percent;
            ISOCurrentFileText = e.Phase;
            UpdateISOStats(e.BytesScanned, e.BytesTotal);
        });
    }

    private void OnProgress(object? _, SRSReconstructionProgressEventArgs e)
    {
        Application.Current.Dispatcher.BeginInvoke(() =>
        {
            // Keep the scan modal open through Rebuilding (frame-data collection
            // can take many seconds on large media files) and Verifying CRC.
            // Close only once reconstruction is complete.
            if (_scanModalActive)
            {
                if (e.Phase == "Rebuilding")
                {
                    ISOProgressHeading = "Rebuilding Sample";
                    ISOCurrentFileText = "Collecting frame data from media file...";
                }
                else if (e.Phase == "Verifying CRC")
                {
                    ISOProgressHeading = "Verifying CRC";
                    ISOCurrentFileText = "Computing checksum of rebuilt sample...";
                    ISOOverallPercent = 100;
                    ISOCurrentPercent = 100;
                }
                else if (e.Phase == "Complete")
                {
                    _scanModalActive = false;
                    ISOProcessing = false;
                }
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

    private void Log(string message) => AppendLogEntry(LogEntries, message);

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
