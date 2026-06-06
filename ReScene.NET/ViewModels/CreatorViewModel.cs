using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ReScene.NET.Helpers;
using ReScene.NET.Models;
using ReScene.NET.Services;
using ReScene.SRR;
using ReScene.SRS;

namespace ReScene.NET.ViewModels;

/// <summary>
/// ViewModel for the SRR Creator tab, handling SRR file creation from SFV or RAR inputs.
/// </summary>
public partial class CreatorViewModel : ViewModelBase
{
    private readonly ISrrCreationService _sRRService;
    private readonly ISrsCreationService _sRSService;
    private readonly IFileDialogService _fileDialog;
    private readonly ITempDirectoryService _tempDir;
    private readonly IAppSettingsService _settingsService;
    private CancellationTokenSource? _cts;

    public CreatorViewModel(ISrrCreationService srrService, ISrsCreationService srsService, IFileDialogService fileDialog, ITempDirectoryService tempDir, IAppSettingsService settingsService)
    {
        _sRRService = srrService;
        _sRSService = srsService;
        _fileDialog = fileDialog;
        _tempDir = tempDir;
        _settingsService = settingsService;

        _sRRService.Progress += OnProgress;

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
    [NotifyCanExecuteChangedFor(nameof(CreateSRRCommand))]
    public partial string InputPath { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsSFVInput { get; set; } = true;

    // Stored Files
    public ObservableCollection<StoredFileItem> StoredFiles { get; } = [];

    [ObservableProperty]
    public partial StoredFileItem? SelectedStoredFile { get; set; }

    // Output
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CreateSRRCommand))]
    public partial string OutputPath { get; set; } = string.Empty;

    // Field guidance
    [ObservableProperty]
    public partial FieldStatus InputStatus { get; set; } = FieldStatus.None;

    [ObservableProperty]
    public partial FieldStatus OutputStatus { get; set; } = FieldStatus.None;

    [ObservableProperty]
    public partial string ActionHint { get; set; } = string.Empty;

    // Options
    [ObservableProperty]
    public partial bool AllowCompressed { get; set; } = true;

    [ObservableProperty]
    public partial bool AutoIncludeFiles { get; set; } = true;

    [ObservableProperty]
    public partial bool AutoCreateSRS { get; set; } = true;

    [ObservableProperty]
    public partial bool CreateVobsubSRR { get; set; } = true;

    [ObservableProperty]
    public partial bool StoreFixRar { get; set; } = true;

    [ObservableProperty]
    public partial bool ComputeOSOHashes { get; set; }

    [ObservableProperty]
    public partial bool GenerateLanguagesDiz { get; set; } = true;

    [ObservableProperty]
    public partial string AppName { get; set; } = string.Empty;

    // Progress
    [ObservableProperty]
    public partial int ProgressPercent { get; set; }

    [ObservableProperty]
    public partial string ProgressMessage { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CreateSRRCommand))]
    public partial bool IsCreating { get; set; }

    [ObservableProperty]
    public partial bool ShowProgress { get; set; }

    // Log
    public ObservableCollection<string> LogEntries { get; } = [];

    /// <summary>
    /// Clears all user-entered state back to a freshly-constructed default so a Beginner
    /// wizard opens clean. No-op while a creation is in progress (e.g. started from the
    /// Advanced tab) so an active run isn't disrupted.
    /// </summary>
    public void Reset()
    {
        if (IsCreating)
        {
            return;
        }

        InputPath = string.Empty;
        OutputPath = string.Empty;
        InputStatus = FieldStatus.None;
        OutputStatus = FieldStatus.None;
        ActionHint = string.Empty;

        StoredFiles.Clear();
        SelectedStoredFile = null;

        ProgressPercent = 0;
        ProgressMessage = string.Empty;
        ShowProgress = false;
        LogEntries.Clear();

        // Option toggles back to the same defaults the constructor / property initializers set.
        IsSFVInput = true;
        AllowCompressed = true;
        AutoIncludeFiles = true;
        AutoCreateSRS = true;
        CreateVobsubSRR = true;
        StoreFixRar = true;
        ComputeOSOHashes = false;
        GenerateLanguagesDiz = true;

        // Re-derive AppName / OutputPath from settings the same way the constructor does.
        AppSettings settings = _settingsService.Load();
        AppName = settings.DefaultAppName;

        if (!string.IsNullOrEmpty(settings.DefaultOutputDirectory))
        {
            OutputPath = settings.DefaultOutputDirectory;
        }
    }

    [RelayCommand]
    private async Task BrowseInputAsync()
    {
        string? path = await _fileDialog.OpenFileAsync("Select Input File",
            FileDialogFilters.SFVAndRar);

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
            IsSFVInput = Path.GetExtension(value).Equals(".sfv", StringComparison.OrdinalIgnoreCase);
        }

        UpdateStoredNames();
        AutoScanReleaseFiles();
        UpdateInputStatus(value);
        UpdateActionHint();
    }

    partial void OnOutputPathChanged(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            OutputStatus = FieldStatus.None;
        }

        UpdateActionHint();
    }

    partial void OnIsCreatingChanged(bool value) => UpdateActionHint();

    private void UpdateInputStatus(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            InputStatus = FieldStatus.None;
            return;
        }

        if (!File.Exists(value))
        {
            InputStatus = FieldStatus.Error("This file does not exist.");
            return;
        }

        string releaseDir = Path.GetDirectoryName(value) ?? ".";
        string releaseName = Path.GetFileName(releaseDir);
        int archiveCount = FieldGuidance.CountReleaseArchives(releaseDir);

        InputStatus = archiveCount > 0
            ? FieldStatus.Ok($"Release \"{releaseName}\" — {archiveCount} archive file(s) in this folder.")
            : FieldStatus.Warning($"No .rar volumes found in \"{releaseName}\". An SRR is built from the release's .rar files — they need to be in this folder next to the .sfv.");
    }

    private void UpdateActionHint()
    {
        if (IsCreating)
        {
            ActionHint = string.Empty;
        }
        else if (string.IsNullOrWhiteSpace(InputPath))
        {
            ActionHint = "Select an input file to continue.";
        }
        else if (string.IsNullOrWhiteSpace(OutputPath))
        {
            ActionHint = "Choose where to save the SRR to continue.";
        }
        else
        {
            ActionHint = string.Empty;
        }
    }

    [RelayCommand]
    private async Task BrowseOutputAsync()
    {
        string? path = await _fileDialog.SaveFileAsync(
            "Save SRR File", ".srr", FileDialogFilters.SRRSave);
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

    /// <summary>
    /// Adds files to the stored files list, skipping duplicates. Called from code-behind drag-drop.
    /// </summary>
    public void AddStoredFiles(IEnumerable<string> paths)
    {
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

    private bool CanCreateSRR() => !IsCreating
        && !string.IsNullOrWhiteSpace(InputPath)
        && !string.IsNullOrWhiteSpace(OutputPath);

    [RelayCommand(CanExecute = nameof(CanCreateSRR))]
    private async Task CreateSRRAsync()
    {
        if (File.Exists(OutputPath))
        {
            bool proceed = await _fileDialog.ShowConfirmAsync(
                "Overwrite existing SRR?",
                $"An SRR file already exists at:\n\n{OutputPath}\n\nDo you want to overwrite it?");
            if (!proceed)
            {
                return;
            }
        }

        IsCreating = true;
        ShowProgress = true;
        ProgressPercent = 0;
        ProgressMessage = "Starting...";
        LogEntries.Clear();

        _cts = new CancellationTokenSource();
        string? tempDir = null;

        try
        {
            var options = new SRRCreationOptions
            {
                AppName = string.IsNullOrWhiteSpace(AppName) ? null : AppName,
                AllowCompressed = AllowCompressed,
                ComputeOSOHashes = ComputeOSOHashes,
                GenerateLanguagesDiz = GenerateLanguagesDiz
            };

            Log("Starting SRR creation...");
            Log($"Input: {InputPath}");
            Log($"Output: {OutputPath}");

            string releaseDir = Path.GetDirectoryName(InputPath) ?? ".";

            // Phase 1: Auto-create SRS files for samples
            if (AutoCreateSRS)
            {
                tempDir = await CreateSRSForSamplesAsync(releaseDir, _cts.Token);
            }

            // Phase 2: Create nested SRRs for subtitle archives
            if (CreateVobsubSRR)
            {
                await CreateVobsubSrrsAsync(releaseDir, options, tempDir ??= _tempDir.CreateTempDirectory(), _cts.Token);
            }

            // Phase 3: Store fix RAR if applicable
            if (StoreFixRar)
            {
                StoreFixRarFile(releaseDir);
            }

            // Phase 4: Create the main SRR
            SRRCreationResult result;

            var storedFiles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (StoredFileItem item in StoredFiles)
            {
                storedFiles[item.StoredName] = item.FullPath;
            }

            if (IsSFVInput)
            {
                result = await _sRRService.CreateFromSFVAsync(
                    OutputPath, InputPath,
                    storedFiles.Count > 0 ? storedFiles : null,
                    options, _cts.Token);
            }
            else
            {
                List<string> volumes = DiscoverRarVolumes(InputPath);
                Log($"Found {volumes.Count} volume(s).");

                result = await _sRRService.CreateFromRarAsync(
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
                Log($"  SRR size: {result.SRRFileSize:N0} bytes");

                if (result.LanguagesDizIdxFiles.Count > 0)
                {
                    Log($"  VobSub .idx files found: {result.LanguagesDizIdxFiles.Count} ({string.Join(", ", result.LanguagesDizIdxFiles)})");
                }
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

    private async Task<string?> CreateSRSForSamplesAsync(string releaseDir, CancellationToken ct)
    {
        List<string> samples = ReleaseFileScanner.FindSampleFiles(releaseDir);
        if (samples.Count == 0)
        {
            return null;
        }

        string tempDir = _tempDir.CreateTempDirectory();
        var srsOptions = new SRSCreationOptions
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
                SRSCreationResult result = await _sRSService.CreateAsync(srsPath, samplePath, srsOptions, ct);
                if (result.Success)
                {
                    string storedName = Path.GetRelativePath(releaseDir, samplePath).Replace('\\', '/');
                    storedName = Path.ChangeExtension(storedName, ".srs");

                    StoredFiles.Add(new StoredFileItem
                    {
                        FullPath = srsPath,
                        StoredName = storedName
                    });

                    Log($"  SRS created: {srsName} ({result.SRSFileSize:N0} bytes)");
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

    private async Task CreateVobsubSrrsAsync(string releaseDir, SRRCreationOptions options, string tempDir, CancellationToken ct)
    {
        List<string> subtitleSfvs = ReleaseFileScanner.FindSubtitleSFVFiles(releaseDir);
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
                // Nested SRRs have no UI to curate stored files, so explicitly pass the
                // vobsub SFV and any NFOs in its directory to be stored.
                var nestedStoredFiles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                string sfvDir = Path.GetDirectoryName(sfvPath) ?? ".";
                foreach (string nfoFile in Directory.GetFiles(sfvDir, "*.nfo"))
                {
                    nestedStoredFiles[Path.GetFileName(nfoFile)] = nfoFile;
                }
                nestedStoredFiles[Path.GetFileName(sfvPath)] = sfvPath;

                SRRCreationResult result = await _sRRService.CreateFromSFVAsync(
                    srrPath, sfvPath, nestedStoredFiles, options, ct);

                if (result.Success)
                {
                    string storedName = Path.GetRelativePath(releaseDir, sfvPath).Replace('\\', '/');
                    storedName = Path.ChangeExtension(storedName, ".srr");

                    StoredFiles.Add(new StoredFileItem
                    {
                        FullPath = srrPath,
                        StoredName = storedName
                    });

                    Log($"  Nested SRR created: {srrName} ({result.SRRFileSize:N0} bytes)");
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
        List<string> rarFiles = ReleaseFileScanner.FindRarFilesFromSFV(sfvFiles[0]);
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

    private void OnProgress(object? _, SRRCreationProgressEventArgs e)
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
            OutputPath = FieldGuidance.SuggestSiblingPath(inputPath, ".srr");
            OutputStatus = FieldStatus.Info("Auto-filled next to the input. Change it if you want the SRR elsewhere.");
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

        volumes.Sort(RARVolumeNameComparer.Instance);
        return volumes;
    }

    /// <summary>
    /// Represents a file to be stored inside the SRR, with its full path and relative stored name.
    /// </summary>
    public class StoredFileItem
    {
        /// <summary>
        /// Gets or sets the absolute path to the file on disk.
        /// </summary>
        public string FullPath { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the relative name used when storing the file in the SRR.
        /// </summary>
        public string StoredName { get; set; } = string.Empty;
    }
}
