using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ReScene.NET.Helpers;
using ReScene.NET.Services;
using ReScene.SRS;

namespace ReScene.NET.ViewModels;

public partial class SrsCreatorViewModel : ViewModelBase
{
    private readonly ISrsCreationService _srsService;
    private readonly IFileDialogService _fileDialog;
    private readonly ITempDirectoryService _tempDir;
    private CancellationTokenSource? _cts;
    private string? _extractedTempFile;

    public SrsCreatorViewModel(ISrsCreationService srsService, IFileDialogService fileDialog, ITempDirectoryService tempDir)
    {
        _srsService = srsService;
        _fileDialog = fileDialog;
        _tempDir = tempDir;

        _srsService.Progress += OnProgress;
    }

    #region Batch Mode

    /// <summary>
    /// Gets or sets whether batch mode is enabled.
    /// </summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CreateSrsCommand))]
    [NotifyPropertyChangedFor(nameof(ShowIsoSelection))]
    private bool _isBatchMode;

    /// <summary>
    /// Gets or sets the currently selected batch file item.
    /// </summary>
    [ObservableProperty]
    private BatchSrsItem? _selectedBatchFile;

    /// <summary>
    /// Gets the collection of files to process in batch mode.
    /// </summary>
    public ObservableCollection<BatchSrsItem> BatchFiles { get; } = [];

    /// <summary>
    /// Represents a single file entry in the batch SRS creation list.
    /// </summary>
    public partial class BatchSrsItem : ObservableObject
    {
        [ObservableProperty]
        private string _filePath = string.Empty;

        [ObservableProperty]
        private string _outputPath = string.Empty;

        [ObservableProperty]
        private string _status = "Pending";

        public string FileName => Path.GetFileName(FilePath);
    }

    [RelayCommand]
    private async Task AddBatchFilesAsync()
    {
        IReadOnlyList<string> paths = await _fileDialog.OpenFilesAsync(
            "Select Sample Files", FileDialogFilters.MediaSamples);

        AddBatchFilePaths(paths);
    }

    /// <summary>
    /// Adds the specified file paths to the batch list.
    /// Called from both the command and drag-drop handler.
    /// </summary>
    public void AddBatchFilePaths(IReadOnlyList<string> paths)
    {
        foreach (string path in paths)
        {
            if (BatchFiles.Any(f => f.FilePath.Equals(path, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            string dir = Path.GetDirectoryName(path) ?? ".";
            string name = Path.GetFileNameWithoutExtension(path);

            BatchFiles.Add(new BatchSrsItem
            {
                FilePath = path,
                OutputPath = Path.Combine(dir, name + ".srs")
            });
        }

        CreateSrsCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand]
    private void RemoveBatchFile()
    {
        if (SelectedBatchFile is not null)
        {
            BatchFiles.Remove(SelectedBatchFile);
            CreateSrsCommand.NotifyCanExecuteChanged();
        }
    }

    [RelayCommand]
    private void ClearBatchFiles()
    {
        BatchFiles.Clear();
        CreateSrsCommand.NotifyCanExecuteChanged();
    }

    #endregion

    // Input
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CreateSrsCommand))]
    private string _inputPath = string.Empty;

