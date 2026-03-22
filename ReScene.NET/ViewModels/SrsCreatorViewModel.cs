using System.Collections.ObjectModel;
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

    // Output
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CreateSrsCommand))]
    private string _outputPath = string.Empty;

    // Options
    [ObservableProperty]
    private string _appName = "ReScene.NET";

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
            "All Files|*.*"
        ]);

        if (path != null)
        {
            InputPath = path;
            AutoSetOutputPath(path);
        }
    }

    [RelayCommand]
    private async Task BrowseOutputAsync()
    {
        string? path = await _fileDialog.SaveFileAsync(
            "Save SRS File", ".srs", ["SRS Files|*.srs"]);
        if (path != null)
            OutputPath = path;
    }

    private bool CanCreateSrs() => !IsCreating
        && !string.IsNullOrWhiteSpace(InputPath)
        && !string.IsNullOrWhiteSpace(OutputPath);

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
                AppName = string.IsNullOrWhiteSpace(AppName) ? "ReScene.NET" : AppName
            };

            Log("Starting SRS creation...");
            Log($"Input: {InputPath}");
            Log($"Output: {OutputPath}");

            var result = await _srsService.CreateAsync(
                OutputPath, InputPath, options, _cts.Token);

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

    private void Log(string message)
    {
        string entry = $"{DateTime.Now:HH:mm:ss} {message}";
        LogEntries.Add(entry);
    }

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
