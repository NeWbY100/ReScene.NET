using ReScene.NET.Models;
using ReScene.NET.Services;
using ReScene.NET.ViewModels;

namespace ReScene.NET.Tests;

// NOTE: the grid's code-behind handlers in EditSrrWizardBody.xaml.cs — SelectionChanged
// (forwards DataGrid.SelectedItems to vm.SetSelection) and PreviewMouseDown (clears the
// selection on an empty-space left-click) — require a live WPF visual tree and are not
// covered here. These tests drive vm.SetSelection directly, which is exactly what the
// SelectionChanged handler forwards, so the VM-side selection logic is fully exercised.
public class SrrEditorViewModelTests
{
    // ── Fakes ───────────────────────────────────────────────

    /// <summary>
    /// Records every call made to the editing service and serves a scripted stored-file
    /// list from <see cref="StoredFileNames"/>, so the ViewModel orchestration can be
    /// verified without any real SRR file or file I/O.
    /// </summary>
    private sealed class FakeSrrEditingService : ISrrEditingService
    {
        // Internal list of names; sizes default to 0 for fakes that don't care about size.
        public List<string> StoredFileNames { get; } = [];
        public List<string> Calls { get; } = [];

        public string? LastPath { get; private set; }
        public IReadOnlyList<(string StoredName, string FilePath)>? LastAdded { get; private set; }
        public IReadOnlyList<string>? LastRemoved { get; private set; }
        public (string Path, string Old, string New)? LastRenamed { get; private set; }
        public (string Path, string Name, int Offset)? LastMoved { get; private set; }
        public (string SrrPath, string OutputDir, string StoredName)? LastExtracted { get; private set; }

        /// <summary>Every extraction call, in order — lets multi-select extraction be verified.</summary>
        public List<(string SrrPath, string OutputDir, string StoredName)> Extractions { get; } = [];

        /// <summary>Scripted return value for <see cref="ExtractStoredFileAsync"/>. Null simulates not found.</summary>
        public string? ExtractResult { get; set; }

        /// <summary>Per-name scripted results; takes precedence over <see cref="ExtractResult"/> when present.</summary>
        public Dictionary<string, string?> ExtractResultByName { get; } = [];

        public void AddStoredFiles(string srrFilePath, IReadOnlyList<(string StoredName, string FilePath)> files)
        {
            Calls.Add(nameof(AddStoredFiles));
            LastPath = srrFilePath;
            LastAdded = files;
            foreach ((string storedName, _) in files)
            {
                StoredFileNames.Add(storedName);
            }
        }

        public void RemoveStoredFiles(string srrFilePath, IReadOnlyList<string> storedNames)
        {
            Calls.Add(nameof(RemoveStoredFiles));
            LastPath = srrFilePath;
            LastRemoved = storedNames;
            foreach (string name in storedNames)
            {
                StoredFileNames.Remove(name);
            }
        }

        public Task RenameStoredFileAsync(string srrPath, string oldName, string newName, CancellationToken ct = default)
        {
            Calls.Add(nameof(RenameStoredFileAsync));
            LastPath = srrPath;
            LastRenamed = (srrPath, oldName, newName);
            int idx = StoredFileNames.IndexOf(oldName);
            if (idx >= 0)
            {
                StoredFileNames[idx] = newName;
            }
            return Task.CompletedTask;
        }

        public Task MoveStoredFileAsync(string srrPath, string storedName, int offset, CancellationToken ct = default)
        {
            Calls.Add(nameof(MoveStoredFileAsync));
            LastPath = srrPath;
            LastMoved = (srrPath, storedName, offset);
            int idx = StoredFileNames.IndexOf(storedName);
            int target = idx + offset;
            if (idx >= 0 && target >= 0 && target < StoredFileNames.Count)
            {
                StoredFileNames.RemoveAt(idx);
                StoredFileNames.Insert(target, storedName);
            }
            return Task.CompletedTask;
        }

        public IReadOnlyList<StoredFileInfo> GetStoredFiles(string srrFilePath)
        {
            Calls.Add(nameof(GetStoredFiles));
            LastPath = srrFilePath;
            return StoredFileNames.Select(n => new StoredFileInfo(n, 0L)).ToList();
        }

        public Task<string?> ExtractStoredFileAsync(string srrFilePath, string outputDirectory, string storedName, CancellationToken ct = default)
        {
            Calls.Add(nameof(ExtractStoredFileAsync));
            LastExtracted = (srrFilePath, outputDirectory, storedName);
            Extractions.Add((srrFilePath, outputDirectory, storedName));
            string? result = ExtractResultByName.TryGetValue(storedName, out string? perName) ? perName : ExtractResult;
            return Task.FromResult(result);
        }

