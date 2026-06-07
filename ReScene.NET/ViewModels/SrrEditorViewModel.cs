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
    public partial FieldStatus SourceStatus { get; set; } = FieldStatus.Error("Choose the SRR file you want to edit.");

    // ── Output ──────────────────────────────────────────────

    [ObservableProperty]
    public partial string OutputPath { get; set; } = string.Empty;

    [ObservableProperty]
    public partial FieldStatus OutputStatus { get; set; } = FieldStatus.None;

    // ── Stored files ────────────────────────────────────────

    public ObservableCollection<string> StoredFiles { get; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RemoveStoredFileCommand))]
    [NotifyCanExecuteChangedFor(nameof(RenameStoredFileCommand))]
    [NotifyCanExecuteChangedFor(nameof(MoveStoredFileUpCommand))]
    [NotifyCanExecuteChangedFor(nameof(MoveStoredFileDownCommand))]
    public partial string? SelectedStoredFile { get; set; }

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
            SourceStatus = FieldStatus.Error("Choose the SRR file you want to edit.");
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
    /// </summary>
    public void ReloadList()
    {
        StoredFiles.Clear();

        if (_workingCopyPath is null)
        {
            return;
        }

        foreach (string name in _srrEditing.GetStoredFileNames(_workingCopyPath))
        {
            StoredFiles.Add(name);
        }
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
        string? path = await _fileDialog.SaveFileAsync("Save Edited SRR", ".srr", FileDialogFilters.SRRSave);
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

    private bool HasSelection() => !string.IsNullOrEmpty(SelectedStoredFile);

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void RemoveStoredFile()
    {
        if (_workingCopyPath is null || SelectedStoredFile is null)
        {
            return;
        }

        string name = SelectedStoredFile;

        try
        {
            _srrEditing.RemoveStoredFiles(_workingCopyPath, [name]);
            ReloadList();
            Log($"Removed stored file: {name}");
        }
        catch (Exception ex)
        {
            Log($"ERROR removing {name}: {ex.Message}");
        }
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private async Task RenameStoredFileAsync()
    {
        if (_workingCopyPath is null || SelectedStoredFile is null)
        {
            return;
        }

        string oldName = SelectedStoredFile;

        string? newName = await _fileDialog.PromptForTextAsync(
            "Rename stored file", "New name:", oldName);

        if (string.IsNullOrWhiteSpace(newName) || newName == oldName)
        {
            return;
        }

        try
        {
            await _srrEditing.RenameStoredFileAsync(_workingCopyPath, oldName, newName);
            ReloadList();
            SelectedStoredFile = StoredFiles.Contains(newName) ? newName : null;
            Log($"Renamed stored file: {oldName} → {newName}");
        }
        catch (Exception ex)
        {
            Log($"ERROR renaming {oldName}: {ex.Message}");
        }
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private Task MoveStoredFileUpAsync() => MoveStoredFileAsync(-1);

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private Task MoveStoredFileDownAsync() => MoveStoredFileAsync(+1);

    private async Task MoveStoredFileAsync(int offset)
    {
        if (_workingCopyPath is null || SelectedStoredFile is null)
        {
            return;
        }

        string name = SelectedStoredFile;

        try
        {
            await _srrEditing.MoveStoredFileAsync(_workingCopyPath, name, offset);
            ReloadList();
            // Preserve the selection across the reload so the user can keep nudging it.
            SelectedStoredFile = StoredFiles.Contains(name) ? name : null;
            Log($"Moved stored file {(offset < 0 ? "up" : "down")}: {name}");
        }
        catch (Exception ex)
        {
            Log($"ERROR moving {name}: {ex.Message}");
        }
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
        // Restore the same "choose an SRR" guidance a freshly-constructed VM shows, so a
        // re-opened wizard's Next gate (SourceStatus == Ok) behaves identically.
        SourceStatus = FieldStatus.Error("Choose the SRR file you want to edit.");
        OutputStatus = FieldStatus.None;

        StoredFiles.Clear();
        SelectedStoredFile = null;

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

        try
        {
            if (File.Exists(_workingCopyPath))
            {
                File.Delete(_workingCopyPath);
            }
        }
        catch
        {
            // Best-effort cleanup; a leftover temp is harmless and is also swept on app shutdown.
        }
    }

    private static string SuggestEditedSiblingPath(string sourcePath)
    {
        string dir = Path.GetDirectoryName(sourcePath) ?? ".";
        string name = Path.GetFileNameWithoutExtension(sourcePath);
        return Path.Combine(dir, $"{name} (edited).srr");
    }

    private void Log(string message) => AppendLogEntry(LogEntries, message);
}