    // ISO support
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowIsoSelection))]
    private bool _isIsoSource;

    [ObservableProperty]
    private string _isoFilePath = string.Empty;

    public ObservableCollection<string> IsoMediaFiles { get; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CreateSrsCommand))]
    private string? _selectedIsoMediaFile;

    /// <summary>
    /// Gets whether the ISO file selection combo should be visible.
    /// Only shown when an ISO source is loaded and batch mode is off.
    /// </summary>
    public bool ShowIsoSelection => IsIsoSource && !IsBatchMode;

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

    // Output
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CreateSrsCommand))]
    private string _outputPath = string.Empty;

    // Options
    [ObservableProperty]
    private string _appName = FormatUtilities.GetDefaultAppName();

    // Progress
    [ObservableProperty]
    private int _progressPercent;

    [ObservableProperty]
    private string _progressMessage = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CreateSrsCommand))]
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

        if (IsoMediaExtractor.IsIsoFile(path))
        {
            IsIsoSource = true;
            IsoFilePath = path;
            InputPath = path;

            IsoMediaFiles.Clear();
            SelectedIsoMediaFile = null;

            List<string> files = IsoMediaExtractor.ListMediaFiles(path);
            foreach (string file in files)
            {
                IsoMediaFiles.Add(file);
            }

            if (IsoMediaFiles.Count > 0)
            {
                SelectedIsoMediaFile = IsoMediaFiles[0];
            }

            AutoSetOutputPath(path);
        }
        else
        {
            IsIsoSource = false;
            IsoFilePath = string.Empty;
            IsoMediaFiles.Clear();
            SelectedIsoMediaFile = null;
            InputPath = path;
            AutoSetOutputPath(path);
        }
    }

    [RelayCommand]
    private async Task BrowseOutputAsync()
    {
        string? path = await _fileDialog.SaveFileAsync(
            "Save SRS File", ".srs", FileDialogFilters.SrsSave);
        if (path is not null)
        {
            OutputPath = path;
        }
    }

    private bool CanCreateSrs()
    {
        if (IsCreating)
        {
            return false;
        }

        if (IsBatchMode)
        {
            return BatchFiles.Count > 0;
        }

        if (string.IsNullOrWhiteSpace(InputPath) || string.IsNullOrWhiteSpace(OutputPath))
        {
            return false;
        }

        if (IsIsoSource)
        {
            return !string.IsNullOrWhiteSpace(SelectedIsoMediaFile);
        }

        return true;
    }

    [RelayCommand(CanExecute = nameof(CanCreateSrs))]
    private async Task CreateSrsAsync()
    {
        if (IsBatchMode)
        {
            await CreateBatchSrsAsync();
            return;
        }

        await CreateSingleSrsAsync();
    }

    private async Task CreateSingleSrsAsync()
    {
        IsCreating = true;
        ShowProgress = true;
        ProgressPercent = 0;
        ProgressMessage = "Starting...";
        LogEntries.Clear();

        _cts = new CancellationTokenSource();

        try
        {
            var options = new SrsCreationOptions
            {
                AppName = string.IsNullOrWhiteSpace(AppName) ? FormatUtilities.GetDefaultAppName() : AppName
            };

            Log("Starting SRS creation...");

            string samplePath;

            if (IsIsoSource)
            {
                Log($"ISO image: {IsoFilePath}");
                Log($"ISO file:  {SelectedIsoMediaFile}");

                string tempDir = _tempDir.CreateTempDirectory();
                string tempFile = Path.Combine(tempDir, Path.GetFileName(SelectedIsoMediaFile!));
                _extractedTempFile = tempFile;

                // Show ISO progress modal
                IsoProgressHeading = "Extracting from ISO";
                IsoOverallPercent = 0;
                IsoCurrentPercent = 0;
                IsoFileCountText = "Extracting file...";
                IsoCurrentFileText = SelectedIsoMediaFile!;
                IsoCurrentSizeText = string.Empty;
                IsoProcessedText = string.Empty;
                IsoRemainingText = string.Empty;
                IsoSpeedText = string.Empty;
                IsoEtaText = string.Empty;
                IsoProcessing = true;

                Log($"Extracting {SelectedIsoMediaFile} from ISO...");

                await IsoMediaExtractor.ExtractFileAsync(
                    IsoFilePath, SelectedIsoMediaFile!,
                    tempFile,
                    percent => Application.Current.Dispatcher.BeginInvoke(() =>
                    {
                        IsoOverallPercent = percent;
                        IsoCurrentPercent = percent;
                    }),
                    _cts.Token);

                IsoProcessing = false;

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

            SrsCreationResult result = await _srsService.CreateAsync(
                OutputPath, samplePath, options, _cts.Token);

            if (result.Success)
            {
                ProgressPercent = 100;
                ProgressMessage = "Complete!";
                Log($"SRS created successfully.");
                Log($"  Container: {result.ContainerType}");
                Log($"  Tracks: {result.TrackCount}");
                Log($"  Sample CRC: {result.SampleCrc32:X8}");
                Log($"  Sample size: {result.SampleSize:N0} bytes");
                Log($"  SRS size: {result.SrsFileSize:N0} bytes");
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
            IsoProcessing = false;
            IsCreating = false;
            _cts?.Dispose();
            _cts = null;
            CleanupTempFile();
        }
    }

    private async Task CreateBatchSrsAsync()
    {
        IsCreating = true;
        ShowProgress = true;
        ProgressPercent = 0;
        ProgressMessage = "Starting batch...";
        LogEntries.Clear();

        _cts = new CancellationTokenSource();

        int total = BatchFiles.Count;
        int completed = 0;
        int succeeded = 0;
        int failed = 0;

        try
        {
            var options = new SrsCreationOptions
            {
                AppName = string.IsNullOrWhiteSpace(AppName) ? FormatUtilities.GetDefaultAppName() : AppName
            };

            Log($"Starting batch SRS creation ({total} files)...");

            foreach (BatchSrsItem item in BatchFiles)
            {
                _cts.Token.ThrowIfCancellationRequested();

                item.Status = "Creating...";
                ProgressMessage = $"Processing {completed + 1} of {total}: {item.FileName}";
                Log($"Processing: {item.FileName}");

                try
                {
                    SrsCreationResult result = await _srsService.CreateAsync(
                        item.OutputPath, item.FilePath, options, _cts.Token);

                    if (result.Success)
                    {
                        item.Status = "Done";
                        succeeded++;
                        Log($"  OK — {result.ContainerType}, {result.TrackCount} track(s), CRC {result.SampleCrc32:X8}");
                    }
                    else
                    {
                        item.Status = $"Error: {result.ErrorMessage}";
                        failed++;
                        Log($"  ERROR: {result.ErrorMessage}");
                    }

                    foreach (string warning in result.Warnings)
                    {
                        Log($"  WARNING: {warning}");
                    }
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    item.Status = $"Error: {ex.Message}";
                    failed++;
                    Log($"  ERROR: {ex.Message}");
                }

                completed++;
                ProgressPercent = (int)((double)completed / total * 100);
            }

            ProgressPercent = 100;
            ProgressMessage = $"Batch complete — {succeeded} succeeded, {failed} failed.";
            Log($"Batch complete: {succeeded} succeeded, {failed} failed out of {total}.");
        }
        catch (OperationCanceledException)
        {
            ProgressMessage = "Cancelled.";
            Log("Cancelled.");

            // Mark remaining items as cancelled
            foreach (BatchSrsItem item in BatchFiles)
            {
                if (item.Status == "Pending" || item.Status == "Creating...")
                {
                    item.Status = "Cancelled";
                }
            }
        }
        catch (Exception ex)
        {
            ProgressMessage = "Error.";
            Log($"ERROR: {ex.Message}");
        }
        finally
        {
            IsCreating = false;
            _cts?.Dispose();
            _cts = null;
        }
    }

    [RelayCommand]
    private void CancelCreation()
    {
        _cts?.Cancel();
        Log("Cancellation requested...");
    }

    private void OnProgress(object? _, SrsCreationProgressEventArgs e)
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
