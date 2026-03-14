using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ReScene.NET.Services;
using SRRLib;

namespace ReScene.NET.ViewModels;

public partial class CreatorViewModel : ViewModelBase
{
    private readonly ISrrCreationService _srrService;
    private readonly IFileDialogService _fileDialog;
    private CancellationTokenSource? _cts;

    public CreatorViewModel(ISrrCreationService srrService, IFileDialogService fileDialog)
    {
        _srrService = srrService;
        _fileDialog = fileDialog;

        _srrService.Progress += OnProgress;
    }

    // Input
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CreateSrrCommand))]
    private string _inputPath = string.Empty;

    [ObservableProperty]
    private bool _isSfvInput = true;

    // Stored Files
    public ObservableCollection<string> StoredFiles { get; } = [];

    [ObservableProperty]
    private string? _selectedStoredFile;

    // Output
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CreateSrrCommand))]
    private string _outputPath = string.Empty;

    // Options
    [ObservableProperty]
    private bool _allowCompressed = true;

    [ObservableProperty]
    private bool _storePaths = true;

    [ObservableProperty]
    private bool _computeOsoHashes;

    [ObservableProperty]
    private string _appName = "ReScene.NET";

    // Progress
    [ObservableProperty]
    private int _progressPercent;

    [ObservableProperty]
    private string _progressMessage = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CreateSrrCommand))]
    private bool _isCreating;

    [ObservableProperty]
    private bool _showProgress;

    // Log
    public ObservableCollection<string> LogEntries { get; } = [];

    [RelayCommand]
    private async Task BrowseInputAsync()
    {
        string? path = await _fileDialog.OpenFileAsync("Select Input File",
            ["SFV Files|*.sfv", "RAR Files|*.rar", "All Files|*.*"]);

        if (path != null)
        {
            InputPath = path;
            AutoSetOutputPath(path);
        }
    }

    partial void OnInputPathChanged(string value)
    {
        if (!string.IsNullOrWhiteSpace(value))
            IsSfvInput = Path.GetExtension(value).Equals(".sfv", StringComparison.OrdinalIgnoreCase);
    }

    [RelayCommand]
    private async Task BrowseOutputAsync()
    {
        string? path = await _fileDialog.SaveFileAsync(
            "Save SRR File", ".srr", ["SRR Files|*.srr"]);
        if (path != null)
            OutputPath = path;
    }

    [RelayCommand]
    private async Task AddStoredFileAsync()
    {
        var paths = await _fileDialog.OpenFilesAsync(
            "Select Files to Store", ["NFO/SFV Files|*.nfo;*.sfv;*.txt", "All Files|*.*"]);

        foreach (string path in paths)
        {
            if (!StoredFiles.Contains(path))
                StoredFiles.Add(path);
        }
    }

    [RelayCommand]
    private void RemoveStoredFile()
    {
        if (SelectedStoredFile != null)
            StoredFiles.Remove(SelectedStoredFile);
    }

    private bool CanCreateSrr() => !IsCreating
        && !string.IsNullOrWhiteSpace(InputPath)
        && !string.IsNullOrWhiteSpace(OutputPath);

    [RelayCommand(CanExecute = nameof(CanCreateSrr))]
    private async Task CreateSrrAsync()
    {
        IsCreating = true;
        ShowProgress = true;
        ProgressPercent = 0;
        ProgressMessage = "Starting...";
        LogEntries.Clear();

        _cts = new CancellationTokenSource();

        try
        {
            var options = new SrrCreationOptions
            {
                AppName = string.IsNullOrWhiteSpace(AppName) ? null : AppName,
                AllowCompressed = AllowCompressed,
                StorePaths = StorePaths,
                ComputeOsoHashes = ComputeOsoHashes
            };

            Log("Starting SRR creation...");
            Log($"Input: {InputPath}");
            Log($"Output: {OutputPath}");

            SrrCreationResult result;

            if (IsSfvInput)
            {
                var additionalFiles = StoredFiles.ToList();
                result = await _srrService.CreateFromSfvAsync(
                    OutputPath, InputPath, additionalFiles, options, _cts.Token);
            }
            else
            {
                // Single RAR file input - discover all volumes from the first one
                var volumes = DiscoverRarVolumes(InputPath);
                Log($"Found {volumes.Count} volume(s).");

                var storedFiles = new Dictionary<string, string>();
                foreach (string path in StoredFiles)
                {
                    storedFiles[Path.GetFileName(path)] = path;
                }

                result = await _srrService.CreateFromRarAsync(
                    OutputPath, volumes,
                    storedFiles.Count > 0 ? storedFiles : null,
                    options, _cts.Token);
            }

            if (result.Success)
            {
                ProgressPercent = 100;
                ProgressMessage = "Complete!";
                Log($"SRR created successfully.");
                Log($"  Volumes: {result.VolumeCount}");
                Log($"  Stored files: {result.StoredFileCount}");
                Log($"  SRR size: {result.SrrFileSize:N0} bytes");
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

    private void OnProgress(object? _, SrrCreationProgressEventArgs e)
    {
        ProgressPercent = e.ProgressPercent;
        ProgressMessage = e.Message;
        Log(e.Message);
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
            OutputPath = Path.Combine(dir, name + ".srr");
        }
    }

    private static List<string> DiscoverRarVolumes(string firstRarPath)
    {
        string dir = Path.GetDirectoryName(firstRarPath) ?? ".";
        string baseName = Path.GetFileNameWithoutExtension(firstRarPath);

        var volumes = new List<string>();

        // Check for new-style naming: name.part01.rar, name.part02.rar
        if (baseName.Contains(".part", StringComparison.OrdinalIgnoreCase))
        {
            // Find all partNN.rar files with the same base name
            string pattern = baseName[..baseName.LastIndexOf(".part", StringComparison.OrdinalIgnoreCase)];
            foreach (string file in Directory.GetFiles(dir, $"{pattern}.part*.rar"))
            {
                volumes.Add(file);
            }
        }
        else
        {
            // Old-style: name.rar, name.r00, name.r01, etc.
            volumes.Add(firstRarPath);
            for (int i = 0; i < 999; i++)
            {
                string ext;
                if (i < 100)
                {
                    int letterIndex = i / 100;
                    char letter = (char)('r' + letterIndex);
                    ext = $".{letter}{i % 100:D2}";
                }
                else
                {
                    int letterIndex = i / 100;
                    if (letterIndex > 25) break;
                    char letter = (char)('r' + letterIndex);
                    ext = $".{letter}{i % 100:D2}";
                }

                string nextVolume = Path.Combine(dir, baseName + ext);
                if (File.Exists(nextVolume))
                    volumes.Add(nextVolume);
                else
                    break;
            }
        }

        volumes.Sort(SRRWriter.CompareRarVolumeNames);
        return volumes;
    }
}
