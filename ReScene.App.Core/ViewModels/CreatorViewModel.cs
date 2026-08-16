using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ReScene.App.Core.Helpers;
using ReScene.App.Core.Models;
using ReScene.App.Core.Services;
using ReScene.App.Core.ViewModels.Creation;
using ReScene.SRR;
using ReScene.SRS;
namespace ReScene.App.Core.ViewModels;

/// <summary>
/// ViewModel for the SRR Creator tab, handling SRR file creation from SFV or RAR inputs.
/// </summary>
public partial class CreatorViewModel : OperationViewModelBase
{
    private readonly ISRRCreationService _sRRService;
    private readonly IFileDialogService _fileDialog;
    private readonly ITempDirectoryService _tempDir;
    private readonly IAppSettingsService _settingsService;
    private readonly IUiDispatcher _uiDispatcher;
    private readonly Func<string> _workDirFactory;

    public CreatorViewModel(ISRRCreationService srrService, ISRSCreationService srsService, IFileDialogService fileDialog, ITempDirectoryService tempDir, IAppSettingsService settingsService, IUiDispatcher uiDispatcher, IReleaseScanner releaseScanner, Func<string>? workDirFactory = null)
    {
        _sRRService = srrService;
        _fileDialog = fileDialog;
        _tempDir = tempDir;
        _settingsService = settingsService;
        _uiDispatcher = uiDispatcher;
        // A unique working directory per Create run, used ONLY by folder mode's generated-artifact
        // staging (CreatorArtifactStager.StageAsync) — separate from the pre-existing ITempDirectoryService
        // (_tempDir), which the file-mode/wizard placeholder path already owns. Injectable so a test
        // can observe/assert on the exact path.
        _workDirFactory = workDirFactory ?? (() => Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")));

        // Shares this view-model's own per-instance creation services, so the artifacts it
        // writes stream their progress into this instance's log and no other's.
        _artifacts = new ArtifactFileGenerator(srrService, srsService, Log);
        // The release root is a LIVE accessor: staging re-reads it at each phase point,
        // across awaits, while InputPath stays user-editable during a run.
        _folderScan = new FolderScanController(
            releaseScanner, uiDispatcher,
            DetectedSets, StoredFiles, ExtraSampleFiles, ExtraSubtitleSfvFiles,
            new FolderScanController.Hooks(
                SetIsScanning: v => IsScanning = v,
                SetInputStatus: v => InputStatus = v,
                SetOutputStatus: v => OutputStatus = v,
                TrySetAutoOutputPath: v => TrySetAutoOutputPath(v),
                NotifyCanExecuteChanged: RefreshCreateGate,
                NotifyFolderModeChanged: () => OnPropertyChanged(nameof(IsFolderMode)),
                ClearStoredFileSelection: () => SelectedStoredFile = null,
                ClearExtraSampleSelection: () => SelectedExtraSample = null,
                ClearExtraSubtitleSelection: () => SelectedExtraSubtitle = null,
                UpdateActionHint: UpdateActionHint,
                DetectedSetsSummary: () => DetectedSetsSummary,
                AppendLog: Log));

        _stager = new CreatorArtifactStager(srrService, srsService, _artifacts, () => _folderScan.ReleaseRoot!, Log);
        _fileMode = new FileModeCreationPipeline(
            srrService, _artifacts, StoredFiles, ExtraSampleFiles, ExtraSubtitleSfvFiles, Log);

        _sRRService.Progress += OnProgress;

        // HasDetectedSets/DetectedSetsSummary are derived from DetectedSets.Count; raise their change
        // notifications from a single CollectionChanged hook so every mutation site (the scan-apply
        // population loop, Reset, ExitFolderMode's clear) notifies without hand-editing each one.
        DetectedSets.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(HasDetectedSets));
            OnPropertyChanged(nameof(DetectedSetsSummary));
        };

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

    // ── Folder mode: a directory InputPath triggers a background release scan whose results
    // replace StoredFiles/ExtraSampleFiles/ExtraSubtitleSfvFiles and populate DetectedSets. The
    // whole lifecycle — the generation guard, the scan task, applying or discarding its result, and
    // tearing the mode down — belongs to FolderScanController; this view-model reads the mode flags
    // only through that controller's read-only properties.
    private readonly FolderScanController _folderScan;
    private readonly ArtifactFileGenerator _artifacts;
    private readonly CreatorArtifactStager _stager;
    private readonly FileModeCreationPipeline _fileMode;

