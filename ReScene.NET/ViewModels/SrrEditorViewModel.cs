using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ReScene.NET.Helpers;
using ReScene.NET.Models;
using ReScene.NET.Services;

namespace ReScene.NET.ViewModels;

/// <summary>
/// Shared ViewModel for the Beginner "Edit an SRR" wizard. Edits an existing SRR's
/// stored files (add / remove / rename / reorder) non-destructively: the original
/// <see cref="SourcePath"/> is never modified — edits are applied to a temp working
/// copy and written to a user-chosen <see cref="OutputPath"/> on Save.
/// </summary>
public partial class SrrEditorViewModel(ISrrEditingService srrEditing, IFileDialogService fileDialog, ITempDirectoryService tempDir) : ViewModelBase
{
    private readonly ISrrEditingService _srrEditing = srrEditing;
    private readonly IFileDialogService _fileDialog = fileDialog;
    private readonly ITempDirectoryService _tempDir = tempDir;

    /// <summary>Full path to the temp working copy currently being edited, if any.</summary>
    private string? _workingCopyPath;

    /// <summary>The <see cref="SourcePath"/> the current working copy was created from.</summary>
    private string? _workingCopySource;

    // ── Source ──────────────────────────────────────────────

    [ObservableProperty]
    public partial string SourcePath { get; set; } = string.Empty;

    [ObservableProperty]
    public partial FieldStatus SourceStatus { get; set; } = FieldStatus.None;

    // ── Output ──────────────────────────────────────────────

    [ObservableProperty]
    public partial string OutputPath { get; set; } = string.Empty;

    [ObservableProperty]
    public partial FieldStatus OutputStatus { get; set; } = FieldStatus.None;

    // ── Stored files ────────────────────────────────────────

    public ObservableCollection<StoredFileInfo> StoredFiles { get; } = [];

    /// <summary>
    /// The current multi-selection from the stored-file grid — the source of truth for which
    /// stored files the edit commands act on. Maintained via <see cref="SetSelection"/> because
    /// <c>DataGrid.SelectedItems</c> is not bindable, so the view forwards selection changes.
    /// </summary>
    public ObservableCollection<StoredFileInfo> SelectedStoredFiles { get; } = [];

    /// <summary>
    /// The single "anchor" row, kept in sync with the grid's primary <c>SelectedItem</c>. Used
    /// only to re-highlight a row after rename/move; command enablement and the operation targets
    /// come from <see cref="SelectedStoredFiles"/>.
    /// </summary>
    [ObservableProperty]
    public partial StoredFileInfo? SelectedStoredFile { get; set; }

    // ── Manage step status ──────────────────────────────────

    [ObservableProperty]
    public partial FieldStatus ManageStatus { get; set; } = FieldStatus.None;

    // ── Result / log ────────────────────────────────────────

    public ObservableCollection<string> LogEntries { get; } = [];

    [ObservableProperty]
    public partial bool IsSaving { get; set; }

    [ObservableProperty]
    public partial string ResultMessage { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool ShowResult { get; set; }

    // ── Source/output guidance ──────────────────────────────

    partial void OnSourcePathChanged(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            SourceStatus = FieldStatus.None;
            return;
        }

        if (!Path.GetExtension(value).Equals(".srr", StringComparison.OrdinalIgnoreCase))
        {
            SourceStatus = FieldStatus.Error("This is not an .srr file.");
            return;
        }

        if (!File.Exists(value))
        {
            SourceStatus = FieldStatus.Error("This file does not exist.");
            return;
        }

        SourceStatus = FieldStatus.Ok($"Editing \"{Path.GetFileName(value)}\".");

        if (string.IsNullOrWhiteSpace(OutputPath))
        {
            OutputPath = SuggestEditedSiblingPath(value);
            OutputStatus = FieldStatus.Info("Auto-filled next to the source. Change it if you want the edited SRR elsewhere.");
        }
    }

