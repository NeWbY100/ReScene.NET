using System.Collections.ObjectModel;
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
public partial class CreatorViewModel : OperationViewModelBase
{
    private readonly ISrrCreationService _sRRService;
    private readonly ISrsCreationService _sRSService;
    private readonly IFileDialogService _fileDialog;
    private readonly ITempDirectoryService _tempDir;
    private readonly IAppSettingsService _settingsService;
    private readonly IUiDispatcher _uiDispatcher;

    public CreatorViewModel(ISrrCreationService srrService, ISrsCreationService srsService, IFileDialogService fileDialog, ITempDirectoryService tempDir, IAppSettingsService settingsService, IUiDispatcher? uiDispatcher = null)
    {
        _sRRService = srrService;
        _sRSService = srsService;
        _fileDialog = fileDialog;
        _tempDir = tempDir;
        _settingsService = settingsService;
        _uiDispatcher = uiDispatcher ?? new WpfDispatcher();

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

    // Sample / subtitle inputs added manually. The release scan turns samples into .srs and
    // subtitle archives into nested .srr automatically; these cover the case where they aren't
    // found because the release isn't extracted. Unioned with the auto-detected files at creation.
    public ObservableCollection<string> ExtraSampleFiles { get; } = [];

    [ObservableProperty]
    public partial string? SelectedExtraSample { get; set; }

    public ObservableCollection<string> ExtraSubtitleSfvFiles { get; } = [];

    [ObservableProperty]
    public partial string? SelectedExtraSubtitle { get; set; }

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
    [NotifyCanExecuteChangedFor(nameof(CreateSRRCommand))]
    public partial bool IsCreating { get; set; }

    /// <summary>
    /// True only after the most recent creation finished successfully. Lets a hosting wizard gate
    /// the step that follows the build (e.g. the Create-an-SRR "build a draft, then curate" flow).
    /// </summary>
    [ObservableProperty]
    public partial bool BuildSucceeded { get; set; }

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

        ExtraSampleFiles.Clear();
        SelectedExtraSample = null;
        ExtraSubtitleSfvFiles.Clear();
        SelectedExtraSubtitle = null;

        ProgressPercent = 0;
        ProgressMessage = string.Empty;
        ShowProgress = false;
        BuildSucceeded = false;
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
        string? suggested = FieldGuidance.SuggestSaveFileName(OutputPath, InputPath, ".srr");
        string? path = await _fileDialog.SaveFileAsync(
            "Save SRR File", ".srr", FileDialogFilters.SRRSave, suggested);
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

    [RelayCommand]
    private async Task RenameStoredFileAsync()
    {
        if (SelectedStoredFile is null)
        {
            return;
        }

        string prompt = "Name stored inside the SRR:";
        while (true)
        {
            string? input = await _fileDialog.PromptForTextAsync(
                "Rename stored file", prompt, SelectedStoredFile.StoredName);

            if (string.IsNullOrWhiteSpace(input))
            {
                return; // cancelled or blank — keep the original name
            }

            string newName = input.Replace('\\', '/').Trim();
            if (newName.Equals(SelectedStoredFile.StoredName, StringComparison.OrdinalIgnoreCase))
            {
                return; // unchanged
            }

            if (IsStoredNameTaken(newName, SelectedStoredFile))
            {
                // Re-prompt rather than accept a duplicate (which would later drop a file).
                prompt = $"A stored file is already named \"{newName}\". Choose a different name:";
                continue;
            }

            SelectedStoredFile.StoredName = newName;
            return;
        }
    }

    /// <summary>
    /// Whether another stored file (not <paramref name="except"/>) already uses
    /// <paramref name="storedName"/>, compared in the SRR's key space (forward slashes,
    /// case-insensitive).
    /// </summary>
    public bool IsStoredNameTaken(string storedName, StoredFileItem? except)
    {
        string normalized = storedName.Replace('\\', '/');
        return StoredFiles.Any(f => f != except
            && f.StoredName.Replace('\\', '/').Equals(normalized, StringComparison.OrdinalIgnoreCase));
    }

    // ── Sample / subtitle inputs (wizard "Samples & subtitles" step) ──

    [RelayCommand]
    private async Task AddSampleAsync()
    {
        IReadOnlyList<string> paths = await _fileDialog.OpenFilesAsync(
            "Select Sample File(s)", FileDialogFilters.MediaSamples);

        foreach (string path in paths)
        {
            if (!ExtraSampleFiles.Any(p => p.Equals(path, StringComparison.OrdinalIgnoreCase)))
            {
                ExtraSampleFiles.Add(path);
            }
        }
    }

    [RelayCommand]
    private void RemoveSample()
    {
        if (SelectedExtraSample is not null)
        {
            ExtraSampleFiles.Remove(SelectedExtraSample);
        }
    }

    [RelayCommand]
    private async Task AddSubtitleAsync()
    {
        IReadOnlyList<string> paths = await _fileDialog.OpenFilesAsync(
            "Select Subtitle .sfv (its .rar volumes must sit beside it)", FileDialogFilters.SubtitleSfv);

        foreach (string path in paths)
        {
            if (!ExtraSubtitleSfvFiles.Any(p => p.Equals(path, StringComparison.OrdinalIgnoreCase)))
            {
                ExtraSubtitleSfvFiles.Add(path);
            }
        }
    }

    [RelayCommand]
    private void RemoveSubtitle()
    {
        if (SelectedExtraSubtitle is not null)
        {
            ExtraSubtitleSfvFiles.Remove(SelectedExtraSubtitle);
        }
    }

    /// <summary>
    /// Builds placeholder stored-file entries for the release's samples (.srs) and subtitle archives
    /// (.srr) — auto-detected plus anything added on the samples step — so they appear in the Manage
    /// step and can be reordered. No files are generated here; the actual .srs/.srr are created at
    /// the end (from each placeholder's source) when the SRR is built. Called on leaving the samples
    /// step; the placeholders (and the user's ordering) are kept when the source set is unchanged.
    /// </summary>
    public void BuildSampleAndSubtitlePlaceholders()
    {
        if (string.IsNullOrWhiteSpace(InputPath))
        {
            return;
        }

        string releaseDir = Path.GetDirectoryName(InputPath) ?? ".";
        List<string> samples = ReleaseFileScanner.FindSampleFiles(releaseDir)
            .Concat(ExtraSampleFiles)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        List<string> subtitleSfvs = ReleaseFileScanner.FindSubtitleSFVFiles(releaseDir)
            .Concat(ExtraSubtitleSfvFiles)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        // Keep existing placeholders (and the user's ordering) when the source set hasn't changed.
        var existing = StoredFiles
            .Where(f => f.Kind != StoredFileKind.Regular)
            .Select(f => f.GenerateFromPath ?? string.Empty)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var wanted = samples.Concat(subtitleSfvs).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (existing.SetEquals(wanted))
        {
            return;
        }

        for (int i = StoredFiles.Count - 1; i >= 0; i--)
        {
            if (StoredFiles[i].Kind != StoredFileKind.Regular)
            {
                StoredFiles.RemoveAt(i);
            }
        }

        foreach (string sample in samples)
        {
            StoredFiles.Add(new StoredFileItem
            {
                StoredName = GeneratedStoredName(releaseDir, sample, ".srs", "Sample"),
                GenerateFromPath = sample,
                Kind = StoredFileKind.GeneratedSrs,
            });
        }

        foreach (string sfv in subtitleSfvs)
        {
            StoredFiles.Add(new StoredFileItem
            {
                StoredName = GeneratedStoredName(releaseDir, sfv, ".srr", "Subs"),
                GenerateFromPath = sfv,
                Kind = StoredFileKind.GeneratedNestedSrr,
            });
        }
    }

    /// <summary>
    /// Generates the actual .srs/.srr for every placeholder entry into <paramref name="tempDir"/>
    /// and returns a map from placeholder to its generated file path. Non-destructive: the
    /// placeholders are left untouched (so a retry after a failed/cancelled run — whose temp dir is
    /// deleted — regenerates cleanly rather than referencing a dead path). Placeholders that fail to
    /// generate are simply absent from the map. Called at creation time.
    /// </summary>
    private async Task<Dictionary<StoredFileItem, string>> MaterializePlaceholdersAsync(string tempDir, SRRCreationOptions options, CancellationToken ct)
    {
        var srsOptions = new SRSCreationOptions
        {
            AppName = string.IsNullOrWhiteSpace(AppName) ? "ReScene.NET" : AppName
        };

        var materialized = new Dictionary<StoredFileItem, string>();
        List<StoredFileItem> placeholders = StoredFiles.Where(f => f.Kind != StoredFileKind.Regular).ToList();

        await GenerateAndRecordAsync(
            placeholders,
            (item, index, token) => item.Kind switch
            {
                StoredFileKind.GeneratedSrs => GenerateSrsFileAsync(item.GenerateFromPath!, tempDir, index, srsOptions, token),
                StoredFileKind.GeneratedNestedSrr => GenerateNestedSrrFileAsync(item.GenerateFromPath!, tempDir, index, options, token),
                _ => Task.FromResult<string?>(null),
            },
            (item, generated) => materialized[item] = generated,
            ct);

        return materialized;
    }

    [RelayCommand]
    private void MoveStoredFileUp() => MoveSelectedStoredFile(-1);

    [RelayCommand]
    private void MoveStoredFileDown() => MoveSelectedStoredFile(+1);

    /// <summary>
    /// Moves the selected entry within the list. The list order is the order the files are
    /// written into the SRR.
    /// </summary>
    private void MoveSelectedStoredFile(int offset)
    {
        if (SelectedStoredFile is null)
        {
            return;
        }

        int index = StoredFiles.IndexOf(SelectedStoredFile);
        int target = index + offset;
        if (index < 0 || target < 0 || target >= StoredFiles.Count)
        {
            return;
        }

        StoredFiles.Move(index, target);
    }

    /// <summary>
    /// One-shot flag: when set, the next SRR creation skips its own overwrite prompt because the
    /// caller (e.g. the Beginner wizard) already confirmed it. Reset at the start of each run.
    /// </summary>
    public bool SuppressOverwriteConfirm { get; set; }

    private bool CanCreateSRR() => !IsCreating
        && !string.IsNullOrWhiteSpace(InputPath)
        && !string.IsNullOrWhiteSpace(OutputPath);

    [RelayCommand(CanExecute = nameof(CanCreateSRR))]
    private async Task CreateSRRAsync()
    {
        bool skipConfirm = SuppressOverwriteConfirm;
        SuppressOverwriteConfirm = false;
        if (File.Exists(OutputPath) && !skipConfirm)
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
        BuildSucceeded = false;
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

            // Phase 0: Materialize the wizard's sample/subtitle placeholders — generate their
            // actual .srs/.srr now, in the order the user arranged. (Advanced has no placeholders.)
            // Non-destructive: returns a map; placeholders stay placeholders so a retry regenerates.
            var materialized = new Dictionary<StoredFileItem, string>();
            if (StoredFiles.Any(f => f.Kind != StoredFileKind.Regular))
            {
                tempDir = _tempDir.CreateTempDirectory();
                materialized = await MaterializePlaceholdersAsync(tempDir, options, _cts.Token);
            }

            // Phase 1: Auto-create SRS files for samples (Advanced tab; the wizard uses placeholders
            // above instead, with AutoCreateSRS off).
            if (AutoCreateSRS)
            {
                await CreateSRSForSamplesAsync(releaseDir, tempDir ??= _tempDir.CreateTempDirectory(), _cts.Token);
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

            // Stored files are written in this list's order. A stored name can only appear once in
            // an SRR, so two files sharing a name can't both be written: keep the entry in its
            // original position but take the last source for it (so a freshly generated SRS wins
            // over an earlier auto-scanned copy), and warn rather than silently dropping a file.
            var storedFiles = new List<StoredFileEntry>();
            var positions = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (StoredFileItem item in StoredFiles)
            {
                // A placeholder's real path comes from this run's materialization map (it's not
                // written back onto the item, so a retry regenerates). Skip a placeholder whose
                // generation failed.
                string fullPath = item.Kind == StoredFileKind.Regular
                    ? item.FullPath
                    : materialized.GetValueOrDefault(item, string.Empty);
                if (string.IsNullOrEmpty(fullPath))
                {
                    continue;
                }

                // Normalize to the writer's key space (forward slashes) so a backslash typed into
                // the editable "Stored As" column can't slip past this collision check and then be
                // silently dropped by the writer.
                string storedName = item.StoredName.Replace('\\', '/');
                if (positions.TryGetValue(storedName, out int pos))
                {
                    if (!storedFiles[pos].FullPath.Equals(fullPath, StringComparison.OrdinalIgnoreCase))
                    {
                        Log($"WARNING: Two stored files use the name \"{storedName}\" — only one is included. Rename one to keep both.");
                    }

                    storedFiles[pos] = new StoredFileEntry(storedName, fullPath);
                }
                else
                {
                    positions[storedName] = storedFiles.Count;
                    storedFiles.Add(new StoredFileEntry(storedName, fullPath));
                }
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
                BuildSucceeded = true;
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
        Cancel();
        Log("Cancellation requested...");
    }

    [RelayCommand]
    private Task SaveLogAsync() => SaveLogToFileAsync(_fileDialog);

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

    // ── SRS auto-creation (Advanced tab: scan + generate at create time) ──

    private async Task CreateSRSForSamplesAsync(string releaseDir, string tempDir, CancellationToken ct)
    {
        // Auto-detected samples plus any added manually on the wizard's Samples step.
        List<string> samples = ReleaseFileScanner.FindSampleFiles(releaseDir)
            .Concat(ExtraSampleFiles)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var srsOptions = new SRSCreationOptions
        {
            AppName = string.IsNullOrWhiteSpace(AppName) ? "ReScene.NET" : AppName
        };

        await GenerateAndRecordAsync(
            samples,
            (sample, i, token) => GenerateSrsFileAsync(sample, tempDir, i, srsOptions, token),
            (sample, srsPath) => StoredFiles.Add(new StoredFileItem
            {
                FullPath = srsPath,
                StoredName = GeneratedStoredName(releaseDir, sample, ".srs", "Sample"),
            }),
            ct);
    }

    // ── Vobsub nested SRR (Advanced tab: scan + generate at create time) ──

    private async Task CreateVobsubSrrsAsync(string releaseDir, SRRCreationOptions options, string tempDir, CancellationToken ct)
    {
        List<string> subtitleSfvs = ReleaseFileScanner.FindSubtitleSFVFiles(releaseDir)
            .Concat(ExtraSubtitleSfvFiles)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        await GenerateAndRecordAsync(
            subtitleSfvs,
            (sfv, i, token) => GenerateNestedSrrFileAsync(sfv, tempDir, i, options, token),
            (sfv, srrPath) => StoredFiles.Add(new StoredFileItem
            {
                FullPath = srrPath,
                StoredName = GeneratedStoredName(releaseDir, sfv, ".srr", "Subs"),
            }),
            ct);
    }

    // ── Per-file generators (shared by Advanced create-time and wizard placeholder paths) ──

    /// <summary>
    /// Creates one .srs from <paramref name="samplePath"/> into <paramref name="tempDir"/> and
    /// returns its path, or null on failure. The index keeps temp filenames unique so two samples
    /// sharing a basename don't overwrite each other (the prefix never reaches the SRR).
    /// </summary>
    private async Task<string?> GenerateSrsFileAsync(string samplePath, string tempDir, int index, SRSCreationOptions srsOptions, CancellationToken ct)
    {
        string sampleName = Path.GetFileName(samplePath);
        string srsPath = Path.Combine(tempDir, $"{index}_{Path.ChangeExtension(sampleName, ".srs")}");
        Log($"Creating SRS for: {sampleName}");

        try
        {
            SRSCreationResult result = await _sRSService.CreateAsync(srsPath, samplePath, srsOptions, ct);
            if (result.Success)
            {
                Log($"  SRS created: {Path.GetFileName(srsPath)} ({result.SRSFileSize:N0} bytes)");
                return srsPath;
            }

            Log($"  SRS failed for {sampleName}: {result.ErrorMessage}");
        }
        catch (Exception ex)
        {
            Log($"  SRS error for {sampleName}: {ex.Message}");
        }

        return null;
    }

    /// <summary>
    /// Creates one nested .srr from the subtitle <paramref name="sfvPath"/> (and any .nfo beside it)
    /// into <paramref name="tempDir"/> and returns its path, or null on failure.
    /// </summary>
    private async Task<string?> GenerateNestedSrrFileAsync(string sfvPath, string tempDir, int index, SRRCreationOptions options, CancellationToken ct)
    {
        string sfvName = Path.GetFileName(sfvPath);
        string srrPath = Path.Combine(tempDir, $"{index}_{Path.ChangeExtension(sfvName, ".srr")}");
        Log($"Creating nested SRR for: {sfvName}");

        try
        {
            // Nested SRRs have no UI to curate stored files, so explicitly pass the NFOs in the
            // vobsub SFV's directory followed by the SFV itself, to be stored in that order.
            var nestedStoredFiles = new List<StoredFileEntry>();
            string sfvDir = Path.GetDirectoryName(sfvPath) ?? ".";
            foreach (string nfoFile in Directory.GetFiles(sfvDir, "*.nfo"))
            {
                nestedStoredFiles.Add(new StoredFileEntry(Path.GetFileName(nfoFile), nfoFile));
            }
            nestedStoredFiles.Add(new StoredFileEntry(sfvName, sfvPath));

            SRRCreationResult result = await _sRRService.CreateFromSFVAsync(srrPath, sfvPath, nestedStoredFiles, options, ct);
            if (result.Success)
            {
                Log($"  Nested SRR created: {Path.GetFileName(srrPath)} ({result.SRRFileSize:N0} bytes)");
                return srrPath;
            }

            Log($"  Nested SRR failed for {sfvName}: {result.ErrorMessage}");
        }
        catch (Exception ex)
        {
            Log($"  Nested SRR error for {sfvName}: {ex.Message}");
        }

        return null;
    }

    /// <summary>
    /// Shared "enumerate sources → generate file → name it → record it" loop behind the Advanced
    /// create-time scans and the wizard's placeholder materialization. <paramref name="generate"/>
    /// produces the file for each source (returning null on failure/skip); <paramref name="record"/>
    /// is the sink — the Advanced paths add a <see cref="StoredFileItem"/> to the bound
    /// <see cref="StoredFiles"/> collection, while the wizard path writes into its placeholder→path
    /// map. The per-source index keeps temp filenames unique.
    /// </summary>
    private static async Task GenerateAndRecordAsync<TSource>(
        IReadOnlyList<TSource> sources,
        Func<TSource, int, CancellationToken, Task<string?>> generate,
        Action<TSource, string> record,
        CancellationToken ct)
    {
        for (int i = 0; i < sources.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            string? generated = await generate(sources[i], i, ct);
            if (generated is not null)
            {
                record(sources[i], generated);
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
        _uiDispatcher.Post(() =>
        {
            ProgressPercent = e.ProgressPercent;
            ProgressMessage = e.Message;
            Log(e.Message);
        });
    }

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

        // GetRelativePath returns a rooted path when the file is on a different drive, and a
        // "..\"-prefixed path when it's outside the release folder — neither is a valid stored
        // name (the SRR should hold release-relative names), so fall back to the bare filename.
        if (Path.IsPathRooted(relative) || relative.StartsWith("..", StringComparison.Ordinal))
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

    /// <summary>
    /// Stored name for a generated .srs/.srr: the release-relative path (with the new extension)
    /// when the source lives under the release, otherwise the conventional
    /// <paramref name="conventionalDir"/>/&lt;name&gt;<paramref name="newExtension"/> — manually-added
    /// samples/subtitles from an unextracted release sit outside the release folder.
    /// </summary>
    private static string GeneratedStoredName(string releaseDir, string sourcePath, string newExtension, string conventionalDir)
    {
        string relative = Path.GetRelativePath(releaseDir, sourcePath);
        string name = Path.IsPathRooted(relative) || relative.StartsWith("..", StringComparison.Ordinal)
            ? $"{conventionalDir}/{Path.GetFileName(sourcePath)}"
            : relative.Replace('\\', '/');
        return Path.ChangeExtension(name, newExtension);
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

            // Old-style continuation volumes: .r00, .r01, … .r99, .s00, … .z99. Enumerate the
            // directory rather than walking the sequence, so a gap in the numbering doesn't
            // silently truncate the set (the loop here previously stopped at the first missing one).
            foreach (string file in Directory.GetFiles(dir, baseName + ".*"))
            {
                if (SceneFileTypes.IsOldStyleRarVolumeExtension(Path.GetExtension(file)))
                {
                    volumes.Add(file);
                }
            }
        }

        volumes.Sort(RARVolumeNameComparer.Instance);
        return volumes;
    }

    /// <summary>
    /// Represents a file to be stored inside the SRR, with its full path and relative stored name.
    /// Observable so a programmatic rename (the wizard's Rename button) refreshes the grid.
    /// </summary>
    public partial class StoredFileItem : ObservableObject
    {
        /// <summary>
        /// Gets or sets the absolute path to the file on disk. Empty for a not-yet-generated
        /// placeholder (see <see cref="Kind"/>) until it is materialized at creation time.
        /// </summary>
        [ObservableProperty]
        public partial string FullPath { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the relative name used when storing the file in the SRR.
        /// </summary>
        [ObservableProperty]
        public partial string StoredName { get; set; } = string.Empty;

        /// <summary>
        /// What this entry is. <see cref="StoredFileKind.Regular"/> is a real file at
        /// <see cref="FullPath"/>; the generated kinds are placeholders built from
        /// <see cref="GenerateFromPath"/> at creation time (so they can be listed and reordered
        /// on the Manage step before the actual files exist).
        /// </summary>
        public StoredFileKind Kind { get; set; } = StoredFileKind.Regular;

        /// <summary>For a placeholder, the source sample/SFV to generate the .srs/.srr from.</summary>
        public string? GenerateFromPath { get; set; }

        /// <summary>Path shown in the UI: the file on disk, or the pending source until generated.</summary>
        public string SourceDisplay => string.IsNullOrEmpty(FullPath) ? GenerateFromPath ?? string.Empty : FullPath;
    }

    /// <summary>What a <see cref="StoredFileItem"/> represents.</summary>
    public enum StoredFileKind
    {
        /// <summary>A real file already present at FullPath.</summary>
        Regular,

        /// <summary>A placeholder: an .srs to generate from a sample at creation time.</summary>
        GeneratedSrs,

        /// <summary>A placeholder: a nested .srr to generate from a subtitle .sfv at creation time.</summary>
        GeneratedNestedSrr,
    }
}
