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
    private readonly ISRSCreationService _sRSService;
    private readonly IFileDialogService _fileDialog;
    private readonly ITempDirectoryService _tempDir;
    private readonly IAppSettingsService _settingsService;
    private readonly IUiDispatcher _uiDispatcher;
    private readonly IReleaseScanner _releaseScanner;
    private readonly Func<string> _workDirFactory;

    public CreatorViewModel(ISRRCreationService srrService, ISRSCreationService srsService, IFileDialogService fileDialog, ITempDirectoryService tempDir, IAppSettingsService settingsService, IUiDispatcher uiDispatcher, IReleaseScanner releaseScanner, Func<string>? workDirFactory = null)
    {
        _sRRService = srrService;
        _sRSService = srsService;
        _fileDialog = fileDialog;
        _tempDir = tempDir;
        _settingsService = settingsService;
        _uiDispatcher = uiDispatcher;
        _releaseScanner = releaseScanner;
        // A unique working directory per Create run, used ONLY by folder mode's generated-artifact
        // staging (StageFolderArtifactsAsync) — separate from the pre-existing ITempDirectoryService
        // (_tempDir), which the file-mode/wizard placeholder path already owns. Injectable so a test
        // can observe/assert on the exact path.
        _workDirFactory = workDirFactory ?? (() => Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")));

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

    // ── Folder mode: a directory InputPath triggers a background release scan whose
    // results replace StoredFiles/ExtraSampleFiles/ExtraSubtitleSfvFiles and populate DetectedSets.
    // Mirrors InspectorViewModel's generation-guard house pattern: every InputPath change bumps
    // _scanGeneration and cancels _scanCts, so a scan whose generation is no longer current is
    // discarded on the UI thread even if it had already finished before the cancellation was seen.
    private int _scanGeneration;
    private CancellationTokenSource? _scanCts;
    private bool _isFolderMode;
    private bool _isMusicOnlyFolder;

    // Set when the current folder-mode state has nothing creatable: the input itself is invalid
    // (a filesystem root) or the scanner couldn't enumerate the root at all (ReleaseScanResult
    // "RootError" — e.g. permission denied). Gates CanCreateSRR so an empty/header-only SRR can't
    // be built from a scan that never actually looked at the release.
    private bool _folderScanInvalid;
    private string? _releaseRoot;

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
    /// so the toggle is inert here. Change-notified manually at every <see cref="_isFolderMode"/>
    /// assignment (Reset / ExitFolderMode / StartFolderScan).
    /// </summary>
    public bool IsFolderMode => _isFolderMode;

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
    internal Task? LastFolderScan { get; private set; }

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
        _scanGeneration++;
        CancelInFlightScan();
        _isFolderMode = false;
        OnPropertyChanged(nameof(IsFolderMode));
        _isMusicOnlyFolder = false;
        _folderScanInvalid = false;
        _releaseRoot = null;
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
        _scanGeneration++;
        CancelInFlightScan();

        if (Directory.Exists(value))
        {
            StartFolderScan(value);
            return;
        }

        if (_isFolderMode)
        {
            ExitFolderMode();
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
        AutoScanReleaseFiles();
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

        await GenerateAndRecordAsync(
            placeholders,
            (item, index, token) => item.Kind switch
            {
                StoredFileKind.GeneratedSRS => GenerateSRSFileAsync(item.GenerateFromPath!, tempDir, index, srsOptions, token),
                StoredFileKind.GeneratedNestedSRR => GenerateNestedSRRFileAsync(item.GenerateFromPath!, tempDir, index, options, token),
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
        && !_isMusicOnlyFolder
        && !_folderScanInvalid
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

            if (_isFolderMode)
            {
                // Folder mode: stage generated artifacts (SRS/nested SRR/failure txt) into the
                // stored-file list before handing everything to the multi-input writer.
                Log($"Release root: {_releaseRoot}");

                List<string> inputPaths = [.. DetectedSets.Select(s => s.SfvOrRarPath)];
                List<StoredFileEntry> additionalFiles = [.. StoredFiles.Select(f => new StoredFileEntry(f.StoredName, f.FullPath))];

                string? artifactWorkDir = null;
                try
                {
                    if (ExtraSampleFiles.Count > 0 || ExtraSubtitleSfvFiles.Count > 0)
                    {
                        artifactWorkDir = _workDirFactory();
                        Directory.CreateDirectory(artifactWorkDir);
                        additionalFiles = await StageFolderArtifactsAsync(additionalFiles, artifactWorkDir, options, _cts.Token);
                    }

                    result = await _sRRService.CreateFromInputsAsync(
                        OutputPath,
                        inputPaths,
                        _releaseRoot,
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
                // GetDirectoryName returns "" (not null) for a bare file name — same guard as
                // ComputeStoredName and BuildSampleAndSubtitlePlaceholders.
                string releaseDir = Path.GetDirectoryName(InputPath) is { Length: > 0 } dir ? dir : ".";

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
                    await CreateVobsubSRRsAsync(releaseDir, options, tempDir ??= _tempDir.CreateTempDirectory(), _cts.Token);
                }

                // Phase 3: Store fix RAR if applicable
                if (StoreFixRAR)
                {
                    StoreFixRARFile(releaseDir);
                }

                // Phase 4: Create the main SRR

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
                    List<string> volumes = CreatorArtifactNaming.DiscoverRARVolumes(InputPath);
                    Log($"Found {volumes.Count} volume(s).");

                    result = await _sRRService.CreateFromRARAsync(
                        OutputPath, volumes,
                        storedFiles.Count > 0 ? storedFiles : null,
                        options, _cts.Token);
                }
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

    // ── Folder mode (release scan) ────────────────────────────

    /// <summary>
    /// Leaves folder mode when InputPath changes to a file/blank/nonexistent path: resets the
    /// folder-only state so a stale detected-set list or a music-only gate can't linger into file
    /// mode. The in-flight scan (if any) was already cancelled by <see cref="OnInputPathChanged(string)"/>;
    /// since its completion will be discarded by the generation check in
    /// <see cref="ApplyFolderScanResult"/>, <see cref="IsScanning"/> must be cleared here
    /// synchronously — nothing else will do it.
    /// </summary>
    private void ExitFolderMode()
    {
        _isFolderMode = false;
        OnPropertyChanged(nameof(IsFolderMode));
        _isMusicOnlyFolder = false;
        _folderScanInvalid = false;
        _releaseRoot = null;
        IsScanning = false;
        ClearFolderScanResults();
    }

    /// <summary>
    /// Cancels and disposes any in-flight folder scan's CTS synchronously, right here on the
    /// calling thread, and clears the field before returning. Called only from UI-thread-invoked
    /// code (a property-changed hook or <see cref="Reset"/>) — paired with
    /// <see cref="RunFolderScanAsync"/>'s own cleanup, which ONLY ever touches a CTS from inside its
    /// <see cref="_uiDispatcher"/>.Post callback and never if this method already claimed it. Together
    /// these ensure Cancel()/Dispose() on one CancellationTokenSource instance never run
    /// concurrently (forbidden — throws ObjectDisposedException) and a live newer CTS can never be
    /// null'd out by an older scan's cleanup.
    /// </summary>
    private void CancelInFlightScan()
    {
        if (_scanCts is not { } cts)
        {
            return;
        }

        _scanCts = null;
        try
        {
            cts.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Already disposed by an earlier call — nothing left to do (defensive; the ownership
            // rule above should make this unreachable in practice).
        }
        finally
        {
            cts.Dispose();
        }
    }

    private void ClearFolderScanResults()
    {
        DetectedSets.Clear();
        StoredFiles.Clear();
        SelectedStoredFile = null;
        ExtraSampleFiles.Clear();
        SelectedExtraSample = null;
        ExtraSubtitleSfvFiles.Clear();
        SelectedExtraSubtitle = null;
    }

    /// <summary>
    /// Kicks off a background release scan of <paramref name="releaseRoot"/>. A filesystem root
    /// (e.g. "C:\") is rejected synchronously without scanning — the scanner walks the tree
    /// recursively, so scanning an entire drive would be both meaningless (no name to derive an SRR
    /// filename from) and dangerously slow.
    /// </summary>
    private void StartFolderScan(string releaseRoot)
    {
        _isFolderMode = true;
        OnPropertyChanged(nameof(IsFolderMode));
        _releaseRoot = releaseRoot;

        if (CreatorArtifactNaming.IsFilesystemRoot(releaseRoot))
        {
            // Never scanned — Create must not be able to build an empty/header-only SRR from an
            // input that was rejected outright. Reset music-only too: a prior scan's gate must not
            // linger once the input itself becomes invalid.
            IsScanning = false;
            _isMusicOnlyFolder = false;
            _folderScanInvalid = true;
            ClearFolderScanResults();
            InputStatus = FieldStatus.Error("This is a drive root, not a release folder — choose the folder containing the release's files.");
            OutputStatus = FieldStatus.Error("Choose a release folder, not a drive root — there's no name to base the SRR on.");
            UpdateActionHint();
            CreateSRRCommand.NotifyCanExecuteChanged();
            return;
        }

        IsScanning = true;

        // Busy announcement: reuse the existing InputStatus + its FieldStatusLine live region for a
        // single announced busy→result transition. ApplyFolderScanResult (or the root-error paths
        // above) overwrites this with the Ok(summary)/Error(...) result on completion — no second
        // status line, so a screen reader isn't double-announced.
        InputStatus = FieldStatus.Info("Scanning release folder…");

        // Captured now (after OnInputPathChanged already bumped it for this input change) so the
        // posted continuation below can tell whether it's still the current input.
        int generation = _scanGeneration;
        var cts = new CancellationTokenSource();

        // Captured as a value ONCE, right here, while `cts` is certainly not yet disposed — the
        // background delegate below reads `token`, never `cts.Token` again. `CancellationTokenSource
        // .Token`'s GETTER throws ObjectDisposedException once the source is disposed, but a
        // CancellationToken struct already obtained beforehand stays safe to poll
        // (IsCancellationRequested/ThrowIfCancellationRequested) even after that. Re-reading
        // `cts.Token` lazily inside the Task.Run delegate — evaluated whenever the thread pool
        // actually gets to it — could otherwise race a later CancelInFlightScan() disposing this
        // exact `cts` first (see the RapidInputSwitching_WithoutAwaiting_NeverThrows test).
        CancellationToken token = cts.Token;
        _scanCts = cts;
        LastFolderScan = RunFolderScanAsync(releaseRoot, generation, cts, token);
    }

    private async Task RunFolderScanAsync(string releaseRoot, int generation, CancellationTokenSource cts, CancellationToken token)
    {
        try
        {
            ReleaseScanResult result = await Task.Run(
                () => _releaseScanner.Scan(releaseRoot, token), token).ConfigureAwait(false);

            _uiDispatcher.Post(() =>
            {
                // Every _scanCts read/write below happens inside this Post callback — which, like
                // every other UI-thread-invoked entry point (a property-changed hook, Reset), is
                // serialized onto the UI thread — never on the background thread that ran the
                // scan. That's what keeps this identity check race-free against CancelInFlightScan:
                // the two either run one-at-a-time on the same thread, or (in a real app) are
                // serialized by the dispatcher itself.
                //
                // generation/_scanCts are kept in lockstep by CancelInFlightScan (both change
                // together, synchronously, whenever a scan is superseded), so checking either alone
                // would do; checking both is belt-and-suspenders. If superseded, the newer input
                // change already cancelled, disposed, and null'd out `cts` itself — there is nothing
                // left for us to clean up, and touching it again here would double-dispose (harmless)
                // or resurrect a reference someone else already tore down (not harmless with a naive
                // reordering) — so this is a hard bail, not just a "don't apply the result" check.
                if (generation != _scanGeneration || !ReferenceEquals(_scanCts, cts))
                {
                    return;
                }

                _scanCts = null;
                cts.Dispose();
                ApplyFolderScanResult(releaseRoot, result);
            });
        }
        catch (OperationCanceledException)
        {
            // Superseded — the newer input change already cancelled, disposed, and null'd out `cts`
            // itself (see CancelInFlightScan); there's nothing left for us to do here.
        }
        catch (Exception ex)
        {
            // The scan faulted with an UNEXPECTED (non-OCE) exception — the scan and
            // RarProofInspector.Inspect both catch only IOException/UnauthorizedAccessException, so
            // an ArgumentException/NotSupportedException/SecurityException from a FileStream or a
            // RAR-parser fault escapes here. Without this catch the background Task faults, the
            // success Post never runs, and IsScanning + InputStatus stay stranded on the busy
            // "Scanning release folder…" state (Create disabled, the live region stuck announcing
            // busy) until the user re-inputs. Post the SAME generation/_scanCts-gated UI-thread
            // continuation the success completion uses — every _scanCts read/write stays on the UI
            // thread, preserving the CTS-lifecycle invariants — then fail closed EXACTLY like
            // ApplyFolderScanResult's root-enumeration (IsRootError) branch: clear IsScanning, gate
            // Create, and surface the failure, so a faulted scan can never leave an empty/header-only
            // SRR buildable. Cancellation stays silent (handled above).
            _uiDispatcher.Post(() =>
            {
                if (generation != _scanGeneration || !ReferenceEquals(_scanCts, cts))
                {
                    return;
                }

                _scanCts = null;
                cts.Dispose();

                IsScanning = false;
                _isMusicOnlyFolder = false;
                _folderScanInvalid = true;
                ClearFolderScanResults();
                InputStatus = FieldStatus.Error($"Could not scan the folder: {ex.Message}");
                CreateSRRCommand.NotifyCanExecuteChanged();
                UpdateActionHint();
            });
        }
    }

    /// <summary>
    /// Applies a completed, still-current folder scan: populates <see cref="DetectedSets"/>,
    /// <see cref="StoredFiles"/> (StoredName = root-relative path), <see cref="ExtraSampleFiles"/>,
    /// and <see cref="ExtraSubtitleSfvFiles"/>; sets the input status summary (or a music-only
    /// error, which also gates <see cref="CreateSRRCommand"/>); logs every warning in order (the
    /// status line shows only a count/preview); and auto-fills <see cref="OutputPath"/> when it is
    /// still blank or auto-generated.
    /// </summary>
    private void ApplyFolderScanResult(string releaseRoot, ReleaseScanResult result)
    {
        IsScanning = false;
        _releaseRoot = releaseRoot;

        if (CreatorArtifactNaming.IsRootError(result))
        {
            // The scanner couldn't enumerate the root at all (e.g. permission denied) — surface the
            // failure and gate Create, rather than the previous fail-open behavior of treating the
            // resulting empty collections as an ordinary (successful, Ok-status) empty scan, which
            // let Create build an empty/header-only SRR from a root that was never actually read.
            _isMusicOnlyFolder = false;
            _folderScanInvalid = true;
            ClearFolderScanResults();
            InputStatus = FieldStatus.Error(result.Warnings[0]);
            CreateSRRCommand.NotifyCanExecuteChanged();
            UpdateActionHint();
            return;
        }

        // A successful scan clears any earlier invalid/error state — Create is re-enabled once the
        // input points at something the scanner could actually read.
        _folderScanInvalid = false;

        DetectedSets.Clear();
        foreach (ReleaseSetInput set in result.MainSets)
        {
            DetectedSets.Add(set);
        }

        StoredFiles.Clear();
        foreach (string path in result.StoredFiles)
        {
            StoredFiles.Add(new StoredFileItem
            {
                FullPath = path,
                StoredName = CreatorArtifactNaming.RootRelativeName(releaseRoot, path),
            });
        }

        ExtraSampleFiles.Clear();
        foreach (string sample in result.SampleFiles)
        {
            ExtraSampleFiles.Add(sample);
        }

        ExtraSubtitleSfvFiles.Clear();
        foreach (string sfv in result.SubtitleSfvs)
        {
            ExtraSubtitleSfvFiles.Add(sfv);
        }

        // [DIVERGENCE: Spec 2] the scanner routes rescue-fallback music SFVs to MusicSfvs instead of
        // admitting them as ordinary main sets; a folder holding only music has no supported output
        // yet, so Create is gated off with an explanatory error rather than silently building an
        // empty (or wrong) SRR.
        _isMusicOnlyFolder = result.MusicSfvs.Count > 0 && result.MainSets.Count == 0;

        if (_isMusicOnlyFolder)
        {
            InputStatus = FieldStatus.Error("Music release — folder scan support arrives in a later update.");
        }
        else
        {
            // Reuse DetectedSetsSummary for the set-count segment so the status line's grammar
            // ("No RAR sets"/"1 RAR set"/"{n} RAR sets") matches the detected-sets list's own
            // automation Name — DetectedSets was populated from result.MainSets just above, so the
            // two counts are identical here. (The sample/stored-file "(s)" segments are left as-is.)
            string summary = $"{DetectedSetsSummary} · {result.SampleFiles.Count} sample(s) · {result.StoredFiles.Count} stored file(s)";
            if (result.Warnings.Count > 0)
            {
                summary += $" · {result.Warnings.Count} warning(s): {result.Warnings[0]}";
            }

            InputStatus = FieldStatus.Ok(summary);
        }

        foreach (string warning in result.Warnings)
        {
            Log($"WARNING: {warning}");
        }

        AutoSetFolderOutputPath(releaseRoot);
        CreateSRRCommand.NotifyCanExecuteChanged();
        UpdateActionHint();
    }

    /// <summary>
    /// Auto-fills <see cref="OutputPath"/> from the release root when it is still blank or holds a
    /// previously auto-generated value — never a user-typed or user-picked one.
    /// <paramref name="releaseRoot"/> is never a filesystem root here: <see cref="StartFolderScan"/>
    /// rejects that case before a scan ever runs.
    /// </summary>
    private void AutoSetFolderOutputPath(string releaseRoot)
    {
        string trimmedRoot = Path.TrimEndingDirectorySeparator(releaseRoot);
        string? parent = Path.GetDirectoryName(trimmedRoot);
        string rootName = Path.GetFileName(trimmedRoot);
        if (string.IsNullOrEmpty(parent) || string.IsNullOrEmpty(rootName))
        {
            return;
        }

        TrySetAutoOutputPath(Path.Combine(parent, rootName + ".srr"));
    }

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

    // ── Generated artifacts (folder mode) ──

    /// <summary>
    /// Generates folder mode's samples/subtitles artifacts (an .srs, its failure .txt, and a
    /// RAR-backed .vob's nested .srr; a subtitle SFV's nested .srr) and splices them into
    /// <paramref name="baseline"/> (the current <see cref="StoredFiles"/> snapshot) at the
    /// excerpt's category positions, then re-applies the pass-10 proof-before-sfv reorder over the
    /// complete, merged list. No-op (returns <paramref name="baseline"/> unchanged) when there is
    /// nothing to generate. Samples are generated and spliced in BEFORE subtitles (matching the
    /// excerpt's own pass order — samples are pass 6, subtitles pass 9) so
    /// <see cref="GenerateSubtitleArtifactsAsync"/>'s already-stored-RAR check sees the
    /// fully-current stored list, not just the pre-sample baseline.
    /// </summary>
    private async Task<List<StoredFileEntry>> StageFolderArtifactsAsync(
        List<StoredFileEntry> baseline, string workDir, SRRCreationOptions options, CancellationToken ct)
    {
        // Folder mode honors AutoCreateSRS just as file mode does (see the file-mode phase, this
        // file's AutoCreateSRS gate) for pyrescene --no-srs parity. When off, generate no sample SRS
        // artifacts — a sample's ONLY stored output is its .srs (the sample MEDIA itself is never
        // stored), so an empty list simply stores nothing.
        List<StoredFileEntry> samples = AutoCreateSRS
            ? await GenerateSampleArtifactsAsync(workDir, options, ct)
            : [];

        // Generated `.srs` SUPERSEDES a same-relative-path pre-existing `.srs` in the stored list:
        // drop the baseline entry at any logical name a freshly-generated SRS also produced — no
        // collision error, the generated one simply replaces it.
        var supersededNames = new HashSet<string>(
            samples.Where(e => e.StoredName.EndsWith(".srs", StringComparison.OrdinalIgnoreCase)).Select(e => e.StoredName),
            StringComparer.OrdinalIgnoreCase);
        List<StoredFileEntry> kept = [.. baseline.Where(e => !supersededNames.Contains(e.StoredName))];

        // Splice positions are derived from the CURRENT (possibly user-edited) StoredFiles
        // snapshot rather than re-derived from the raw scan categories, so a manual edit between
        // scan and Create is respected — an accepted approximation: the byte-identity guarantee
        // only covers an unedited scan's output, which is what these two finders locate exactly
        // (see their own remarks for how).
        kept.InsertRange(CreatorArtifactNaming.FindSampleArtifactSpliceIndex(kept), samples);

        // For pyrescene --vobsub-srr parity: CreateVobsubSRR gates ONLY the nested-SRR generation
        // (pass 9) inside GenerateSubtitleArtifactsAsync — the subtitle-SFV storage (pass 10) always
        // runs, matching pyrescene, which without --vobsub-srr still stores extra_sfvs and only
        // skips create_srr_for_subs.
        List<StoredFileEntry> subtitles = await GenerateSubtitleArtifactsAsync(kept, workDir, options, CreateVobsubSRR, ct);
        kept.InsertRange(CreatorArtifactNaming.FindSubtitleArtifactSpliceIndex(kept), subtitles);

        return ReleaseScanner.ApplyProofBeforeSfvReorder(kept, static e => e.StoredName);
    }

    /// <summary>
    /// Creates one .srs per <see cref="ExtraSampleFiles"/> entry (+its failure .txt when creation
    /// fails and the SAMPLE FILE is non-empty; +a nested .srr when the sample is a RAR-backed
    /// .vob). Collision keying matches the excerpt's <c>same_srs_name</c> exactly: by FULL RELATIVE
    /// STEM (directory included) — only a stem shared by more than one sample keeps the full source
    /// extension in its SRS name.
    /// </summary>
    private async Task<List<StoredFileEntry>> GenerateSampleArtifactsAsync(string workDir, SRRCreationOptions options, CancellationToken ct)
    {
        List<string> samples = [.. ExtraSampleFiles];
        if (samples.Count == 0)
        {
            return [];
        }

        // FolderRelativeStem/FolderRelativeName fall back to "Sample/<basename>" for a source
        // OUTSIDE the release root — ExtraSampleFiles is shared with the file-mode Advanced tab's
        // "Add Sample" command, so a folder-mode run can still see a manually-added, out-of-root
        // sample; the raw root-relative path would keep an invalid "../" the writer's
        // CanonicalizeRelative rejects.
        List<string> stems = [.. samples.Select(s => CreatorArtifactNaming.FolderRelativeStem(_releaseRoot!, s, "Sample"))];
        var collisionStems = stems
            .GroupBy(s => s, StringComparer.Ordinal)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToHashSet(StringComparer.Ordinal);

        var srsOptions = new SRSCreationOptions
        {
            AppName = string.IsNullOrWhiteSpace(AppName) ? "ReScene Manager" : AppName
        };

        var result = new List<StoredFileEntry>();
        for (int i = 0; i < samples.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            string sample = samples[i];
            string relPath = CreatorArtifactNaming.FolderRelativeName(_releaseRoot!, sample, "Sample");
            string srsLogicalName = (collisionStems.Contains(stems[i]) ? relPath : stems[i]) + ".srs";
            string physicalSrsPath = Path.Combine(workDir, $"{i}_{Path.GetFileName(sample)}.srs");

            SRSCreationResult srsResult = await _sRSService.CreateAsync(physicalSrsPath, sample, srsOptions, ct);
            if (!srsResult.Success)
            {
                Log($"SRS failed for {Path.GetFileName(sample)}: {srsResult.ErrorMessage}");

                // The excerpt gates the failure .txt on the SAMPLE FILE's own size —
                // `sample_size = os.path.getsize(sample); if sample_size > 0: keep_txt = True` —
                // unconditionally on the error, NOT on the error text's length (the old gate here).
                // A genuinely 0-byte sample is the only case that differs: pyrescene suppresses the
                // .txt for it regardless of what stderr captured.
                long sampleSize;
                try
                {
                    sampleSize = new FileInfo(sample).Length;
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    sampleSize = 0;
                }

                if (sampleSize > 0)
                {
                    string physicalTxtPath = Path.Combine(workDir, $"{i}_{Path.GetFileName(sample)}.txt");
                    await File.WriteAllTextAsync(physicalTxtPath, srsResult.ErrorMessage ?? string.Empty, ct);
                    result.Add(new StoredFileEntry(relPath + ".txt", physicalTxtPath));
                }

                continue;
            }

            Log($"Created SRS: {srsLogicalName}");
            result.Add(new StoredFileEntry(srsLogicalName, physicalSrsPath));

            // Per the excerpt, a RAR-backed .vob sample keeps its SRS AND gets a nested SRR (both,
            // not a replacement). Only attempted once the SRS itself exists (a [DIVERGENCE:
            // hardening] over the excerpt, which would try to load an SRS that was never written
            // when creation failed for a .vob — see IsRarBackedVobSample's remarks).
            if (!CreatorArtifactNaming.IsRarBackedVobSample(sample))
            {
                continue;
            }

            string vobSrrLogicalName = srsLogicalName[..^".srs".Length] + ".srr";
            string physicalVobSrrPath = Path.Combine(workDir, $"{i}_{Path.GetFileName(sample)}.vob.srr");

            // pyrescene's create_srr_single_volume (main.py) — the routine that writes this
            // .vob/single-volume nested SRR — emits ONLY a header block, a RarFile block, and the
            // raw RAR block bytes; it has NO oso_hash parameter or logic at all, so its output NEVER
            // contains OSO blocks. Force our own dedicated nestedOptions (oso off, compressed
            // allowed) IDENTICAL to the two sibling nested-SRR paths (GenerateNestedSubtitleSrrsAsync
            // / GenerateNestedSRRFileAsync) instead of forwarding the OUTER `options`, so a user
            // enabling ComputeOSOHashes for the outer SRR cannot leak OSO blocks pyrescene omits into
            // this nested one (a byte divergence the oso-off golden can't catch). This also corrects
            // the secondary AllowCompressed forwarding.
            var nestedOptions = new SRRCreationOptions
            {
                AppName = options.AppName,
                AllowCompressed = true,
                ComputeOSOHashes = false,
            };
            try
            {
                SRRCreationResult vobResult = await _sRRService.CreateFromRARAsync(
                    physicalVobSrrPath, [sample], null, nestedOptions, ct);
                if (vobResult.Success)
                {
                    Log($"Created nested SRR for RAR-backed vob sample: {vobSrrLogicalName}");
                    result.Add(new StoredFileEntry(vobSrrLogicalName, physicalVobSrrPath));
                }
                else
                {
                    Log($"Nested SRR failed for RAR-backed vob sample {Path.GetFileName(sample)}: {vobResult.ErrorMessage}");
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                Log($"Nested SRR error for RAR-backed vob sample {Path.GetFileName(sample)}: {ex.Message}");
            }
        }

        return result;
    }

    /// <summary>
    /// Stages every subtitle SFV's generated artifacts in pyrescene's own two-pass order: pyrescene
    /// creates ALL nested subtitle SRRs first, in its vobsub pass 9 (<c>create_srr_for_subs</c>),
    /// and appends the excluded/subtitle SFVs' own bytes only in the final tail, pass 10 — so ALL
    /// nested SRRs precede ALL subtitle-SFV entries. An earlier version emitted each SFV's own entry
    /// BEFORE its nested SRRs (per-SFV interleaving), producing the reversed
    /// <c>[subs.sfv, eng.srr]</c>; the same-stem pass-10 reorder cannot repair that when the SFV
    /// name differs from the chain (a <c>subs.sfv</c> listing <c>eng.rar</c>), which is exactly
    /// every multi-language subtitle SFV.
    ///
    /// The subtitle SFV's own bytes are stored EXACTLY ONCE, however it got here — for a
    /// SCANNER-origin subtitle, the scanner's pass-10 (<c>foreach sfv in sfvs</c>,
    /// ReleaseScanner.cs) already stores EVERY sfv, including excluded/subtitle ones
    /// (<c>InputSfvs_Appended_AfterAllOtherCategories</c> proves it), so <paramref name="currentStored"/>
    /// already contains it and re-adding it here would be a duplicate. But
    /// <see cref="ExtraSubtitleSfvFiles"/> is ALSO populated by the manual <see cref="AddSubtitleAsync"/>
    /// command, whose source never reaches the scanner's traversal at all — pass-10 doesn't cover a
    /// manually-added one — so those need storing here. Both cases are reconciled with one
    /// OS-final-path dedup check (matching the scanner's own
    /// <see cref="ReleaseScanner.ResolveDedupKey"/> dedup discipline) instead of two separate code
    /// paths for "scanner-origin" vs. "manual".
    /// </summary>
    private async Task<List<StoredFileEntry>> GenerateSubtitleArtifactsAsync(
        List<StoredFileEntry> currentStored, string workDir, SRRCreationOptions options, bool generateNestedSrrs, CancellationToken ct)
    {
        List<string> subtitleSfvs = [.. ExtraSubtitleSfvFiles];
        if (subtitleSfvs.Count == 0)
        {
            return [];
        }

        var result = new List<StoredFileEntry>();

        // Pass 9 (create_srr_for_subs): create every subtitle SFV's nested SRRs first, so they all
        // precede the subtitle-SFV entries emitted in pass 10 below. Gated on CreateVobsubSRR
        // (threaded in as generateNestedSrrs) for pyrescene --vobsub-srr parity: pyrescene without
        // --vobsub-srr skips create_srr_for_subs entirely but STILL stores the extra_sfvs — so pass
        // 10 below runs regardless, keeping scanner-origin and manually-added subtitle SFVs stored.
        for (int i = 0; generateNestedSrrs && i < subtitleSfvs.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            string sfv = subtitleSfvs[i];
            string sfvBasename = Path.GetFileName(sfv);

            // FolderRelativeStem falls back to "Subs/<basename>" for a source OUTSIDE the release
            // root (ExtraSubtitleSfvFiles is shared with the file-mode Advanced tab's "Add
            // Subtitle" command) — the raw root-relative path would keep an invalid "../" the
            // writer's CanonicalizeRelative rejects.
            string stem = CreatorArtifactNaming.FolderRelativeStem(_releaseRoot!, sfv, "Subs");

            // Per the excerpt, "not for Proof RARs that are already stored inside the SRR" — skip
            // nested-SRR creation for this subtitle SFV when some entry ALREADY in the stored list
            // ends with its basename-stem swapped to ".rar" (`basename(esfv)[:-3] + "rar"`, the
            // excerpt's own slice: for a 4-char ".sfv" extension this simply swaps it to ".rar").
            // Skipping here does not affect the SFV's OWN storage (pass 10 below, independent of
            // this check) — only whether we ALSO wrap its RAR in a redundant nested SRR when the
            // RAR is already embedded directly (e.g. a proof RAR sharing this excluded SFV's stem).
            if (sfvBasename.Length > 3)
            {
                string candidateRarSuffix = sfvBasename[..^3] + "rar";
                if (currentStored.Any(e => e.StoredName.EndsWith(candidateRarSuffix, StringComparison.OrdinalIgnoreCase)))
                {
                    Log($"Subtitle SFV skipped (its RAR is already stored): {sfvBasename}");
                    continue;
                }
            }

            // The "directory" half of `stem` (everything up to the SFV's own last path segment) is
            // reused for EVERY nested SRR this subtitle SFV produces — each chain keeps ITS OWN
            // first-RAR basename as the file-name half (create_srr_for_subs), not the SFV's.
            int lastSlash = stem.LastIndexOf('/');
            string dirPrefix = lastSlash < 0 ? string.Empty : stem[..(lastSlash + 1)];
            result.AddRange(await GenerateNestedSubtitleSrrsAsync(sfv, dirPrefix, workDir, i, options, ct));
        }

        // Pass 10: append each subtitle SFV's own bytes AFTER all nested SRRs. Store this subtitle
        // SFV's own bytes unless SOME entry already in the stored list (pass-10's scanner-origin
        // storage, or an earlier iteration's own addition) resolves to the same OS-final-path —
        // dedup against re-storing a scanner-origin subtitle, while still covering a manually-added
        // one exactly once. (Nested SRRs already in `result` are `.srr` sources whose dedup keys
        // never collide with an SFV's, so the pass-9 additions above do not interfere with this
        // check.)
        //
        // [DIVERGENCE: determinism] A manually-added subtitle SFV (ExtraSubtitleSfvFiles via
        // AddSubtitleAsync — an app feature with no pyrescene equivalent) is staged here into
        // `result`, which the caller splices with the nested-SRR artifact block; a scanner-origin
        // subtitle SFV instead rides the `currentStored` baseline (the scanner's own pass-10). So in
        // a folder that has BOTH, the manually-added SFV precedes the scanner-discovered one in the
        // merged list. pyrescene has no manual-add feature, so there is NO parity target for this
        // relative order; the design already declares excluded-SFV ordering a [DIVERGENCE:
        // determinism]. The invariant that DOES matter — each subtitle SFV stored EXACTLY ONCE (the
        // dedup below) — holds regardless of that order.
        foreach (string sfv in subtitleSfvs)
        {
            ct.ThrowIfCancellationRequested();
            string sfvDedupKey = ReleaseScanner.ResolveDedupKey(sfv);
            bool sfvAlreadyStored = currentStored.Concat(result)
                .Any(e => string.Equals(ReleaseScanner.ResolveDedupKey(e.FullPath), sfvDedupKey, StringComparison.OrdinalIgnoreCase));
            if (!sfvAlreadyStored)
            {
                string stem = CreatorArtifactNaming.FolderRelativeStem(_releaseRoot!, sfv, "Subs");
                result.Add(new StoredFileEntry(stem + ".sfv", sfv));
            }
        }

        return result;
    }

    /// <summary>
    /// One nested SRR PER RAR CHAIN the subtitle SFV lists — matching pyrescene's
    /// <c>create_srr_for_subs</c>, which walks every "first RAR" it finds and makes a dedicated SRR
    /// named after THAT chain's own basename (e.g. a two-language subtitle SFV listing
    /// "eng.rar"+"eng.r00" and a separate "jpn.rar" yields "eng.srr" AND "jpn.srr", not one merged
    /// SRR).
    ///
    /// Chain grouping goes through the shared <see cref="SfvVolumeResolver.ResolveOrderedChains"/>
    /// — the SAME code <c>SRRWriter.ResolveVolumesAsync</c>'s SFV branch runs, so the two can never
    /// disagree. An earlier hand-rolled copy DIVERGED: <c>SFVFile.ReadFile</c> split every space (so
    /// it THREW <see cref="InvalidDataException"/> on a spaced RAR name like <c>my movie.rar</c>,
    /// dropping the whole chain), and a raw <see cref="Path.Combine(string, string)"/> left a
    /// <c>.\eng.r00</c> continuation keyed apart from its <c>eng.rar</c> head (splitting one chain
    /// into two same-named <c>eng.srr</c> SRRs — a duplicate logical name the writer then rejects).
    /// The resolver's space-tolerant parse + <see cref="SrrNameCanonicalizer.ResolveSfvEntry"/>
    /// fixes both, trusting the SFV's OWN listed entries as each chain's full membership (no fresh
    /// on-disk chain-walk, same as the writer). Each chain is written via
    /// <see cref="ISRRCreationService.CreateFromRARAsync"/> directly (not <c>CreateFromSFVAsync</c>)
    /// since the chain's volumes are already resolved here.
    /// </summary>
    private async Task<List<StoredFileEntry>> GenerateNestedSubtitleSrrsAsync(
        string sfvPath, string dirPrefix, string workDir, int index, SRRCreationOptions options, CancellationToken ct)
    {
        string sfvName = Path.GetFileName(sfvPath);
        string sfvDir = Path.GetDirectoryName(sfvPath) ?? ".";

        IReadOnlyList<IReadOnlyList<string>> chains;
        try
        {
            string[] lines = await File.ReadAllLinesAsync(sfvPath, ct).ConfigureAwait(false);
            chains = SfvVolumeResolver.ResolveOrderedChains(sfvDir, lines);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            Log($"  Nested SRR error for {sfvName}: {ex.Message}");
            return [];
        }

        if (chains.Count == 0)
        {
            Log($"  Nested SRR skipped for {sfvName}: no RAR volumes found in SFV.");
            return [];
        }

        // pyrescene's create_srr_for_subs HARDCODES its own nested-creation call's options
        // (`save_paths=False, compressed=True, oso_hash=False`) rather than forwarding whatever the
        // OUTER SRR's options happen to be — so a user enabling ComputeOSOHashes for the outer SRR
        // must not leak OSO blocks into a nested subtitle SRR pyrescene never adds them to.
        // `save_paths=False` needs no explicit handling: CreateFromRARAsync (unlike
        // CreateFromInputsAsync) has no relative-path mode at all, so it is already flat-name-only
        // by construction.
        var nestedOptions = new SRRCreationOptions
        {
            AppName = options.AppName,
            AllowCompressed = true,
            ComputeOSOHashes = false,
        };

        var result = new List<StoredFileEntry>();
        int chainIndex = 0;
        foreach (IReadOnlyList<string> chain in chains)
        {
            ct.ThrowIfCancellationRequested();
            // SfvVolumeResolver now returns each chain in first-seen LISTING order and no longer
            // sorts (so the writer's single sort stays byte-identical to base — see
            // SfvVolumeResolver.ResolveOrderedChains). This path, unlike the writer, needs the TRUE
            // first volume (volume[0]) for chainStem/naming and passes the volumes to
            // CreateFromRARAsync, so it applies its OWN single per-chain sort here.
            var volumes = new List<string>(chain);
            volumes.Sort(RARVolumeNameComparer.Instance);
            string firstVolumeName = Path.GetFileName(volumes[0]);
            string chainStem = firstVolumeName.Length >= 4 ? firstVolumeName[..^4] : firstVolumeName;
            string storedName = dirPrefix + chainStem + ".srr";
            string srrPath = Path.Combine(workDir, $"{index}_{chainIndex}_{chainStem}.srr");
            chainIndex++;
            Log($"Creating nested SRR for subtitle chain: {chainStem} (from {sfvName})");

            try
            {
                SRRCreationResult creation = await _sRRService.CreateFromRARAsync(srrPath, volumes, storedFiles: null, nestedOptions, ct);
                if (creation.Success)
                {
                    Log($"  Nested SRR created: {Path.GetFileName(srrPath)} ({creation.SRRFileSize:N0} bytes)");
                    result.Add(new StoredFileEntry(storedName, srrPath));
                }
                else
                {
                    Log($"  Nested SRR failed for {chainStem}: {creation.ErrorMessage}");
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                Log($"  Nested SRR error for {chainStem}: {ex.Message}");
            }
        }

        return result;
    }

    /// <summary>
    /// A nested subtitle SRR is RAR-blocks-ONLY — no embedded SFV, no sibling .nfo files. Confirmed
    /// against a real pyrescene <c>--vobsub-srr</c> golden: its nested SRR contains only the
    /// extracted RAR volume block(s); embedding the SFV (and sibling .nfo files) was this app's own
    /// PRE-EXISTING choice, shared by both the folder-mode staging path above and the
    /// wizard/Advanced <see cref="GenerateNestedSRRFileAsync"/> — and is also redundant regardless
    /// of the golden: the subtitle SFV's own bytes are already stored in the OUTER SRR (scanner
    /// pass-10 stores every SFV; see <see cref="GenerateSubtitleArtifactsAsync"/>'s remarks). Fixed
    /// globally (both callers), matching the RECOVERY_BLOCKS_REMOVED precedent: a shipped-behavior
    /// change applied everywhere the shared code path runs, not just the new folder-mode surface.
    /// </summary>
    private static List<StoredFileEntry>? BuildNestedSubtitleStoredFiles() => null;

    // ── SRS auto-creation (Advanced tab: scan + generate at create time) ──

    private async Task CreateSRSForSamplesAsync(string releaseDir, string tempDir, CancellationToken ct)
    {
        // Auto-detected samples plus any added manually on the wizard's Samples step.
        List<string> samples = [.. ReleaseFileScanner.FindSampleFiles(releaseDir)
            .Concat(ExtraSampleFiles)
            .Distinct(StringComparer.OrdinalIgnoreCase)];

        var srsOptions = new SRSCreationOptions
        {
            AppName = string.IsNullOrWhiteSpace(AppName) ? "ReScene Manager" : AppName
        };

        await GenerateAndRecordAsync(
            samples,
            (sample, i, token) => GenerateSRSFileAsync(sample, tempDir, i, srsOptions, token),
            (sample, srsPath) => StoredFiles.Add(new StoredFileItem
            {
                FullPath = srsPath,
                StoredName = CreatorArtifactNaming.GeneratedStoredName(releaseDir, sample, ".srs", "Sample"),
            }),
            ct);
    }

    // ── Vobsub nested SRR (Advanced tab: scan + generate at create time) ──

    private async Task CreateVobsubSRRsAsync(string releaseDir, SRRCreationOptions options, string tempDir, CancellationToken ct)
    {
        List<string> subtitleSfvs = [.. ReleaseFileScanner.FindSubtitleSFVFiles(releaseDir)
            .Concat(ExtraSubtitleSfvFiles)
            .Distinct(StringComparer.OrdinalIgnoreCase)];

        // The Advanced-tab create-time scan produces one nested SRR PER RAR CHAIN by REUSING the
        // folder-mode GenerateNestedSubtitleSrrsAsync — which already does the chain-split AND
        // builds its own nestedOptions (ComputeOSOHashes=false, AllowCompressed=true), so a
        // multi-language subtitle SFV yields per-chain SRRs (never one merged SRR) and a user
        // enabling ComputeOSOHashes for the outer run cannot leak OSO blocks into them.
        // GenerateAndRecordAsync's 1-source->1-path shape no longer fits (a single SFV can now
        // yield multiple SRRs), so record the returned list directly; the per-source index still
        // keeps temp filenames unique.
        for (int i = 0; i < subtitleSfvs.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            string sfv = subtitleSfvs[i];

            // dirPrefix = the directory half of the Advanced-tab CreatorArtifactNaming.GeneratedStoredName("Subs")
            // convention (strip the file-name), so each chain becomes `dirPrefix + chainStem +
            // ".srr"` — consistent with folder mode, where each chain keeps its OWN first-RAR
            // basename as the file-name half, not the subtitle SFV's.
            string generatedName = CreatorArtifactNaming.GeneratedStoredName(releaseDir, sfv, ".srr", "Subs");
            int lastSlash = generatedName.LastIndexOf('/');
            string dirPrefix = lastSlash < 0 ? string.Empty : generatedName[..(lastSlash + 1)];

            foreach (StoredFileEntry entry in await GenerateNestedSubtitleSrrsAsync(sfv, dirPrefix, tempDir, i, options, ct))
            {
                StoredFiles.Add(new StoredFileItem
                {
                    FullPath = entry.FullPath,
                    StoredName = entry.StoredName,
                });
            }
        }
    }

    // ── Per-file generators (shared by Advanced create-time and wizard placeholder paths) ──

    /// <summary>
    /// Creates one .srs from <paramref name="samplePath"/> into <paramref name="tempDir"/> and
    /// returns its path, or null on failure. The index keeps temp filenames unique so two samples
    /// sharing a basename don't overwrite each other (the prefix never reaches the SRR).
    /// </summary>
    private async Task<string?> GenerateSRSFileAsync(string samplePath, string tempDir, int index, SRSCreationOptions srsOptions, CancellationToken ct)
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
    /// into <paramref name="tempDir"/> and returns its path, or null on failure. Used only by the
    /// wizard's placeholder materialization (<see cref="MaterializePlaceholdersAsync"/>).
    ///
    /// Unlike the Advanced-tab create-time path (<see cref="CreateVobsubSRRsAsync"/>, which emits
    /// one nested SRR PER RAR CHAIN via <see cref="GenerateNestedSubtitleSrrsAsync"/>), this
    /// wizard-placeholder path stays single-merged-SRR: the placeholder→stored-item model is 1:1,
    /// so a subtitle SFV listing multiple chains still produces ONE merged SRR here. Per-chain
    /// support here needs a wizard model that can materialize one placeholder into several stored
    /// items. The oso-off option IS applied below regardless.
    /// </summary>
    private async Task<string?> GenerateNestedSRRFileAsync(string sfvPath, string tempDir, int index, SRRCreationOptions options, CancellationToken ct)
    {
        string sfvName = Path.GetFileName(sfvPath);
        string srrPath = Path.Combine(tempDir, $"{index}_{Path.ChangeExtension(sfvName, ".srr")}");
        Log($"Creating nested SRR for: {sfvName}");

        // Force the nested subtitle SRR's own options (AllowCompressed=true,
        // ComputeOSOHashes=false) — mirroring the folder-mode nestedOptions in
        // GenerateNestedSubtitleSrrsAsync — rather than forwarding the outer run's `options`, so a
        // user enabling ComputeOSOHashes for the outer SRR cannot leak OSO blocks into a nested
        // subtitle SRR pyrescene never adds them to.
        var nestedOptions = new SRRCreationOptions
        {
            AppName = options.AppName,
            AllowCompressed = true,
            ComputeOSOHashes = false,
        };

        try
        {
            SRRCreationResult result = await _sRRService.CreateFromSFVAsync(
                srrPath, sfvPath, BuildNestedSubtitleStoredFiles(), nestedOptions, ct);
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

    private void StoreFixRARFile(string releaseDir)
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
        List<string> rarFiles = ReleaseFileScanner.FindRARFilesFromSFV(sfvFiles[0]);
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