    partial void OnOutputPathChanged(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            OutputStatus = FieldStatus.None;
        }
    }

    // ── Working copy ────────────────────────────────────────

    /// <summary>
    /// Builds or refreshes the temp working copy of <see cref="SourcePath"/>, then reloads
    /// the stored-file list. Idempotent per source: if a working copy already exists for the
    /// same <see cref="SourcePath"/> it is kept so navigating Back → Next does not discard
    /// edits; it is recreated only when the source changes. Called when leaving the source step.
    /// </summary>
    public void EnsureWorkingCopy()
    {
        if (_workingCopyPath is not null && _workingCopySource == SourcePath)
        {
            return;
        }

        DeleteWorkingCopy();

        _workingCopyPath = CreateWorkingCopy(SourcePath);
        _workingCopySource = SourcePath;

        ReloadList();
    }

    /// <summary>
    /// Creates the temp working copy of <paramref name="sourcePath"/> and returns its full path.
    /// Overridable so tests can substitute a dummy path without touching the file system.
    /// </summary>
    protected virtual string CreateWorkingCopy(string sourcePath)
    {
        string tempDir = _tempDir.CreateTempDirectory();
        string workingPath = Path.Combine(tempDir, Path.GetFileName(sourcePath));
        File.Copy(sourcePath, workingPath, overwrite: true);
        return workingPath;
    }

    /// <summary>
    /// Reloads <see cref="StoredFiles"/> from the working copy, via the editing service.
    /// Optionally restores the selection to the entry whose <see cref="StoredFileInfo.Name"/>
    /// matches <paramref name="selectName"/> (pass <see langword="null"/> to leave the
    /// selection cleared).
    /// </summary>
    public void ReloadList(string? selectName = null)
    {
        StoredFiles.Clear();

        if (_workingCopyPath is null)
        {
            SelectedStoredFile = null;
            SetSelection([]);
            return;
        }

        foreach (StoredFileInfo info in _srrEditing.GetStoredFiles(_workingCopyPath))
        {
            StoredFiles.Add(info);
        }

        StoredFileInfo? match = selectName is not null
            ? StoredFiles.FirstOrDefault(f => f.Name == selectName)
            : null;

        // Re-highlight the single row (VM → grid), and mirror it into the selection collection
        // so command enablement/targets stay correct even without the view (e.g. in unit tests).
        SelectedStoredFile = match;
        SetSelection(match is not null ? [match] : []);
    }

    // ── Selection ───────────────────────────────────────────

    /// <summary>
    /// Replaces the current selection with <paramref name="items"/> and re-evaluates the edit
    /// commands' enablement. The view calls this when the grid selection changes (and it is used
    /// internally after list reloads), because <c>DataGrid.SelectedItems</c> cannot be data-bound.
    /// </summary>
    public void SetSelection(IReadOnlyList<StoredFileInfo> items)
    {
        // Idempotent: re-highlighting after a reload bounces the grid's SelectedItem binding back
        // through SelectionChanged, calling this again with the same selection. Short-circuiting
        // avoids redundant command re-notification and any binding re-entrancy.
        if (SelectedStoredFiles.SequenceEqual(items))
        {
            return;
        }

        SelectedStoredFiles.Clear();
        foreach (StoredFileInfo item in items)
        {
            SelectedStoredFiles.Add(item);
        }

        // Command enablement depends ONLY on SelectedStoredFiles.Count (HasSelection /
        // HasSingleSelection) — not on SelectedStoredFile — so this is the single place that
        // re-evaluates it. (That is also why SelectedStoredFile carries no NotifyCanExecuteChangedFor.)
        RemoveStoredFileCommand.NotifyCanExecuteChanged();
        RenameStoredFileCommand.NotifyCanExecuteChanged();
        MoveStoredFileUpCommand.NotifyCanExecuteChanged();
        MoveStoredFileDownCommand.NotifyCanExecuteChanged();
        ExtractStoredFileCommand.NotifyCanExecuteChanged();
    }

    // ── Browse ──────────────────────────────────────────────

    [RelayCommand]
    private async Task BrowseSourceAsync()
    {
        string? path = await _fileDialog.OpenFileAsync("Select SRR to Edit", FileDialogFilters.SRRFiles);
        if (path is not null)
        {
            SourcePath = path;
        }
    }

    [RelayCommand]
    private async Task BrowseOutputAsync()
    {
        // Pre-fill the save dialog with the intended output: the current OutputPath (already
        // auto-filled to a "(edited)" sibling), or one derived from the source if it was cleared.
        string? suggested = !string.IsNullOrWhiteSpace(OutputPath)
            ? OutputPath
            : !string.IsNullOrWhiteSpace(SourcePath)
                ? SuggestEditedSiblingPath(SourcePath)
                : null;

        string? path = await _fileDialog.SaveFileAsync(
            "Save Edited SRR", ".srr", FileDialogFilters.SRRSave, suggested);
        if (path is not null)
        {
            OutputPath = path;
        }
    }

    // ── Edit commands ───────────────────────────────────────

    [RelayCommand]
    private async Task AddStoredFilesAsync()
    {
        if (_workingCopyPath is null)
        {
            return;
        }

        IReadOnlyList<string> paths = await _fileDialog.OpenFilesAsync(
            "Select Files to Store", FileDialogFilters.StoredFiles);

        if (paths.Count == 0)
        {
            return;
        }

        var files = paths
            .Select(p => (StoredName: Path.GetFileName(p), FilePath: p))
            .ToList();

        try
        {
            _srrEditing.AddStoredFiles(_workingCopyPath, files);
            ReloadList();
            Log($"Added {files.Count} file(s): {string.Join(", ", files.Select(f => f.StoredName))}");
        }
        catch (Exception ex)
        {
            Log($"ERROR adding files: {ex.Message}");
        }
    }

    /// <summary>True when at least one stored file is selected (Remove / Extract).</summary>
    private bool HasSelection() => SelectedStoredFiles.Count > 0;

    /// <summary>True when exactly one stored file is selected (Rename / Move up / Move down).</summary>
    private bool HasSingleSelection() => SelectedStoredFiles.Count == 1;

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void RemoveStoredFile()
    {
        if (_workingCopyPath is null || SelectedStoredFiles.Count == 0)
        {
            return;
        }

        List<string> names = SelectedStoredFiles.Select(f => f.Name).ToList();

        try
        {
            _srrEditing.RemoveStoredFiles(_workingCopyPath, names);
            ReloadList();
            Log($"Removed {names.Count} stored file(s): {string.Join(", ", names)}");
        }
        catch (Exception ex)
        {
            Log($"ERROR removing files: {ex.Message}");
        }
    }

    [RelayCommand(CanExecute = nameof(HasSingleSelection))]
    private async Task RenameStoredFileAsync()
    {
        if (_workingCopyPath is null || SelectedStoredFiles.Count != 1)
        {
            return;
        }

        string oldName = SelectedStoredFiles[0].Name;

        string? newName = await _fileDialog.PromptForTextAsync(
            "Rename stored file", "New name:", oldName);

        if (string.IsNullOrWhiteSpace(newName) || newName == oldName)
        {
            return;
        }

        try
        {
            await _srrEditing.RenameStoredFileAsync(_workingCopyPath, oldName, newName);
            ReloadList(selectName: newName);
            Log($"Renamed stored file: {oldName} → {newName}");
        }
        catch (Exception ex)
        {
            Log($"ERROR renaming {oldName}: {ex.Message}");
        }
    }

    [RelayCommand(CanExecute = nameof(HasSingleSelection))]
    private Task MoveStoredFileUpAsync() => MoveStoredFileAsync(-1);

    [RelayCommand(CanExecute = nameof(HasSingleSelection))]
    private Task MoveStoredFileDownAsync() => MoveStoredFileAsync(+1);

    private async Task MoveStoredFileAsync(int offset)
    {
        if (_workingCopyPath is null || SelectedStoredFiles.Count != 1)
        {
            return;
        }

        string name = SelectedStoredFiles[0].Name;

        try
        {
            await _srrEditing.MoveStoredFileAsync(_workingCopyPath, name, offset);
            // Preserve the selection across the reload so the user can keep nudging it.
            ReloadList(selectName: name);
            Log($"Moved stored file {(offset < 0 ? "up" : "down")}: {name}");
        }
        catch (Exception ex)
        {
            Log($"ERROR moving {name}: {ex.Message}");
        }
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private async Task ExtractStoredFileAsync()
    {
        if (_workingCopyPath is null || SelectedStoredFiles.Count == 0)
        {
            return;
        }

        List<string> names = SelectedStoredFiles.Select(f => f.Name).ToList();
        string? folder = await _fileDialog.OpenFolderAsync(
            names.Count == 1 ? "Choose where to save the file" : "Choose where to save the files");
        if (folder is null)
        {
            return;
        }

        int saved = 0;
        var failures = new List<string>();
        string? lastPath = null;

        foreach (string name in names)
        {
            try
            {
                string? path = await _srrEditing.ExtractStoredFileAsync(_workingCopyPath, folder, name);
                if (path is not null)
                {
                    saved++;
                    lastPath = path;
                    Log($"Extracted \"{name}\" to {path}");
                }
                else
                {
                    failures.Add(name);
                    Log($"Could not find \"{name}\" to extract.");
                }
            }
            catch (Exception ex)
            {
                failures.Add(name);
                Log($"Extract failed for \"{name}\": {ex.Message}");
            }
        }

        ManageStatus = BuildExtractStatus(saved, failures, folder, lastPath, names);
    }

    /// <summary>Summarises the outcome of an extract over one or more selected files.</summary>
    private static FieldStatus BuildExtractStatus(
        int saved, IReadOnlyList<string> failures, string folder, string? lastPath, IReadOnlyList<string> names)
    {
        if (failures.Count == 0)
        {
            return saved == 1
                ? FieldStatus.Ok($"Saved \"{names[0]}\" to {lastPath}")
                : FieldStatus.Ok($"Saved {saved} files to {folder}");
        }

        if (saved == 0)
        {
            return failures.Count == 1
                ? FieldStatus.Error($"Could not extract \"{failures[0]}\".")
                : FieldStatus.Error($"Could not extract {failures.Count} files.");
        }

        return FieldStatus.Warning($"Saved {saved} file(s); {failures.Count} could not be extracted.");
    }

    // ── Save ────────────────────────────────────────────────

    /// <summary>
    /// Copies the working copy to <see cref="OutputPath"/> (overwriting), recording the result.
    /// Called when leaving the save step.
    /// </summary>
    public void Save()
    {
        ShowResult = true;

        if (_workingCopyPath is null)
        {
            ResultMessage = "Nothing to save — no working copy was created.";
            Log("ERROR: no working copy to save.");
            return;
        }

        if (string.IsNullOrWhiteSpace(OutputPath))
        {
            ResultMessage = "No output path was chosen.";
            Log("ERROR: no output path.");
            return;
        }

        IsSaving = true;
        try
        {
            CopyWorkingCopyTo(OutputPath);
            ResultMessage = $"Saved edited SRR to:\n{OutputPath}";
            Log($"Saved edited SRR to {OutputPath}");
        }
        catch (Exception ex)
        {
            ResultMessage = $"Failed to save: {ex.Message}";
            Log($"ERROR saving to {OutputPath}: {ex.Message}");
        }
        finally
        {
            IsSaving = false;
        }
    }

    /// <summary>
    /// Copies the working copy to <paramref name="outputPath"/>, overwriting any existing file.
    /// Overridable so tests can verify the save without touching the file system.
    /// </summary>
    protected virtual void CopyWorkingCopyTo(string outputPath)
        => File.Copy(_workingCopyPath!, outputPath, overwrite: true);

    // ── Reset ───────────────────────────────────────────────

    /// <summary>
    /// Clears all state back to a freshly-constructed default and deletes any working copy,
    /// so the next wizard open starts clean.
    /// </summary>
    public void Reset()
    {
        DeleteWorkingCopy();
        _workingCopyPath = null;
        _workingCopySource = null;

        SourcePath = string.Empty;
        OutputPath = string.Empty;
        // Empty source shows no status (the step's intro text already guides the user); the
        // Next gate (SourceStatus == Ok) stays blocked until a valid .srr is chosen.
        SourceStatus = FieldStatus.None;
        OutputStatus = FieldStatus.None;

        StoredFiles.Clear();
        SelectedStoredFile = null;
        SetSelection([]);
        ManageStatus = FieldStatus.None;

        LogEntries.Clear();
        ResultMessage = string.Empty;
        ShowResult = false;
        IsSaving = false;
    }

    // ── Helpers ─────────────────────────────────────────────

    private void DeleteWorkingCopy()
    {
        if (_workingCopyPath is null)
        {
            return;
        }

        // Clean the whole GUID temp folder, not just the file, so temps don't accumulate.
        // Cleanup is best-effort (it suppresses its own exceptions).
        _tempDir.Cleanup(Path.GetDirectoryName(_workingCopyPath));
    }

    private static string SuggestEditedSiblingPath(string sourcePath)
    {
        string dir = Path.GetDirectoryName(sourcePath) ?? ".";
        string name = Path.GetFileNameWithoutExtension(sourcePath);
        return Path.Combine(dir, $"{name} (edited).srr");
    }

    private void Log(string message) => AppendLogEntry(LogEntries, message);
}