    // OutputPath auto-vs-user tracking: _lastAutoOutputPath is whatever value
    // AutoSetFolderOutputPath last wrote, so OnOutputPathChanged can tell "this is the value we
    // just wrote" apart from "the user (or a Browse pick) changed it since".
    private bool _outputPathAutoGenerated;
    private string? _lastAutoOutputPath;

    /// <summary>The release's main RAR sets found by the most recent folder scan, in traversal order.</summary>
    public ObservableCollection<ReleaseSetInput> DetectedSets { get; } = [];

    /// <summary>
    /// Whether the Advanced tab is currently in folder mode (a directory <see cref="InputPath"/>
    /// drove a release scan). Bound by the view to DISABLE the "Store fix RAR" checkbox: in folder
    /// mode a fix release's RAR is always stored automatically (the scanner provides it in its
    /// <see cref="ReleaseScanResult.StoredFiles"/>), matching pyrescene, which has no fix-RAR flag —
    /// so the toggle is inert here. Change-notified manually by
    /// <see cref="FolderScanController"/> at every mode change (Reset / ExitFolderMode / Start).
    /// </summary>
    public bool IsFolderMode => _folderScan.IsFolderMode;

    /// <summary>
    /// Whether the most recent folder scan found any main RAR sets — the detected-sets list binds its
    /// <c>IsVisible</c> to this. A bool (not <c>DetectedSets.Count</c>): Avalonia has no implicit
    /// int→bool conversion, so binding visibility straight to <c>Count</c> would never resolve.
    /// Change-notified via the constructor's <c>DetectedSets</c>
    /// <see cref="System.Collections.Specialized.INotifyCollectionChanged.CollectionChanged"/> hook.
    /// </summary>
    public bool HasDetectedSets => DetectedSets.Count > 0;

