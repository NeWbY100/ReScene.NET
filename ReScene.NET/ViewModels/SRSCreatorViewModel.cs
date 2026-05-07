using System.Collections.ObjectModel;
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
    private string _inputPath = string.Empty;

    // ISO support
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowISOSelection))]
    private bool _isISOSource;

    [ObservableProperty]
    private string _iSOFilePath = string.Empty;

    public ObservableCollection<string> ISOMediaFiles { get; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CreateSRSCommand))]
    private string? _selectedISOMediaFile;

    /// <summary>
    /// Gets whether the ISO file selection combo should be visible.
    /// </summary>
    public bool ShowISOSelection => IsISOSource;

    // ISO progress (for modal window)
    [ObservableProperty]
    private string _iSOProgressHeading = string.Empty;

    [ObservableProperty]
    private int _iSOOverallPercent;

    [ObservableProperty]
    private string _iSOFileCountText = string.Empty;

    [ObservableProperty]
    private int _iSOCurrentPercent;

    [ObservableProperty]
    private string _iSOCurrentFileText = string.Empty;

    [ObservableProperty]
    private string _iSOProcessedText = string.Empty;

    [ObservableProperty]
    private string _iSORemainingText = string.Empty;

    [ObservableProperty]
    private string _iSOCurrentSizeText = string.Empty;

    [ObservableProperty]
    private string _iSOSpeedText = string.Empty;

    [ObservableProperty]
    private string _iSOEtaText = string.Empty;

    [ObservableProperty]
    private bool _iSOProcessing;

    // Output
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CreateSRSCommand))]
    private string _outputPath = string.Empty;

    // Options
    [ObservableProperty]
    private string _appName = string.Empty;

    // Progress
    [ObservableProperty]
    private int _progressPercent;

    [ObservableProperty]
    private string _progressMessage = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CreateSRSCommand))]
    private bool _isCreating;

    [ObservableProperty]
    private bool _showProgress;

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
                AppName = string.IsNullOrWhiteSpace(AppName) ? FormatUtilities.GetDefaultAppName() : AppName
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

            SRSCreationResult result = await _sRSService.CreateAsync(
                OutputPath, samplePath, options, _cts.Token);

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
        });
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
