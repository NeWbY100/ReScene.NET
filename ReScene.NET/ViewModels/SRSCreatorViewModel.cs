using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ReScene.NET.Helpers;
using ReScene.NET.Models;
using ReScene.NET.Services;
using ReScene.SRS;

namespace ReScene.NET.ViewModels;

public partial class SRSCreatorViewModel : ViewModelBase
{
    private readonly ISrsCreationService _sRSService;
    private readonly IFileDialogService _fileDialog;
    private readonly ITempDirectoryService _tempDir;
    private readonly IAppSettingsService _settingsService;
    private CancellationTokenSource? _cts;
    private string? _extractedTempFile;

    public SRSCreatorViewModel(ISrsCreationService srsService, IFileDialogService fileDialog, ITempDirectoryService tempDir, IAppSettingsService settingsService)
    {
        _sRSService = srsService;
        _fileDialog = fileDialog;
        _tempDir = tempDir;
        _settingsService = settingsService;

        _sRSService.Progress += OnProgress;
        _sRSService.ScanProgress += OnScanProgress;

        AppSettings settings = _settingsService.Load();

        if (string.IsNullOrEmpty(AppName))
        {
            AppName = settings.DefaultAppName;
        }

        if (string.IsNullOrEmpty(OutputPath) && !string.IsNullOrEmpty(settings.DefaultOutputDirectory))
        {
            OutputPath = settings.DefaultOutputDirectory;
        }

        _settingsService.Changed += (_, _) =>
        {
            AppSettings updated = _settingsService.Load();

            if (string.IsNullOrEmpty(AppName))
            {
                AppName = updated.DefaultAppName;
            }
        };
    }

