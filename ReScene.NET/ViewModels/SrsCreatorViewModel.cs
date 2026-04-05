using System.Collections.ObjectModel;
using System.Reflection;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ReScene.NET.Services;
using ReScene.SRS;

namespace ReScene.NET.ViewModels;

public partial class SrsCreatorViewModel : ViewModelBase
{
    private readonly ISrsCreationService _srsService;
    private readonly IFileDialogService _fileDialog;
    private CancellationTokenSource? _cts;
    private string? _extractedTempFile;

    public SrsCreatorViewModel(ISrsCreationService srsService, IFileDialogService fileDialog)
    {
        _srsService = srsService;
        _fileDialog = fileDialog;

        _srsService.Progress += OnProgress;
    }

    // Input
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CreateSrsCommand))]
    private string _inputPath = string.Empty;

    // ISO support
    [ObservableProperty]
    private bool _isIsoSource;

    [ObservableProperty]
    private string _isoFilePath = string.Empty;

    public ObservableCollection<string> IsoMediaFiles { get; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CreateSrsCommand))]
    private string? _selectedIsoMediaFile;

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
    private string _appName = GetDefaultAppName();

    private static string GetDefaultAppName()
    {
        string? version = Assembly.GetEntryAssembly()?
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;

        if (version is null)
        {
            return "ReScene.NET";
        }

        int plus = version.IndexOf('+', StringComparison.Ordinal);
        return plus >= 0
            ? $"ReScene.NET v{version[..plus]} ({version[(plus + 1)..]})"
            : $"ReScene.NET v{version}";
    }

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
        [
            "Video Samples|*.avi;*.mkv;*.mp4;*.wmv;*.m4v",
            "Audio Samples|*.flac;*.mp3",
            "Stream Samples|*.vob;*.m2ts;*.ts;*.mpg;*.mpeg;*.evo",
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
            "Save SRS File", ".srs", ["SRS Files|*.srs"]);
        if (path is not null)
        {
            OutputPath = path;
        }
    }

    private bool CanCreateSrs()
    {
        if (IsCreating || string.IsNullOrWhiteSpace(InputPath) || string.IsNullOrWhiteSpace(OutputPath))
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
                AppName = string.IsNullOrWhiteSpace(AppName) ? GetDefaultAppName() : AppName
            };

            Log("Starting SRS creation...");

            string samplePath;

            if (IsIsoSource)
            {
                Log($"ISO image: {IsoFilePath}");
                Log($"ISO file:  {SelectedIsoMediaFile}");

                string tempDir = Path.Combine(Path.GetTempPath(), "ReScene.NET", Guid.NewGuid().ToString("N")[..8]);
                Directory.CreateDirectory(tempDir);
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

    private void Log(string message)
    {
        string entry = $"{DateTime.Now:HH:mm:ss} {message}";
        LogEntries.Add(entry);
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
