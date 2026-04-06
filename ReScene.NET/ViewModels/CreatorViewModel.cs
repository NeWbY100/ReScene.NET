using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ReScene.NET.Helpers;
using ReScene.NET.Services;
using ReScene.SRR;
using ReScene.SRS;

namespace ReScene.NET.ViewModels;

/// <summary>
/// ViewModel for the SRR Creator tab, handling SRR file creation from SFV or RAR inputs.
/// </summary>
public partial class CreatorViewModel : ViewModelBase
{
    private readonly ISrrCreationService _srrService;
    private readonly ISrsCreationService _srsService;
    private readonly IFileDialogService _fileDialog;
    private readonly ITempDirectoryService _tempDir;
    private CancellationTokenSource? _cts;

    public CreatorViewModel(ISrrCreationService srrService, ISrsCreationService srsService, IFileDialogService fileDialog, ITempDirectoryService tempDir)
    {
        _srrService = srrService;
        _srsService = srsService;
        _fileDialog = fileDialog;
        _tempDir = tempDir;

        _srrService.Progress += OnProgress;
    }

    // Input
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CreateSrrCommand))]
    private string _inputPath = string.Empty;

    [ObservableProperty]
    private bool _isSfvInput = true;

    // Stored Files
    public ObservableCollection<StoredFileItem> StoredFiles { get; } = [];

    [ObservableProperty]
    private StoredFileItem? _selectedStoredFile;

    // Output
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CreateSrrCommand))]
    private string _outputPath = string.Empty;

    // Options
    [ObservableProperty]
    private bool _allowCompressed = true;

    [ObservableProperty]
    private bool _autoIncludeFiles = true;

    [ObservableProperty]
    private bool _autoCreateSrs = true;

    [ObservableProperty]
    private bool _createVobsubSrr = true;

    [ObservableProperty]
    private bool _storeFixRar = true;

    [ObservableProperty]
    private bool _computeOsoHashes;

    [ObservableProperty]
    private bool _generateLanguagesDiz = true;

    [ObservableProperty]
    private string _appName = FormatUtilities.GetDefaultAppName();

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
            FileDialogFilters.SfvAndRar);

        if (path is not null)
        {
            InputPath = path;
            AutoSetOutputPath(path);
        }
    }

    partial void OnInputPathChanged(string value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            IsSfvInput = Path.GetExtension(value).Equals(".sfv", StringComparison.OrdinalIgnoreCase);
        }

        UpdateStoredNames();
        AutoScanReleaseFiles();
    }

    [RelayCommand]
    private async Task BrowseOutputAsync()
    {
        string? path = await _fileDialog.SaveFileAsync(
            "Save SRR File", ".srr", FileDialogFilters.SrrSave);
        if (path is not null)
        {
            OutputPath = path;
        }
    }

    [RelayCommand]
    private async Task AddStoredFileAsync()
    {
        IReadOnlyList<string> paths = await _fileDialog.OpenFilesAsync(
            "Select Files to Store", FileDialogFilters.StoredFiles);

        foreach (string path in paths)
        {
            if (StoredFiles.Any(f => f.FullPath.Equals(path, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            StoredFiles.Add(new StoredFileItem
            {
                FullPath = path,
                StoredName = ComputeStoredName(path)
            });
        }
    }

    [RelayCommand]
    private void RemoveStoredFile()
    {
        if (SelectedStoredFile is not null)
        {
            StoredFiles.Remove(SelectedStoredFile);
        }
    }

    [RelayCommand]
    private void RemoveAllStoredFiles() => StoredFiles.Clear();

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
        string? tempDir = null;

        try
        {
            var options = new SrrCreationOptions
            {
                AppName = string.IsNullOrWhiteSpace(AppName) ? null : AppName,
                AllowCompressed = AllowCompressed,
                ComputeOsoHashes = ComputeOsoHashes,
                GenerateLanguagesDiz = GenerateLanguagesDiz
            };

            Log("Starting SRR creation...");
            Log($"Input: {InputPath}");
            Log($"Output: {OutputPath}");

            string releaseDir = Path.GetDirectoryName(InputPath) ?? ".";

            // Phase 1: Auto-create SRS files for samples
            if (AutoCreateSrs)
            {
                tempDir = await CreateSrsForSamplesAsync(releaseDir, _cts.Token);
            }

            // Phase 2: Create nested SRRs for subtitle archives
            if (CreateVobsubSrr)
            {
                await CreateVobsubSrrsAsync(releaseDir, options, tempDir ??= _tempDir.CreateTempDirectory(), _cts.Token);
            }

            // Phase 3: Store fix RAR if applicable
            if (StoreFixRar)
            {
                StoreFixRarFile(releaseDir);
            }

            // Phase 4: Create the main SRR
            SrrCreationResult result;

            var storedFiles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (StoredFileItem item in StoredFiles)
            {
                storedFiles[item.StoredName] = item.FullPath;
            }

            if (IsSfvInput)
            {
                result = await _srrService.CreateFromSfvAsync(
                    OutputPath, InputPath,
                    storedFiles.Count > 0 ? storedFiles : null,
                    options, _cts.Token);
            }
            else
            {
                List<string> volumes = DiscoverRarVolumes(InputPath);
                Log($"Found {volumes.Count} volume(s).");

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
            _tempDir.Cleanup(tempDir);
        }
    }

    [RelayCommand]
    private void CancelCreation()
    {
        _cts?.Cancel();
        Log("Cancellation requested...");
    }

    // ── Auto-scan ───────────────────────────────────────────

    private void AutoScanReleaseFiles()
    {
        if (!AutoIncludeFiles || string.IsNullOrWhiteSpace(InputPath))
        {
            return;
        }

        string releaseDir = Path.GetDirectoryName(InputPath) ?? ".";
        if (!Directory.Exists(releaseDir))
        {
            return;
        }

        StoredFiles.Clear();

        try
        {
            List<(string FullPath, string StoredName)> scanned = ReleaseFileScanner.ScanReleaseDirectory(releaseDir);
            foreach ((string? fullPath, string? storedName) in scanned)
            {
                StoredFiles.Add(new StoredFileItem
                {
                    FullPath = fullPath,
                    StoredName = storedName
                });
            }
        }
        catch
        {
            // Directory scan failures are non-fatal
        }
    }

    // ── SRS auto-creation ───────────────────────────────────

    private async Task<string?> CreateSrsForSamplesAsync(string releaseDir, CancellationToken ct)
    {
        List<string> samples = ReleaseFileScanner.FindSampleFiles(releaseDir);
        if (samples.Count == 0)
        {
            return null;
        }

        string tempDir = _tempDir.CreateTempDirectory();
        var srsOptions = new SrsCreationOptions
        {
            AppName = string.IsNullOrWhiteSpace(AppName) ? "ReScene.NET" : AppName
        };

        foreach (string samplePath in samples)
        {
            ct.ThrowIfCancellationRequested();

            string sampleName = Path.GetFileName(samplePath);
            string srsName = Path.ChangeExtension(sampleName, ".srs");
            string srsPath = Path.Combine(tempDir, srsName);

            Log($"Creating SRS for: {sampleName}");

            try
            {
                SrsCreationResult result = await _srsService.CreateAsync(srsPath, samplePath, srsOptions, ct);
                if (result.Success)
                {
                    string storedName = Path.GetRelativePath(releaseDir, samplePath).Replace('\\', '/');
                    storedName = Path.ChangeExtension(storedName, ".srs");

                    StoredFiles.Add(new StoredFileItem
                    {
                        FullPath = srsPath,
                        StoredName = storedName
                    });

                    Log($"  SRS created: {srsName} ({result.SrsFileSize:N0} bytes)");
                }
                else
                {
                    Log($"  SRS failed for {sampleName}: {result.ErrorMessage}");
                }
            }
            catch (Exception ex)
            {
                Log($"  SRS error for {sampleName}: {ex.Message}");
            }
        }

        return tempDir;
    }

    // ── Vobsub nested SRR ───────────────────────────────────

    private async Task CreateVobsubSrrsAsync(string releaseDir, SrrCreationOptions options, string tempDir, CancellationToken ct)
    {
        List<string> subtitleSfvs = ReleaseFileScanner.FindSubtitleSfvFiles(releaseDir);
        if (subtitleSfvs.Count == 0)
        {
            return;
        }

        foreach (string sfvPath in subtitleSfvs)
        {
            ct.ThrowIfCancellationRequested();

            string sfvName = Path.GetFileName(sfvPath);
            string srrName = Path.ChangeExtension(sfvName, ".srr");
            string srrPath = Path.Combine(tempDir, srrName);

            Log($"Creating nested SRR for: {sfvName}");

            try
            {
                SrrCreationResult result = await _srrService.CreateFromSfvAsync(
                    srrPath, sfvPath, null, options, ct);

                if (result.Success)
                {
                    string storedName = Path.GetRelativePath(releaseDir, sfvPath).Replace('\\', '/');
                    storedName = Path.ChangeExtension(storedName, ".srr");

                    StoredFiles.Add(new StoredFileItem
                    {
                        FullPath = srrPath,
                        StoredName = storedName
                    });

                    Log($"  Nested SRR created: {srrName} ({result.SrrFileSize:N0} bytes)");
                }
                else
                {
                    Log($"  Nested SRR failed for {sfvName}: {result.ErrorMessage}");
                }
            }
            catch (Exception ex)
            {
                Log($"  Nested SRR error for {sfvName}: {ex.Message}");
            }
        }
    }

    // ── Fix release detection ───────────────────────────────

    private void StoreFixRarFile(string releaseDir)
    {
        string releaseName = Path.GetFileName(releaseDir) ?? string.Empty;
        if (!ReleaseFileScanner.IsFixRelease(releaseName))
        {
            return;
        }

        // Find SFV files in the release root
        string[] sfvFiles = Directory.GetFiles(releaseDir, "*.sfv");
        if (sfvFiles.Length != 1)
        {
            return;
        }

        // Find RAR files referenced by the SFV
        List<string> rarFiles = ReleaseFileScanner.FindRarFilesFromSfv(sfvFiles[0]);
        if (rarFiles.Count != 1)
        {
            return;
        }

        string rarPath = rarFiles[0];
        string storedName = Path.GetFileName(rarPath);

        // Don't add if already in stored files
        if (StoredFiles.Any(f => f.StoredName.Equals(storedName, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        StoredFiles.Add(new StoredFileItem
        {
            FullPath = rarPath,
            StoredName = storedName
        });

        Log($"Fix release detected. Storing RAR: {storedName}");
    }

    // ── Progress & logging ──────────────────────────────────

    private void OnProgress(object? _, SrrCreationProgressEventArgs e)
    {
        Application.Current.Dispatcher.BeginInvoke(() =>
        {
            ProgressPercent = e.ProgressPercent;
            ProgressMessage = e.Message;
            Log(e.Message);
        });
    }

    private void Log(string message) => AppendLogEntry(LogEntries, message);

    // ── Helpers ─────────────────────────────────────────────

    private void AutoSetOutputPath(string inputPath)
    {
        if (string.IsNullOrWhiteSpace(OutputPath))
        {
            string dir = Path.GetDirectoryName(inputPath) ?? ".";
            string name = Path.GetFileNameWithoutExtension(inputPath);
            OutputPath = Path.Combine(dir, name + ".srr");
        }
    }

    private string ComputeStoredName(string fullPath)
    {
        if (string.IsNullOrWhiteSpace(InputPath))
        {
            return Path.GetFileName(fullPath);
        }

        string releaseDir = Path.GetDirectoryName(InputPath) ?? ".";
        string relative = Path.GetRelativePath(releaseDir, fullPath);

        if (relative.StartsWith("..", StringComparison.Ordinal))
        {
            return Path.GetFileName(fullPath);
        }

        return relative.Replace('\\', '/');
    }

    private void UpdateStoredNames()
    {
        foreach (StoredFileItem item in StoredFiles)
        {
            item.StoredName = ComputeStoredName(item.FullPath);
        }
    }

    private static List<string> DiscoverRarVolumes(string firstRarPath)
    {
        string dir = Path.GetDirectoryName(firstRarPath) ?? ".";
        string baseName = Path.GetFileNameWithoutExtension(firstRarPath);

        var volumes = new List<string>();

        if (baseName.Contains(".part", StringComparison.OrdinalIgnoreCase))
        {
            string pattern = baseName[..baseName.LastIndexOf(".part", StringComparison.OrdinalIgnoreCase)];
            foreach (string file in Directory.GetFiles(dir, $"{pattern}.part*.rar"))
            {
                volumes.Add(file);
            }
        }
        else
        {
            volumes.Add(firstRarPath);
            const int maxOldStyleVolumes = 999;
            const int volumesPerLetter = 100;
            const int maxLetterIndex = 25; // 'r' through 'z'
            for (int i = 0; i < maxOldStyleVolumes; i++)
            {
                int letterIndex = i / volumesPerLetter;
                if (letterIndex > maxLetterIndex)
                {
                    break;
                }
                char letter = (char)('r' + letterIndex);
                string ext = $".{letter}{i % volumesPerLetter:D2}";

                string nextVolume = Path.Combine(dir, baseName + ext);
                if (File.Exists(nextVolume))
                {
                    volumes.Add(nextVolume);
                }
                else
                {
                    break;
                }
            }
        }

        volumes.Sort(RarVolumeNameComparer.Instance);
        return volumes;
    }

    /// <summary>
    /// Represents a file to be stored inside the SRR, with its full path and relative stored name.
    /// </summary>
    public class StoredFileItem
    {
        /// <summary>Gets or sets the absolute path to the file on disk.</summary>
        public string FullPath { get; set; } = string.Empty;

        /// <summary>Gets or sets the relative name used when storing the file in the SRR.</summary>
        public string StoredName { get; set; } = string.Empty;
    }
}