    // Input
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CreateSRSCommand))]
    public partial string InputPath { get; set; } = string.Empty;

    // ISO support
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowISOSelection))]
    public partial bool IsISOSource { get; set; }

    [ObservableProperty]
    public partial string ISOFilePath { get; set; } = string.Empty;

    public ObservableCollection<string> ISOMediaFiles { get; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CreateSRSCommand))]
    public partial string? SelectedISOMediaFile { get; set; }

    /// <summary>
    /// Gets whether the ISO file selection combo should be visible.
    /// </summary>
    public bool ShowISOSelection => IsISOSource;

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

    private Stopwatch? _scanStopwatch;
    private bool _scanModalActive;

    // Output
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CreateSRSCommand))]
    public partial string OutputPath { get; set; } = string.Empty;

    // Optional main file for match-offset verification (mirrors pyrescene -c)
    [ObservableProperty]
    public partial string MainFilePath { get; set; } = string.Empty;

    // Options
    [ObservableProperty]
    public partial string AppName { get; set; } = string.Empty;

    // Progress
    [ObservableProperty]
    public partial int ProgressPercent { get; set; }

    [ObservableProperty]
    public partial string ProgressMessage { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CreateSRSCommand))]
    public partial bool IsCreating { get; set; }

    [ObservableProperty]
    public partial bool ShowProgress { get; set; }

    // Log
    public ObservableCollection<string> LogEntries { get; } = [];

    [RelayCommand]
    private async Task BrowseInputAsync()
    {
        string? path = await _fileDialog.OpenFileAsync("Select Sample File",
            FileDialogFilters.MediaSamples);

        if (path is null)
        {
            return;
        }

        if (ISOMediaExtractor.IsISOFile(path))
        {
            IsISOSource = true;
            ISOFilePath = path;
            InputPath = path;

            ISOMediaFiles.Clear();
            SelectedISOMediaFile = null;

            List<string> files = ISOMediaExtractor.ListMediaFiles(path);
            foreach (string file in files)
            {
                ISOMediaFiles.Add(file);
            }

            if (ISOMediaFiles.Count > 0)
            {
                SelectedISOMediaFile = ISOMediaFiles[0];
            }

            AutoSetOutputPath(path);
        }
        else
        {
            IsISOSource = false;
            ISOFilePath = string.Empty;
            ISOMediaFiles.Clear();
            SelectedISOMediaFile = null;
            InputPath = path;
            AutoSetOutputPath(path);
        }
    }

    [RelayCommand]
    private async Task BrowseMainFileAsync()
    {
        string? path = await _fileDialog.OpenFileAsync("Select Main File (Full Movie)",
            FileDialogFilters.MediaFiles);

        if (path is not null)
        {
            MainFilePath = path;
        }
    }

    [RelayCommand]
    private void ClearMainFile() => MainFilePath = string.Empty;

    [RelayCommand]
    private async Task BrowseOutputAsync()
    {
        string? path = await _fileDialog.SaveFileAsync(
            "Save SRS File", ".srs", FileDialogFilters.SRSSave);
        if (path is not null)
        {
            OutputPath = path;
        }
    }

    private bool CanCreateSRS()
    {
        if (IsCreating || string.IsNullOrWhiteSpace(InputPath) || string.IsNullOrWhiteSpace(OutputPath))
        {
            return false;
        }

        if (IsISOSource)
        {
            return !string.IsNullOrWhiteSpace(SelectedISOMediaFile);
        }

        return true;
    }

    [RelayCommand(CanExecute = nameof(CanCreateSRS))]
    private async Task CreateSRSAsync()
    {
        IsCreating = true;
        ShowProgress = true;
        ProgressPercent = 0;
        ProgressMessage = "Starting...";
        LogEntries.Clear();

        _cts = new CancellationTokenSource();

        try
        {
            var options = new SRSCreationOptions
            {
                AppName = string.IsNullOrWhiteSpace(AppName) ? FormatUtilities.GetDefaultAppName() : AppName,
                MainFilePath = string.IsNullOrWhiteSpace(MainFilePath) ? null : MainFilePath
            };

            Log("Starting SRS creation...");

            string samplePath;

            if (IsISOSource)
            {
                Log($"ISO image: {ISOFilePath}");
                Log($"ISO file:  {SelectedISOMediaFile}");

                string tempDir = _tempDir.CreateTempDirectory();
                string tempFile = Path.Combine(tempDir, Path.GetFileName(SelectedISOMediaFile!));
                _extractedTempFile = tempFile;

                // Show ISO progress modal
                ISOProgressHeading = "Extracting from ISO";
                ISOOverallPercent = 0;
                ISOCurrentPercent = 0;
                ISOFileCountText = "Extracting file...";
                ISOCurrentFileText = SelectedISOMediaFile!;
                ISOCurrentSizeText = string.Empty;
                ISOProcessedText = string.Empty;
                ISORemainingText = string.Empty;
                ISOSpeedText = string.Empty;
                ISOEtaText = string.Empty;
                ISOProcessing = true;

                Log($"Extracting {SelectedISOMediaFile} from ISO...");

                await ISOMediaExtractor.ExtractFileAsync(
                    ISOFilePath, SelectedISOMediaFile!,
                    tempFile,
                    percent => Application.Current.Dispatcher.BeginInvoke(() =>
                    {
                        ISOOverallPercent = percent;
                        ISOCurrentPercent = percent;
                    }),
                    _cts.Token);

                ISOProcessing = false;

                long size = new FileInfo(tempFile).Length;
                Log($"Extracted ({size:N0} bytes).");
                samplePath = tempFile;
            }
            else
            {
                samplePath = InputPath;
            }

            Log($"Input:  {samplePath}");
            Log($"Output: {OutputPath}");

            // Show progress modal during profiling (can take many seconds on large samples)
            ISOProgressHeading = "Profiling Sample";
            ISOCurrentFileText = Path.GetFileName(samplePath);
            ISOFileCountText = "Reading sample structure and computing CRC...";
            ISOOverallPercent = 0;
            ISOCurrentPercent = 0;
            ISOCurrentSizeText = string.Empty;
            ISOProcessedText = string.Empty;
            ISORemainingText = string.Empty;
            ISOSpeedText = string.Empty;
            ISOEtaText = string.Empty;
            _scanStopwatch = Stopwatch.StartNew();
            _scanModalActive = true;
            ISOProcessing = true;

            // Yield to let the dispatcher open the modal before heavy work starts
            await Task.Yield();

            SRSCreationResult result = await _sRSService.CreateAsync(
                OutputPath, samplePath, options, _cts.Token);

            _scanModalActive = false;
            ISOProcessing = false;

            if (result.Success)
            {
                ProgressPercent = 100;
                ProgressMessage = "Complete!";
                Log($"SRS created successfully.");
                Log($"  Container: {result.ContainerType}");
                Log($"  Tracks: {result.TrackCount}");
                Log($"  Sample CRC: {result.SampleCRC32:X8}");
                Log($"  Sample size: {result.SampleSize:N0} bytes");
                Log($"  SRS size: {result.SRSFileSize:N0} bytes");
            }
            else
            {
                ProgressMessage = "Failed.";
                Log($"ERROR: {result.ErrorMessage}");
            }

            foreach (string warning in result.Warnings)
            {
                Log($"WARNING: {warning}");
            }
        }
        catch (OperationCanceledException)
        {
            ProgressMessage = "Cancelled.";
            Log("Cancelled.");
        }
        catch (Exception ex)
        {
            ProgressMessage = "Error.";
            Log($"ERROR: {ex.Message}");
        }
        finally
        {
            _scanModalActive = false;
            ISOProcessing = false;
            IsCreating = false;
            _cts?.Dispose();
            _cts = null;
            CleanupTempFile();
        }
    }

    [RelayCommand]
    private void CancelCreation()
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

    private void OnProgress(object? _, SRSCreationProgressEventArgs e)
    {
        Application.Current.Dispatcher.BeginInvoke(() =>
        {
            ProgressMessage = e.Message;
            Log(e.Message);

            if (!_scanModalActive)
            {
                return;
            }

            // Transition the modal as we move from profiling -> verifying -> writing -> complete
            if (e.Message.StartsWith("Verifying sample against main file", StringComparison.OrdinalIgnoreCase))
            {
                ISOProgressHeading = "Verifying Against Main File";
                ISOCurrentFileText = "Searching for track signatures in main file...";
                ISOOverallPercent = 0;
                ISOCurrentPercent = 0;
                _scanStopwatch?.Restart();
            }
            else if (e.Message.StartsWith("Writing SRS", StringComparison.OrdinalIgnoreCase))
            {
                ISOProgressHeading = "Writing SRS";
                ISOCurrentFileText = "Writing SRS file...";
                ISOOverallPercent = 100;
                ISOCurrentPercent = 100;
            }
        });
    }

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
            UpdateScanStats(e.BytesScanned, e.BytesTotal);
        });
    }

    private void UpdateScanStats(long processed, long total)
    {
        if (total <= 0 || _scanStopwatch is null)
        {
            return;
        }

        double elapsed = _scanStopwatch.Elapsed.TotalSeconds;
        ISOProcessedText = $"{FormatUtilities.FormatSize(processed)} / {FormatUtilities.FormatSize(total)}";

        long remaining = total - processed;
        ISORemainingText = FormatUtilities.FormatSize(remaining);

        if (elapsed > 0.5 && processed > 0)
        {
            double bytesPerSec = processed / elapsed;
            ISOSpeedText = $"{FormatUtilities.FormatSize((long)bytesPerSec)}/s";

            double secondsRemaining = remaining / bytesPerSec;
            ISOEtaText = secondsRemaining < 60
                ? $"{secondsRemaining:F0}s"
                : $"{(int)(secondsRemaining / 60)}m {(int)(secondsRemaining % 60)}s";
        }
    }

    private void Log(string message) => AppendLogEntry(LogEntries, message);

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

    private void AutoSetOutputPath(string inputPath)
    {
        if (string.IsNullOrWhiteSpace(OutputPath))
        {
            string dir = Path.GetDirectoryName(inputPath) ?? ".";
            string name = Path.GetFileNameWithoutExtension(inputPath);
            OutputPath = Path.Combine(dir, name + ".srs");
        }
    }
}