        /// <summary>Scripted bytes returned by <see cref="ReadStoredFileBytesAsync"/>.</summary>
        public byte[]? BytesToReturn { get; set; }

        /// <summary>The (path, name) of the last read request.</summary>
        public (string Path, string Name)? LastRead { get; private set; }

        public Task<byte[]?> ReadStoredFileBytesAsync(string srrFilePath, string storedName, CancellationToken ct = default)
        {
            Calls.Add(nameof(ReadStoredFileBytesAsync));
            LastRead = (srrFilePath, storedName);
            return Task.FromResult(BytesToReturn);
        }
    }

    /// <summary>Fake dialog: serves scripted responses and records prompts.</summary>
    private sealed class FakeFileDialogService : NoOpFileDialogService
    {
        public string? OpenFileResult { get; set; }
        public IReadOnlyList<string> OpenFilesResult { get; set; } = [];
        public string? SaveFileResult { get; set; }
        public string? OpenFolderResult { get; set; }
        public string? PromptResult { get; set; }

        public string? LastPromptInitialValue { get; private set; }
        public string? LastSaveDefaultFileName { get; private set; }

        public override Task<string?> OpenFileAsync(string title, IReadOnlyList<string> filters) => Task.FromResult(OpenFileResult);
        public override Task<IReadOnlyList<string>> OpenFilesAsync(string title, IReadOnlyList<string> filters) => Task.FromResult(OpenFilesResult);

        public override Task<string?> SaveFileAsync(string title, string defaultExtension, IReadOnlyList<string> filters, string? defaultFileName = null)
        {
            LastSaveDefaultFileName = defaultFileName;
            return Task.FromResult(SaveFileResult);
        }
        public override Task<string?> OpenFolderAsync(string title) => Task.FromResult(OpenFolderResult);
        public override Task<bool> ShowConfirmAsync(string title, string message) => Task.FromResult(true);

        public override Task<string?> PromptForTextAsync(string title, string message, string initialValue)
        {
            LastPromptInitialValue = initialValue;
            return Task.FromResult(PromptResult);
        }

        public override bool Confirm(string title, string message) => true;
    }

    /// <summary>
    /// Test ViewModel that overrides the working-copy seam to return a dummy path with no I/O,
    /// so the orchestration runs against the fake service without touching disk.
    /// </summary>
    private sealed class TestSrrEditorViewModel(ISrrEditingService srrEditing, IFileDialogService fileDialog, ITempDirectoryService tempDir)
        : SrrEditorViewModel(srrEditing, fileDialog, tempDir)
    {
        public const string DummyWorkingPath = @"X:\__working__\copy.srr";

        public int CreateWorkingCopyCalls { get; private set; }
        public int CopyWorkingCopyToCalls { get; private set; }
        public string? LastCopiedTo { get; private set; }

        protected override string CreateWorkingCopy(string sourcePath)
        {
            CreateWorkingCopyCalls++;
            return DummyWorkingPath;
        }

        protected override void CopyWorkingCopyTo(string outputPath)
        {
            CopyWorkingCopyToCalls++;
            LastCopiedTo = outputPath;
        }
    }

    private static TestSrrEditorViewModel CreateVm(
        out FakeSrrEditingService editing,
        out FakeFileDialogService dialog)
    {
        editing = new FakeSrrEditingService();
        dialog = new FakeFileDialogService();
        return new TestSrrEditorViewModel(editing, dialog, new NoOpTempDirectoryService());
    }

    // ── StoredFileInfo model ────────────────────────────────

    [Fact]
    public void StoredFileInfo_SizeText_IsFormattedForKnownSize()
    {
        var info = new StoredFileInfo("readme.nfo", 2048L);
        // 2048 B = 2 KB
        Assert.Equal("2 KB", info.SizeText);
    }

    [Fact]
    public void StoredFileInfo_SizeText_IsNonEmptyForZeroBytes()
    {
        var info = new StoredFileInfo("empty.nfo", 0L);
        Assert.False(string.IsNullOrEmpty(info.SizeText));
    }

    // ── OnSourcePathChanged ─────────────────────────────────

    [Fact]
    public void FreshVm_SourceStatusIsNone()
    {
        TestSrrEditorViewModel vm = CreateVm(out _, out _);
        Assert.Equal(FieldState.None, vm.SourceStatus.State);
    }

    [Fact]
    public void ClearingSource_SetsNone()
    {
        TestSrrEditorViewModel vm = CreateVm(out _, out _);
        vm.SourcePath = @"C:\rel\movie.srr";   // non-existent path → Error, but a definite change
        vm.SourcePath = string.Empty;          // exercises the empty branch of OnSourcePathChanged
        Assert.Equal(FieldState.None, vm.SourceStatus.State);
    }