    /// <summary>
    /// Grammatically-correct count of the detected RAR sets ("No RAR sets" / "1 RAR set" /
    /// "{n} RAR sets"), surfaced as the detected-sets list's automation Name so a screen reader
    /// reads a sensible label instead of "N items". Change-notified with
    /// <see cref="HasDetectedSets"/>.
    /// </summary>
    public string DetectedSetsSummary => DetectedSets.Count switch
    {
        0 => "No RAR sets",
        1 => "1 RAR set",
        int n => $"{n} RAR sets",
    };

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CreateSRRCommand))]
    public partial bool IsScanning { get; set; }

    /// <summary>
    /// The most recent folder-scan Task, exposed so tests can await scan completion
    /// deterministically (production is fire-and-forget and marshals results to the UI thread).
    /// </summary>
    internal Task? LastFolderScan => _folderScan.LastScan;

    [RelayCommand]
    private async Task BrowseInputFolderAsync()
    {
        string? path = await _fileDialog.OpenFolderAsync("Select Release Folder", InputPath);

        if (path is not null)
        {
            InputPath = path;
        }
    }

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
    public partial bool StoreFixRAR { get; set; } = true;

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

        // Folder mode: cancel any in-flight scan and clear the folder-only state up front so it
        // can't linger between wizard runs (its stale completion is also independently discarded
        // by the generation check below, but a reset shouldn't wait on that).
        _folderScan.Reset();
        _outputPathAutoGenerated = false;
        _lastAutoOutputPath = null;
        IsScanning = false;
        DetectedSets.Clear();

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
        StoreFixRAR = true;
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
            FileDialogFilters.SFVAndRAR, InputPath);

        if (path is not null)
        {
            // OnInputPathChanged's File.Exists(value) branch now handles the auto-fill uniformly
            // (typed or browsed), so there's no separate call needed here.
            InputPath = path;
        }
    }

    partial void OnInputPathChanged(string value)
    {
        // Every input change — file, blank, nonexistent, or folder — invalidates any in-flight
        // folder scan: bump the generation and cancel+dispose the CTS so a stale completion (even
        // one that finished the work before observing cancellation) is discarded by the generation
        // check in ApplyFolderScanResult, never overwriting newer state.
        _folderScan.InvalidateInFlight();

        if (Directory.Exists(value))
        {
            _folderScan.Start(value);
            return;
        }

        if (_folderScan.IsFolderMode)
        {
            _folderScan.ExitFolderMode();
        }

        if (!string.IsNullOrWhiteSpace(value))
        {
            IsSFVInput = Path.GetExtension(value).Equals(".sfv", StringComparison.OrdinalIgnoreCase);
        }

        // Mirrors folder mode's auto-fill-on-scan: switching TO a real file (by typing or Browse)
        // re-derives OutputPath when it's still eligible (blank, or itself a prior auto value from
        // either mode) — the same output-path auto-fill rule applies uniformly to both input kinds,
        // not just Browse.
        if (File.Exists(value))
        {
            AutoSetOutputPath(value);
        }

        UpdateStoredNames();
        _fileMode.AutoScanReleaseFiles(AutoIncludeFiles, InputPath);
        UpdateInputStatus(value);
        UpdateActionHint();
    }

    partial void OnOutputPathChanged(string value)
    {
        // A change that didn't come from AutoSetFolderOutputPath (which sets _lastAutoOutputPath to
        // the same value first) is a user edit — user edits are never auto-replaced again until the
        // next auto-fill.
        if (value != _lastAutoOutputPath)
        {
            _outputPathAutoGenerated = false;
        }

        if (string.IsNullOrWhiteSpace(value))
        {
            OutputStatus = FieldStatus.None;
        }

        UpdateActionHint();
    }

    partial void OnIsCreatingChanged(bool value) => UpdateActionHint();

    private void UpdateInputStatus(string value) => InputStatus = CreatorFieldGuidance.BuildInputStatus(value);

    private void UpdateActionHint() => ActionHint = CreatorFieldGuidance.BuildActionHint(IsCreating, InputPath, OutputPath);

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
            "Select Files to Store", FileDialogFilters.StoredFiles, InputPath);

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

    /// <summary>
    /// Shows the platform's synchronous "duplicate stored name" warning through the injected
    /// <see cref="IFileDialogService"/>. Kept on the ViewModel (rather than in view code-behind) so
    /// the message is framework-agnostic and rendered by whatever dialog the platform provides — the
    /// editable Stored Files grid calls this when an inline rename would collide with an existing
    /// stored name.
    /// </summary>
    public void WarnDuplicateStoredName(string attemptedName) =>
        _fileDialog.ShowWarning(
            "Duplicate stored name",
            $"A stored file is already named \"{attemptedName}\". The name was not changed.");

    // ── Sample / subtitle inputs (wizard "Samples & subtitles" step) ──

    [RelayCommand]
    private async Task AddSampleAsync()
    {
        IReadOnlyList<string> paths = await _fileDialog.OpenFilesAsync(
            "Select Sample File(s)", FileDialogFilters.MediaSamples, InputPath);

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
            "Select Subtitle .sfv (its .rar volumes must sit beside it)", FileDialogFilters.SubtitleSfv, InputPath);

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

        // Same trap as ComputeStoredName: GetDirectoryName returns "" (not null) for a bare file
        // name, and the scanners throw on an empty path — a bare input means the current directory.
        string releaseDir = Path.GetDirectoryName(InputPath) is { Length: > 0 } dir ? dir : ".";
        List<string> samples = [.. ReleaseFileScanner.FindSampleFiles(releaseDir)
            .Concat(ExtraSampleFiles)
            .Distinct(StringComparer.OrdinalIgnoreCase)];
        List<string> subtitleSfvs = [.. ReleaseFileScanner.FindSubtitleSFVFiles(releaseDir)
            .Concat(ExtraSubtitleSfvFiles)
            .Distinct(StringComparer.OrdinalIgnoreCase)];

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
                StoredName = CreatorArtifactNaming.GeneratedStoredName(releaseDir, sample, ".srs", "Sample"),
                GenerateFromPath = sample,
                Kind = StoredFileKind.GeneratedSRS,
            });
        }

        foreach (string sfv in subtitleSfvs)
        {
            StoredFiles.Add(new StoredFileItem
            {
                StoredName = CreatorArtifactNaming.GeneratedStoredName(releaseDir, sfv, ".srr", "Subs"),
                GenerateFromPath = sfv,
                Kind = StoredFileKind.GeneratedNestedSRR,
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
            AppName = string.IsNullOrWhiteSpace(AppName) ? "ReScene Manager" : AppName
        };

        var materialized = new Dictionary<StoredFileItem, string>();
        List<StoredFileItem> placeholders = [.. StoredFiles.Where(f => f.Kind != StoredFileKind.Regular)];

        await ArtifactFileGenerator.GenerateAndRecordAsync(
            placeholders,
            (item, index, token) => item.Kind switch
            {
                StoredFileKind.GeneratedSRS => _artifacts.GenerateSRSFileAsync(item.GenerateFromPath!, tempDir, index, srsOptions, token),
                StoredFileKind.GeneratedNestedSRR => _artifacts.GenerateNestedSRRFileAsync(item.GenerateFromPath!, tempDir, index, options, token),
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
        if (SelectedStoredFile is not { } item)
        {
            return;
        }

        int index = StoredFiles.IndexOf(item);
        int target = index + offset;
        if (index < 0 || target < 0 || target >= StoredFiles.Count)
        {
            return;
        }

        // NOT ObservableCollection.Move: Avalonia's DataGridCollectionView.ProcessCollectionChanged
        // handles Add/Remove/Replace/Reset but silently drops Move notifications, so a Move reorders
        // the list without the grid ever repainting — the buttons look dead. Remove+Insert raises
        // two events the view does handle; the removal clears the grid selection, so restore it.
        StoredFiles.RemoveAt(index);
        StoredFiles.Insert(target, item);
        SelectedStoredFile = item;
    }

    /// <summary>
    /// One-shot flag: when set, the next SRR creation skips its own overwrite prompt because the
    /// caller (e.g. the Beginner wizard) already confirmed it. Reset at the start of each run.
    /// </summary>
    public bool SuppressOverwriteConfirm { get; set; }

    private bool CanCreateSRR() => !IsCreating
        && !IsScanning
        && !_folderScan.IsMusicOnly
        && !_folderScan.IsInvalid
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

            SRRCreationResult result;

            if (_folderScan.IsFolderMode)
            {
                // Folder mode: stage generated artifacts (SRS/nested SRR/failure txt) into the
                // stored-file list before handing everything to the multi-input writer.
                Log($"Release root: {_folderScan.ReleaseRoot}");

                List<string> inputPaths = [.. DetectedSets.Select(s => s.SfvOrRarPath)];
                List<StoredFileEntry> additionalFiles = [.. StoredFiles.Select(f => new StoredFileEntry(f.StoredName, f.FullPath))];

                string? artifactWorkDir = null;
                try
                {
                    if (ExtraSampleFiles.Count > 0 || ExtraSubtitleSfvFiles.Count > 0)
                    {
                        artifactWorkDir = _workDirFactory();
                        Directory.CreateDirectory(artifactWorkDir);
                        // Samples are materialized now; the subtitle list and the vobsub toggle
                        // are accessors the stager invokes AFTER sample generation, exactly
                        // where this code read them before.
                        additionalFiles = await _stager.StageAsync(
                            additionalFiles, artifactWorkDir, options, AutoCreateSRS, AppName,
                            [.. ExtraSampleFiles], () => [.. ExtraSubtitleSfvFiles], () => CreateVobsubSRR,
                            _cts.Token);
                    }

                    result = await _sRRService.CreateFromInputsAsync(
                        OutputPath,
                        inputPaths,
                        _folderScan.ReleaseRoot,
                        storeRelativePaths: true,
                        additionalFiles.Count > 0 ? additionalFiles : null,
                        options,
                        _cts.Token);
                }
                finally
                {
                    // Best-effort cleanup — the writer's own atomic-move transaction is what keeps
                    // the destination safe, not this. Deliberately does NOT catch
                    // OperationCanceledException: a cancelled run still gets its temp dir cleaned up
                    // here, but the cancellation itself must keep propagating out of this method.
                    if (artifactWorkDir is not null)
                    {
                        try
                        {
                            Directory.Delete(artifactWorkDir, recursive: true);
                        }
                        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                        {
                        }
                    }
                }
            }
            else
            {
                // File mode: every phase - placeholder materialization, create-time SRS and vobsub
                // generation, fix-RAR storage, and the writer call itself - lives in the pipeline.
                // StoredFiles goes in BY REFERENCE and is still appended to incrementally as each
                // phase generates, so the collection is already growing while IsCreating is true.
                // The options go in as LIVE accessors: each is read at its own phase boundary, and
                // none of the controls is disabled during a run.
                result = await _fileMode.RunAsync(
                    new FileModeCreationPipeline.Inputs(
                        () => InputPath, () => OutputPath, () => IsSFVInput, () => AutoCreateSRS,
                        () => CreateVobsubSRR, () => StoreFixRAR, () => AppName, options),
                    // Lazily creates the run's temp directory at most once. The variable stays here
                    // because the `finally` below owns its cleanup.
                    () => tempDir ??= _tempDir.CreateTempDirectory(),
                    MaterializePlaceholdersAsync,
                    _cts.Token);
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
        catch (OperationCanceledException)
        {
            // Distinguish a clean cancellation from a real error — the blanket `catch (Exception)`
            // below would otherwise swallow this too, showing "Error." for what the user explicitly
            // requested via CancelCreation().
            //
            // The OCE is intentionally NOT rethrown — the command's own Task completes normally
            // (RanToCompletion, not Canceled), with the cancellation instead made observable through
            // this VM's own state (ProgressMessage, BuildSucceeded staying false, the "Cancelled" log
            // entry) and IsCreating resetting to false in `finally` below so the command becomes
            // runnable again. This matches every OTHER cancellable command in the codebase —
            // ReconstructorViewModel's ExecuteReconstructionAsync and FileCompareViewModel's diff
            // both swallow their own OCE the same way — so rethrowing here would be the outlier, not
            // the fix.
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

    // ── Folder mode (release scan) ────────────────────────────

    /// <summary>
    /// Re-evaluates the Create button's enabled state. A named method rather than the
    /// <c>CreateSRRCommand.NotifyCanExecuteChanged</c> method group: binding that group directly
    /// would evaluate the generated <c>CreateSRRCommand</c> property while building the hook record
    /// in the constructor, forcing the command into existence before <c>_folderScan</c> is assigned.
    /// A lambda would say the same thing, but IDE0200 flags it as removable.
    /// </summary>
    private void RefreshCreateGate() => CreateSRRCommand.NotifyCanExecuteChanged();

    /// <summary>
    /// Assigns <paramref name="autoValue"/> to <see cref="OutputPath"/> and records it as
    /// auto-generated, PROVIDED the current value is still eligible for auto-replacement — blank,
    /// or itself a still-current auto value, never a user-typed or user-picked one. Shared by both
    /// input kinds so switching between them consistently replaces an auto value and preserves a
    /// genuine user edit either way. Returns whether it applied the auto value (vs. leaving a
    /// user-owned one untouched), for callers that only want to surface UI feedback (e.g. an Info
    /// status) when an auto-fill actually happened.
    /// </summary>
    private bool TrySetAutoOutputPath(string autoValue)
    {
        if (!_outputPathAutoGenerated && !string.IsNullOrWhiteSpace(OutputPath))
        {
            return false;
        }

        // Set before the assignment below: OnOutputPathChanged compares the incoming value against
        // _lastAutoOutputPath to tell "we just wrote this" apart from a user edit, so it must
        // already hold the new value by the time the property-changed hook runs.
        _lastAutoOutputPath = autoValue;
        _outputPathAutoGenerated = true;
        OutputPath = autoValue;
        return true;
    }

    // ── SRS auto-creation (Advanced tab: scan + generate at create time) ──

    // ── Vobsub nested SRR (Advanced tab: scan + generate at create time) ──

    // ── Per-file generators (shared by Advanced create-time and wizard placeholder paths) ──

    // ── Fix release detection ───────────────────────────────

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
        if (TrySetAutoOutputPath(FieldGuidance.SuggestSiblingPath(inputPath, ".srr")))
        {
            OutputStatus = FieldStatus.Info("Auto-filled next to the input. Change it if you want the SRR elsewhere.");
        }
    }

    private string ComputeStoredName(string fullPath)
    {
        if (string.IsNullOrWhiteSpace(InputPath))
        {
            return Path.GetFileName(fullPath);
        }

        // GetDirectoryName yields null for a root path but "" for a bare file name (which the
        // Input text box accepts); both mean "no release folder", and an empty relativeTo would
        // throw out of AddStoredFiles.
        string releaseDir = Path.GetDirectoryName(InputPath) is { Length: > 0 } dir ? dir : ".";
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
            // Only recompute names for real files. Wizard sample/subtitle placeholders have an empty
            // FullPath (materialized at creation time); ComputeStoredName would call
            // Path.GetRelativePath(releaseDir, "") which throws ArgumentException and aborts the rest
            // of OnInputPathChanged. Their names are derived separately via GeneratedStoredName.
            if (item.Kind == StoredFileKind.Regular)
            {
                item.StoredName = ComputeStoredName(item.FullPath);
            }
        }
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
        GeneratedSRS,

        /// <summary>A placeholder: a nested .srr to generate from a subtitle .sfv at creation time.</summary>
        GeneratedNestedSRR,
    }
}
