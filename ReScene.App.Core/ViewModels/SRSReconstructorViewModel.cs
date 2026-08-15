using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ReScene.App.Core.Helpers;
using ReScene.App.Core.Models;
using ReScene.App.Core.Services;
using ReScene.SRS;
namespace ReScene.App.Core.ViewModels;

public partial class SRSReconstructorViewModel : OperationViewModelBase
{
    private readonly ISRSReconstructionService _service;
    private readonly IFileDialogService _fileDialog;
    private readonly ITempDirectoryService _tempDir;
    private readonly IUiDispatcher _uiDispatcher;
    private string? _extractedTempFile;

    public SRSReconstructorViewModel(ISRSReconstructionService service, IFileDialogService fileDialog, ITempDirectoryService tempDir, IUiDispatcher uiDispatcher)
    {
        _service = service;
        _fileDialog = fileDialog;
        _tempDir = tempDir;
        _uiDispatcher = uiDispatcher;

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
    [NotifyCanExecuteChangedFor(nameof(RebuildCommand))]
    public partial bool IsISOSource { get; set; }

    [ObservableProperty]
    public partial string ISOFilePath { get; set; } = string.Empty;

    // Output
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RebuildCommand))]
    public partial string OutputPath { get; set; } = string.Empty;

    // Field guidance
    [ObservableProperty]
    public partial FieldStatus SRSStatus { get; set; } = FieldStatus.None;

    [ObservableProperty]
    public partial FieldStatus MediaStatus { get; set; } = FieldStatus.None;

    [ObservableProperty]
    public partial FieldStatus OutputStatus { get; set; } = FieldStatus.None;

    private long _expectedSampleSize;

    // Progress
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RebuildCommand))]
    public partial bool IsRebuilding { get; set; }

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

    /// <summary>
    /// Clears all user-entered state back to a freshly-constructed default so a Beginner
    /// wizard opens clean. No-op while a rebuild is in progress (e.g. started from the
    /// Advanced tab) so an active run isn't disrupted.
    /// </summary>
    public void Reset()
    {
        if (IsRebuilding)
        {
            return;
        }

        SRSFilePath = string.Empty;
        MediaFilePath = string.Empty;
        OutputPath = string.Empty;
        SRSStatus = FieldStatus.None;
        MediaStatus = FieldStatus.None;
        OutputStatus = FieldStatus.None;

        IsISOSource = false;
        ISOFilePath = string.Empty;
        _expectedSampleSize = 0;

        ShowResult = false;
        ResultSuccess = false;
        ResultSummary = string.Empty;

        ProgressPercent = 0;
        ProgressMessage = string.Empty;
        ShowProgress = false;
        LogEntries.Clear();
    }

    [RelayCommand]
    private async Task BrowseSRSAsync()
    {
        string? path = await _fileDialog.OpenFileAsync("Select SRS File",
            FileDialogFilters.SRSFiles, SRSFilePath);

        if (path is not null)
        {
            SRSFilePath = path;
        }
    }

    [RelayCommand]
    private async Task BrowseMediaAsync()
    {
        string? path = await _fileDialog.OpenFileAsync("Select Media File",
            FileDialogFilters.MediaFiles,
            string.IsNullOrWhiteSpace(MediaFilePath) ? SRSFilePath : MediaFilePath); // media usually sits beside the SRS

        if (path is null)
        {
            return;
        }

        // OnMediaFilePathChanged applies ISO detection + status for every entry path
        // (Browse, typed, dropped), so the two cannot diverge.
        MediaFilePath = path;
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
        // Cleared explicitly (not just implied by ShowResult=false): the view's ResultStatus
        // TextBlock is always in the tree and bound directly to ResultSummary so its
        // LiveSetting="Polite" announcement survives the visual banner's own visibility
        // toggling. Without this, a stale value from a previous run would sit there
        // unannounced, and two runs producing an identical outcome would raise no
        // PropertyChanged the second time (CommunityToolkit's setter suppresses equal-value
        // sets) — re-arming here guarantees completion is always a genuine empty->text
        // transition.
        ResultSummary = string.Empty;
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
                    p => _uiDispatcher.Post(() =>
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

            if (_cts?.IsCancellationRequested == true)
            {
                // A deliberate cancel: the rebuilder reports it as Success=false with a "cancelled"
                // ErrorMessage, which would otherwise fall into the failure branch and show the red
                // "Failed" banner. Show the neutral "Cancelled" state instead (mirrors
                // SRSCreatorViewModel) — no result banner, not logged as a failure.
                Log("Cancelled.");
                ProgressMessage = "Cancelled.";
            }
            else if (result.Success)
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
                ShowResult = true;
            }
            else
            {
                Log($"Reconstruction failed after {sw.Elapsed.TotalSeconds:F1}s");
                Log($"  Error: {result.ErrorMessage}");

                ProgressMessage = "Failed.";
                ResultSuccess = false;
                ResultSummary = result.ErrorMessage ?? "Unknown error";
                ShowResult = true;
            }
        }
        catch (OperationCanceledException)
        {
            // Cancellation may also surface as a thrown OperationCanceledException; keep it neutral
            // (no red banner), consistent with the cancelled-result branch above.
            Log("Cancelled.");
            ProgressMessage = "Cancelled.";
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
            // Clear both flags unconditionally (matching SRSCreatorViewModel): the ISO extraction
            // phase sets ISOProcessing=true while _scanModalActive is still false, so a cancel/error
            // there must not leave ISOProcessing stuck true — that keeps the ISO modal open and, since
            // [ObservableProperty] suppresses no-op sets, the next ISO run would raise no
            // PropertyChanged and open no progress window. See audit #40.
            _scanModalActive = false;
            ISOProcessing = false;

            IsRebuilding = false;
            _cts?.Dispose();
            _cts = null;
            CleanupTempFile();
        }
    }

    [RelayCommand]
    private void CancelRebuild()
    {
        Cancel();
        Log("Cancellation requested...");
    }

    [RelayCommand]
    private Task SaveLogAsync() => SaveLogToFileAsync(_fileDialog);

    private void UpdateISOStats(long processed, long total)
    {
        if (total <= 0 || _iSOStopwatch is null)
        {
            return;
        }

        double elapsed = _iSOStopwatch.Elapsed.TotalSeconds;
        (ISOProcessedText, ISORemainingText) = FormatUtilities.FormatScanStats(processed, total);

        if (FormatUtilities.FormatSpeedEta(processed, total, elapsed) is { } speedEta)
        {
            (ISOSpeedText, ISOEtaText) = speedEta;
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
        _uiDispatcher.Post(() =>
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
        _uiDispatcher.Post(() =>
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

    partial void OnSRSFilePathChanged(string value)
    {
        _expectedSampleSize = 0;

        if (string.IsNullOrWhiteSpace(value))
        {
            SRSStatus = FieldStatus.None;
            return;
        }

        if (!File.Exists(value))
        {
            SRSStatus = FieldStatus.Error("This .srs file does not exist.");
            return;
        }

        try
        {
            SRSFile srs = SRSFile.Load(value);
            if (srs.FileData is null || string.IsNullOrWhiteSpace(srs.FileData.FileName))
            {
                _expectedSampleSize = 0;
                SRSStatus = FieldStatus.Error("This SRS contains no sample file data.");
                return;
            }

            string sampleName = srs.FileData.FileName;
            long sampleSize = (long)srs.FileData.SampleSize;
            _expectedSampleSize = sampleSize;
            SRSStatus = FieldStatus.Ok($"Sample: {sampleName} ({FormatUtilities.FormatSize(sampleSize)}).");

            if (string.IsNullOrWhiteSpace(OutputPath))
            {
                string dir = Path.GetDirectoryName(value) ?? ".";
                OutputPath = Path.Combine(dir, sampleName);
                OutputStatus = FieldStatus.Info("Auto-filled from the SRS sample name. Change it if needed.");
            }
        }
        catch (Exception ex)
        {
            SRSStatus = FieldStatus.Error($"Could not read this SRS: {ex.Message}");
        }
    }

    partial void OnMediaFilePathChanged(string value)
    {
        // Empty/invalid paths deliberately leave any existing ISO selection untouched — an ISO
        // source can keep IsISOSource set while the media textbox is blank (rebuild reads the ISO).
        if (string.IsNullOrWhiteSpace(value))
        {
            MediaStatus = FieldStatus.None;
            return;
        }

        if (!File.Exists(value))
        {
            MediaStatus = FieldStatus.Error("This media file does not exist.");
            return;
        }

        // ISO detection/state lives here (not only in the Browse command) so typed and drag-dropped
        // media paths are recognised as ISO sources — otherwise an .iso entered by text or drop is
        // treated as a plain media file at rebuild time.
        if (ISOMediaExtractor.IsISOFile(value))
        {
            IsISOSource = true;
            ISOFilePath = value;
            MediaStatus = FieldStatus.Info("ISO image — the matching VOB set is extracted during rebuild.");
        }
        else
        {
            IsISOSource = false;
            ISOFilePath = string.Empty;
            long mediaSize = new FileInfo(value).Length;
            MediaStatus = FieldGuidance.EvaluateMediaAgainstSample(mediaSize, _expectedSampleSize);
        }
    }
}