    [Fact]
    public void NonSrrSource_SetsError()
    {
        TestSrrEditorViewModel vm = CreateVm(out _, out _);
        vm.SourcePath = @"C:\rel\movie.txt";
        Assert.Equal(FieldState.Error, vm.SourceStatus.State);
    }

    [Fact]
    public void MissingSrrSource_SetsError()
    {
        TestSrrEditorViewModel vm = CreateVm(out _, out _);
        vm.SourcePath = @"C:\does\not\exist.srr";
        Assert.Equal(FieldState.Error, vm.SourceStatus.State);
    }

    [Fact]
    public void ExistingSrrSource_SetsOk_AndAutoFillsOutput()
    {
        TestSrrEditorViewModel vm = CreateVm(out _, out _);
        string srr = Path.Combine(Path.GetTempPath(), $"srr-edit-{Guid.NewGuid():N}.srr");
        File.WriteAllText(srr, "x");
        try
        {
            vm.SourcePath = srr;

            Assert.Equal(FieldState.Ok, vm.SourceStatus.State);
            Assert.Equal(FieldState.Info, vm.OutputStatus.State);
            Assert.EndsWith(" (edited).srr", vm.OutputPath, StringComparison.Ordinal);
            Assert.Equal(Path.GetDirectoryName(srr), Path.GetDirectoryName(vm.OutputPath));
        }
        finally
        {
            File.Delete(srr);
        }
    }

    [Fact]
    public void OutputAutoFill_DoesNotClobberUserValue()
    {
        TestSrrEditorViewModel vm = CreateVm(out _, out _);
        string srr = Path.Combine(Path.GetTempPath(), $"srr-edit-{Guid.NewGuid():N}.srr");
        File.WriteAllText(srr, "x");
        try
        {
            vm.OutputPath = @"D:\mine\custom.srr";
            vm.SourcePath = srr;

            Assert.Equal(@"D:\mine\custom.srr", vm.OutputPath);
        }
        finally
        {
            File.Delete(srr);
        }
    }

    [Fact]
    public void ClearingOutput_HidesStatus()
    {
        TestSrrEditorViewModel vm = CreateVm(out _, out _);
        vm.OutputPath = @"D:\mine\custom.srr";
        vm.OutputPath = string.Empty;
        Assert.Equal(FieldState.None, vm.OutputStatus.State);
    }

    // ── Browse output ───────────────────────────────────────

    [Fact]
    public async Task BrowseOutput_PrefillsCurrentOutputPathAsDefaultFileName()
    {
        TestSrrEditorViewModel vm = CreateVm(out _, out FakeFileDialogService dialog);
        vm.OutputPath = @"D:\rel\movie (edited).srr";
        dialog.SaveFileResult = @"D:\chosen\out.srr";

        await vm.BrowseOutputCommand.ExecuteAsync(null);

        Assert.Equal(@"D:\rel\movie (edited).srr", dialog.LastSaveDefaultFileName);
        Assert.Equal(@"D:\chosen\out.srr", vm.OutputPath);
    }

    [Fact]
    public async Task BrowseOutput_FallsBackToSiblingName_WhenOutputCleared()
    {
        TestSrrEditorViewModel vm = CreateVm(out _, out FakeFileDialogService dialog);
        string srr = Path.Combine(Path.GetTempPath(), $"srr-edit-{Guid.NewGuid():N}.srr");
        File.WriteAllText(srr, "x");
        try
        {
            vm.SourcePath = srr;            // auto-fills OutputPath
            vm.OutputPath = string.Empty;   // user cleared it
            dialog.SaveFileResult = null;   // cancelled

            await vm.BrowseOutputCommand.ExecuteAsync(null);

            Assert.NotNull(dialog.LastSaveDefaultFileName);
            Assert.EndsWith(" (edited).srr", dialog.LastSaveDefaultFileName!, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(srr);
        }
    }

    // ── EnsureWorkingCopy / ReloadList ──────────────────────

    [Fact]
    public void EnsureWorkingCopy_PopulatesListFromService()
    {
        TestSrrEditorViewModel vm = CreateVm(out FakeSrrEditingService editing, out _);
        editing.StoredFileNames.AddRange(["a.nfo", "b.sfv"]);
        vm.SourcePath = @"C:\rel\movie.srr";

        vm.EnsureWorkingCopy();

        Assert.Equal(1, vm.CreateWorkingCopyCalls);
        Assert.Equal(["a.nfo", "b.sfv"], vm.StoredFiles.Select(f => f.Name));
        Assert.Equal(TestSrrEditorViewModel.DummyWorkingPath, editing.LastPath);
    }

    [Fact]
    public void EnsureWorkingCopy_IsIdempotentForSameSource()
    {
        TestSrrEditorViewModel vm = CreateVm(out FakeSrrEditingService editing, out _);
        editing.StoredFileNames.Add("a.nfo");
        vm.SourcePath = @"C:\rel\movie.srr";

        vm.EnsureWorkingCopy();
        vm.EnsureWorkingCopy();

        Assert.Equal(1, vm.CreateWorkingCopyCalls);
    }

    [Fact]
    public void EnsureWorkingCopy_RecreatesWhenSourceChanges()
    {
        TestSrrEditorViewModel vm = CreateVm(out _, out _);
        vm.SourcePath = @"C:\rel\one.srr";
        vm.EnsureWorkingCopy();

        vm.SourcePath = @"C:\rel\two.srr";
        vm.EnsureWorkingCopy();

        Assert.Equal(2, vm.CreateWorkingCopyCalls);
    }

    // ── Edit commands call service + reload ─────────────────

    [Fact]
    public void AddStoredFiles_CallsServiceAndReloads()
    {
        TestSrrEditorViewModel vm = CreateVm(out FakeSrrEditingService editing, out FakeFileDialogService dialog);
        vm.SourcePath = @"C:\rel\movie.srr";
        vm.EnsureWorkingCopy();
        dialog.OpenFilesResult = [@"C:\rel\new.nfo"];

        vm.AddStoredFilesCommand.Execute(null);

        Assert.Contains(nameof(FakeSrrEditingService.AddStoredFiles), editing.Calls);
        Assert.Equal(TestSrrEditorViewModel.DummyWorkingPath, editing.LastPath);
        Assert.NotNull(editing.LastAdded);
        Assert.Equal("new.nfo", editing.LastAdded![0].StoredName);
        Assert.Contains("new.nfo", vm.StoredFiles.Select(f => f.Name));
    }

    [Fact]
    public void AddStoredFiles_NoOpWhenDialogCancelled()
    {
        TestSrrEditorViewModel vm = CreateVm(out FakeSrrEditingService editing, out FakeFileDialogService dialog);
        vm.SourcePath = @"C:\rel\movie.srr";
        vm.EnsureWorkingCopy();
        dialog.OpenFilesResult = [];   // user cancelled / picked nothing

        vm.AddStoredFilesCommand.Execute(null);

        Assert.DoesNotContain(nameof(FakeSrrEditingService.AddStoredFiles), editing.Calls);
        Assert.Null(editing.LastAdded);
    }

    [Fact]
    public void RemoveStoredFile_CallsServiceWithSelectionAndReloads()
    {
        TestSrrEditorViewModel vm = CreateVm(out FakeSrrEditingService editing, out _);
        editing.StoredFileNames.AddRange(["a.nfo", "b.sfv"]);
        vm.SourcePath = @"C:\rel\movie.srr";
        vm.EnsureWorkingCopy();
        vm.SetSelection([vm.StoredFiles.First(f => f.Name == "a.nfo")]);

        vm.RemoveStoredFileCommand.Execute(null);

        Assert.Equal(["a.nfo"], editing.LastRemoved);
        Assert.Equal(TestSrrEditorViewModel.DummyWorkingPath, editing.LastPath);
        Assert.DoesNotContain("a.nfo", vm.StoredFiles.Select(f => f.Name));
        Assert.Contains("b.sfv", vm.StoredFiles.Select(f => f.Name));
    }

    [Fact]
    public void RenameStoredFile_CallsServiceAndPreservesSelection()
    {
        TestSrrEditorViewModel vm = CreateVm(out FakeSrrEditingService editing, out FakeFileDialogService dialog);
        editing.StoredFileNames.Add("old.nfo");
        vm.SourcePath = @"C:\rel\movie.srr";
        vm.EnsureWorkingCopy();
        vm.SetSelection([vm.StoredFiles.First(f => f.Name == "old.nfo")]);
        dialog.PromptResult = "new.nfo";

        vm.RenameStoredFileCommand.Execute(null);

        Assert.NotNull(editing.LastRenamed);
        Assert.Equal((TestSrrEditorViewModel.DummyWorkingPath, "old.nfo", "new.nfo"), editing.LastRenamed!.Value);
        Assert.Equal("old.nfo", dialog.LastPromptInitialValue);
        Assert.Contains("new.nfo", vm.StoredFiles.Select(f => f.Name));
        Assert.Equal("new.nfo", vm.SelectedStoredFile?.Name);
    }

    [Fact]
    public void RenameStoredFile_NoOpWhenPromptCancelled()
    {
        TestSrrEditorViewModel vm = CreateVm(out FakeSrrEditingService editing, out FakeFileDialogService dialog);
        editing.StoredFileNames.Add("old.nfo");
        vm.SourcePath = @"C:\rel\movie.srr";
        vm.EnsureWorkingCopy();
        vm.SetSelection([vm.StoredFiles.First(f => f.Name == "old.nfo")]);
        dialog.PromptResult = null;

        vm.RenameStoredFileCommand.Execute(null);

        Assert.Null(editing.LastRenamed);
        Assert.Contains("old.nfo", vm.StoredFiles.Select(f => f.Name));
    }

    [Fact]
    public void RenameStoredFile_NoOpWhenNewNameEqualsOld()
    {
        TestSrrEditorViewModel vm = CreateVm(out FakeSrrEditingService editing, out FakeFileDialogService dialog);
        editing.StoredFileNames.Add("same.nfo");
        vm.SourcePath = @"C:\rel\movie.srr";
        vm.EnsureWorkingCopy();
        vm.SetSelection([vm.StoredFiles.First(f => f.Name == "same.nfo")]);
        dialog.PromptResult = "same.nfo";   // user kept the existing name

        vm.RenameStoredFileCommand.Execute(null);

        Assert.Null(editing.LastRenamed);
        Assert.DoesNotContain(nameof(FakeSrrEditingService.RenameStoredFileAsync), editing.Calls);
        Assert.Contains("same.nfo", vm.StoredFiles.Select(f => f.Name));
    }

    [Fact]
    public void MoveStoredFileUp_CallsServiceWithNegativeOffsetAndPreservesSelection()
    {
        TestSrrEditorViewModel vm = CreateVm(out FakeSrrEditingService editing, out _);
        editing.StoredFileNames.AddRange(["a.nfo", "b.sfv"]);
        vm.SourcePath = @"C:\rel\movie.srr";
        vm.EnsureWorkingCopy();
        vm.SetSelection([vm.StoredFiles.First(f => f.Name == "b.sfv")]);

        vm.MoveStoredFileUpCommand.Execute(null);

        Assert.NotNull(editing.LastMoved);
        Assert.Equal((TestSrrEditorViewModel.DummyWorkingPath, "b.sfv", -1), editing.LastMoved!.Value);
        Assert.Equal(["b.sfv", "a.nfo"], vm.StoredFiles.Select(f => f.Name));
        Assert.Equal("b.sfv", vm.SelectedStoredFile?.Name);
    }

    [Fact]
    public void MoveStoredFileDown_CallsServiceWithPositiveOffset()
    {
        TestSrrEditorViewModel vm = CreateVm(out FakeSrrEditingService editing, out _);
        editing.StoredFileNames.AddRange(["a.nfo", "b.sfv"]);
        vm.SourcePath = @"C:\rel\movie.srr";
        vm.EnsureWorkingCopy();
        vm.SetSelection([vm.StoredFiles.First(f => f.Name == "a.nfo")]);

        vm.MoveStoredFileDownCommand.Execute(null);

        Assert.NotNull(editing.LastMoved);
        Assert.Equal((TestSrrEditorViewModel.DummyWorkingPath, "a.nfo", +1), editing.LastMoved!.Value);
        Assert.Equal(["b.sfv", "a.nfo"], vm.StoredFiles.Select(f => f.Name));
        Assert.Equal("a.nfo", vm.SelectedStoredFile?.Name);
    }

    // ── Extract ─────────────────────────────────────────────

    [Fact]
    public async Task ExtractStoredFile_CallsServiceAndSetsOkStatus()
    {
        TestSrrEditorViewModel vm = CreateVm(out FakeSrrEditingService editing, out FakeFileDialogService dialog);
        editing.StoredFileNames.Add("readme.nfo");
        vm.SourcePath = @"C:\rel\movie.srr";
        vm.EnsureWorkingCopy();
        vm.SetSelection([vm.StoredFiles.First(f => f.Name == "readme.nfo")]);
        dialog.OpenFolderResult = @"D:\Output";
        editing.ExtractResult = @"D:\Output\readme.nfo";

        await vm.ExtractStoredFileCommand.ExecuteAsync(null);

        Assert.NotNull(editing.LastExtracted);
        Assert.Equal(TestSrrEditorViewModel.DummyWorkingPath, editing.LastExtracted!.Value.SrrPath);
        Assert.Equal(@"D:\Output", editing.LastExtracted.Value.OutputDir);
        Assert.Equal("readme.nfo", editing.LastExtracted.Value.StoredName);
        Assert.Equal(FieldState.Ok, vm.ManageStatus.State);
        Assert.Contains("Saved \"readme.nfo\"", vm.ManageStatus.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExtractStoredFile_WhenFolderDialogCancelled_DoesNothing()
    {
        TestSrrEditorViewModel vm = CreateVm(out FakeSrrEditingService editing, out FakeFileDialogService dialog);
        editing.StoredFileNames.Add("readme.nfo");
        vm.SourcePath = @"C:\rel\movie.srr";
        vm.EnsureWorkingCopy();
        vm.SetSelection([vm.StoredFiles.First(f => f.Name == "readme.nfo")]);
        dialog.OpenFolderResult = null;   // user cancelled

        await vm.ExtractStoredFileCommand.ExecuteAsync(null);

        Assert.DoesNotContain(nameof(FakeSrrEditingService.ExtractStoredFileAsync), editing.Calls);
        Assert.Null(editing.LastExtracted);
        Assert.Equal(FieldState.None, vm.ManageStatus.State);
    }

    [Fact]
    public void ExtractStoredFileCommand_DisabledWithoutSelection()
    {
        TestSrrEditorViewModel vm = CreateVm(out _, out _);
        vm.SetSelection([]);

        Assert.False(vm.ExtractStoredFileCommand.CanExecute(null));
    }

    [Fact]
    public async Task Reset_ClearsManageStatus()
    {
        TestSrrEditorViewModel vm = CreateVm(out FakeSrrEditingService editing, out FakeFileDialogService dialog);
        editing.StoredFileNames.Add("readme.nfo");
        vm.SourcePath = @"C:\rel\movie.srr";
        vm.EnsureWorkingCopy();
        vm.SetSelection([vm.StoredFiles.First(f => f.Name == "readme.nfo")]);
        dialog.OpenFolderResult = @"D:\Output";
        editing.ExtractResult = @"D:\Output\readme.nfo";
        await vm.ExtractStoredFileCommand.ExecuteAsync(null);
        Assert.Equal(FieldState.Ok, vm.ManageStatus.State);

        vm.Reset();

        Assert.Equal(FieldState.None, vm.ManageStatus.State);
    }

    // ── HasSelection gating ─────────────────────────────────

    [Fact]
    public void EditCommands_DisabledWithoutSelection()
    {
        TestSrrEditorViewModel vm = CreateVm(out _, out _);
        vm.SetSelection([]);

        Assert.False(vm.RemoveStoredFileCommand.CanExecute(null));
        Assert.False(vm.RenameStoredFileCommand.CanExecute(null));
        Assert.False(vm.MoveStoredFileUpCommand.CanExecute(null));
        Assert.False(vm.MoveStoredFileDownCommand.CanExecute(null));
        Assert.False(vm.ExtractStoredFileCommand.CanExecute(null));
    }

    [Fact]
    public void EditCommands_EnabledWithSingleSelection()
    {
        TestSrrEditorViewModel vm = CreateVm(out FakeSrrEditingService editing, out _);
        editing.StoredFileNames.Add("a.nfo");
        vm.SourcePath = @"C:\rel\movie.srr";
        vm.EnsureWorkingCopy();
        vm.SetSelection([vm.StoredFiles.First()]);

        Assert.True(vm.RemoveStoredFileCommand.CanExecute(null));
        Assert.True(vm.RenameStoredFileCommand.CanExecute(null));
        Assert.True(vm.MoveStoredFileUpCommand.CanExecute(null));
        Assert.True(vm.MoveStoredFileDownCommand.CanExecute(null));
        Assert.True(vm.ExtractStoredFileCommand.CanExecute(null));
    }

    // ── Multi-selection ─────────────────────────────────────

    [Fact]
    public void MultiSelection_EnablesRemoveAndExtract_ButDisablesSingleOnlyCommands()
    {
        TestSrrEditorViewModel vm = CreateVm(out FakeSrrEditingService editing, out _);
        editing.StoredFileNames.AddRange(["a.nfo", "b.sfv"]);
        vm.SourcePath = @"C:\rel\movie.srr";
        vm.EnsureWorkingCopy();

        // With a single selection the single-only commands are enabled…
        vm.SetSelection([vm.StoredFiles.First(f => f.Name == "a.nfo")]);
        Assert.True(vm.RenameStoredFileCommand.CanExecute(null));
        Assert.True(vm.MoveStoredFileUpCommand.CanExecute(null));
        Assert.True(vm.MoveStoredFileDownCommand.CanExecute(null));

        // …and adding a second selected file disables them, while Remove/Extract stay enabled.
        vm.SetSelection(vm.StoredFiles.ToList());

        Assert.Equal(2, vm.SelectedStoredFiles.Count);
        Assert.True(vm.RemoveStoredFileCommand.CanExecute(null));
        Assert.True(vm.ExtractStoredFileCommand.CanExecute(null));
        Assert.False(vm.RenameStoredFileCommand.CanExecute(null));
        Assert.False(vm.MoveStoredFileUpCommand.CanExecute(null));
        Assert.False(vm.MoveStoredFileDownCommand.CanExecute(null));
    }

    [Fact]
    public void RemoveStoredFile_RemovesAllSelectedFiles()
    {
        TestSrrEditorViewModel vm = CreateVm(out FakeSrrEditingService editing, out _);
        editing.StoredFileNames.AddRange(["a.nfo", "b.sfv", "c.txt"]);
        vm.SourcePath = @"C:\rel\movie.srr";
        vm.EnsureWorkingCopy();
        vm.SetSelection([
            vm.StoredFiles.First(f => f.Name == "a.nfo"),
            vm.StoredFiles.First(f => f.Name == "c.txt"),
        ]);

        vm.RemoveStoredFileCommand.Execute(null);

        Assert.Equal(["a.nfo", "c.txt"], editing.LastRemoved);
        Assert.Equal(["b.sfv"], vm.StoredFiles.Select(f => f.Name));
    }

    [Fact]
    public async Task ExtractStoredFile_ExtractsAllSelectedFiles()
    {
        TestSrrEditorViewModel vm = CreateVm(out FakeSrrEditingService editing, out FakeFileDialogService dialog);
        editing.StoredFileNames.AddRange(["a.nfo", "b.sfv"]);
        vm.SourcePath = @"C:\rel\movie.srr";
        vm.EnsureWorkingCopy();
        vm.SetSelection(vm.StoredFiles.ToList());
        dialog.OpenFolderResult = @"D:\Output";
        editing.ExtractResult = @"D:\Output\file";   // non-null = success for each

        await vm.ExtractStoredFileCommand.ExecuteAsync(null);

        Assert.Equal(2, editing.Extractions.Count);
        Assert.Equal(["a.nfo", "b.sfv"], editing.Extractions.Select(e => e.StoredName));
        Assert.All(editing.Extractions, e => Assert.Equal(@"D:\Output", e.OutputDir));
        Assert.All(editing.Extractions, e => Assert.Equal(TestSrrEditorViewModel.DummyWorkingPath, e.SrrPath));
        Assert.Equal(FieldState.Ok, vm.ManageStatus.State);
        Assert.Contains("Saved 2 files", vm.ManageStatus.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExtractStoredFile_WhenSomeSelectedMissing_SetsWarningStatus()
    {
        TestSrrEditorViewModel vm = CreateVm(out FakeSrrEditingService editing, out FakeFileDialogService dialog);
        editing.StoredFileNames.AddRange(["a.nfo", "b.sfv"]);
        vm.SourcePath = @"C:\rel\movie.srr";
        vm.EnsureWorkingCopy();
        vm.SetSelection(vm.StoredFiles.ToList());
        dialog.OpenFolderResult = @"D:\Output";
        editing.ExtractResultByName["a.nfo"] = @"D:\Output\a.nfo";   // saved
        editing.ExtractResultByName["b.sfv"] = null;                  // not found

        await vm.ExtractStoredFileCommand.ExecuteAsync(null);

        Assert.Equal(2, editing.Extractions.Count);
        Assert.Equal(FieldState.Warning, vm.ManageStatus.State);
    }

    [Fact]
    public async Task ExtractStoredFile_WhenAllSelectedMissing_SetsErrorStatus()
    {
        TestSrrEditorViewModel vm = CreateVm(out FakeSrrEditingService editing, out FakeFileDialogService dialog);
        editing.StoredFileNames.AddRange(["a.nfo", "b.sfv"]);
        vm.SourcePath = @"C:\rel\movie.srr";
        vm.EnsureWorkingCopy();
        vm.SetSelection(vm.StoredFiles.ToList());
        dialog.OpenFolderResult = @"D:\Output";
        editing.ExtractResult = null;   // every extract reports "not found"

        await vm.ExtractStoredFileCommand.ExecuteAsync(null);

        Assert.Equal(2, editing.Extractions.Count);
        Assert.Equal(FieldState.Error, vm.ManageStatus.State);
    }

    [Fact]
    public void SetSelection_ReplacesPreviousSelection()
    {
        TestSrrEditorViewModel vm = CreateVm(out FakeSrrEditingService editing, out _);
        editing.StoredFileNames.AddRange(["a.nfo", "b.sfv"]);
        vm.SourcePath = @"C:\rel\movie.srr";
        vm.EnsureWorkingCopy();

        vm.SetSelection(vm.StoredFiles.ToList());
        Assert.Equal(2, vm.SelectedStoredFiles.Count);

        vm.SetSelection([vm.StoredFiles.First(f => f.Name == "b.sfv")]);

        Assert.Equal(["b.sfv"], vm.SelectedStoredFiles.Select(f => f.Name));
    }

    [Fact]
    public void SetSelection_Empty_ClearsSelection_AndDisablesCommands()
    {
        TestSrrEditorViewModel vm = CreateVm(out FakeSrrEditingService editing, out _);
        editing.StoredFileNames.Add("a.nfo");
        vm.SourcePath = @"C:\rel\movie.srr";
        vm.EnsureWorkingCopy();
        vm.SetSelection(vm.StoredFiles.ToList());
        Assert.True(vm.RemoveStoredFileCommand.CanExecute(null));

        vm.SetSelection([]);

        Assert.Empty(vm.SelectedStoredFiles);
        Assert.False(vm.RemoveStoredFileCommand.CanExecute(null));
        Assert.False(vm.ExtractStoredFileCommand.CanExecute(null));
        Assert.False(vm.RenameStoredFileCommand.CanExecute(null));
        Assert.False(vm.MoveStoredFileUpCommand.CanExecute(null));
        Assert.False(vm.MoveStoredFileDownCommand.CanExecute(null));
    }

    // ── Reset ───────────────────────────────────────────────

    // ── Save ────────────────────────────────────────────────

    [Fact]
    public void Save_CopiesWorkingCopyToOutput_AndReportsSuccess()
    {
        TestSrrEditorViewModel vm = CreateVm(out _, out _);
        vm.SourcePath = @"C:\rel\movie.srr";
        vm.EnsureWorkingCopy();
        vm.OutputPath = @"C:\rel\movie (edited).srr";

        vm.Save();

        Assert.Equal(1, vm.CopyWorkingCopyToCalls);
        Assert.Equal(@"C:\rel\movie (edited).srr", vm.LastCopiedTo);
        Assert.Contains(@"C:\rel\movie (edited).srr", vm.ResultMessage, StringComparison.Ordinal);
        Assert.True(vm.ShowResult);
        Assert.False(vm.IsSaving);
    }

    [Fact]
    public void Save_WithoutWorkingCopy_ReportsFailure_AndDoesNotCopy()
    {
        TestSrrEditorViewModel vm = CreateVm(out _, out _);
        vm.OutputPath = @"C:\rel\movie (edited).srr";   // no EnsureWorkingCopy

        vm.Save();

        Assert.Equal(0, vm.CopyWorkingCopyToCalls);
        Assert.True(vm.ShowResult);
        Assert.False(string.IsNullOrEmpty(vm.ResultMessage));
        Assert.False(vm.IsSaving);
    }

    // ── Reset ───────────────────────────────────────────────

    [Fact]
    public void Reset_ClearsAllState()
    {
        TestSrrEditorViewModel vm = CreateVm(out FakeSrrEditingService editing, out _);
        editing.StoredFileNames.Add("a.nfo");
        vm.SourcePath = @"C:\rel\movie.srr";
        vm.OutputPath = @"C:\rel\movie (edited).srr";
        vm.EnsureWorkingCopy();
        vm.SetSelection([vm.StoredFiles.First()]);

        vm.Reset();

        Assert.Equal(string.Empty, vm.SourcePath);
        Assert.Equal(string.Empty, vm.OutputPath);
        // Reset clears the source status to None (an empty field shows no error).
        Assert.Equal(FieldState.None, vm.SourceStatus.State);
        Assert.Equal(FieldState.None, vm.OutputStatus.State);
        Assert.Empty(vm.StoredFiles);
        Assert.Null(vm.SelectedStoredFile);
        Assert.Empty(vm.SelectedStoredFiles);
        Assert.Empty(vm.LogEntries);
        Assert.Equal(string.Empty, vm.ResultMessage);
        Assert.False(vm.ShowResult);
    }

    [Fact]
    public void Reset_AfterEnsureWorkingCopy_RebuildsOnNextEnsure()
    {
        TestSrrEditorViewModel vm = CreateVm(out _, out _);
        vm.SourcePath = @"C:\rel\movie.srr";
        vm.EnsureWorkingCopy();

        vm.Reset();

        vm.SourcePath = @"C:\rel\movie.srr";
        vm.EnsureWorkingCopy();

        // Reset cleared the cached working-copy source, so a new copy is created.
        Assert.Equal(2, vm.CreateWorkingCopyCalls);
    }
}
