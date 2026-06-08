using ReScene.NET.Helpers;
using ReScene.NET.Models;
using ReScene.NET.Services;
using ReScene.NET.ViewModels;
using ReScene.SRR;
using ReScene.SRS;

namespace ReScene.NET.Tests;

/// <summary>
/// Tests the Create-an-SRR "build a draft, then curate" facade and the build-success gating it
/// relies on. The full creation pipeline is faked; only orchestration is exercised.
/// </summary>
public sealed class CreateSrrWizardViewModelTests : IDisposable
{
    private readonly List<string> _tempPaths = [];

    // ── Fakes ───────────────────────────────────────────────

    private sealed class FakeSrrCreationService : ISrrCreationService
    {
        public event EventHandler<SRRCreationProgressEventArgs>? Progress { add { } remove { } }

        public bool Succeed { get; set; } = true;
        public int Calls { get; private set; }
        public string? LastOutputPath { get; private set; }

        public Task<SRRCreationResult> CreateFromRarAsync(string outputPath, IReadOnlyList<string> rarVolumePaths,
            IReadOnlyDictionary<string, string>? storedFiles, SRRCreationOptions options, CancellationToken ct)
            => Build(outputPath);

        public Task<SRRCreationResult> CreateFromSFVAsync(string outputPath, string sfvFilePath,
            IReadOnlyDictionary<string, string>? additionalFiles, SRRCreationOptions options, CancellationToken ct)
            => Build(outputPath);

        private Task<SRRCreationResult> Build(string outputPath)
        {
            Calls++;
            LastOutputPath = outputPath;
            return Task.FromResult(new SRRCreationResult
            {
                Success = Succeed,
                ErrorMessage = Succeed ? null : "boom",
            });
        }
    }

    private sealed class FakeSrsCreationService : ISrsCreationService
    {
        public event EventHandler<SRSCreationProgressEventArgs>? Progress { add { } remove { } }
        public event EventHandler<SRSScanProgressEventArgs>? ScanProgress { add { } remove { } }

        public Task<SRSCreationResult> CreateAsync(string outputPath, string sampleFilePath, SRSCreationOptions options, CancellationToken ct)
            => throw new InvalidOperationException("SRS creation is disabled in these tests.");
    }

    private sealed class FakeAppSettingsService : IAppSettingsService
    {
        public event EventHandler? Changed { add { } remove { } }
        public AppSettings Load() => new();
        public void Save(AppSettings settings) { }
    }

    private sealed class FakeTempDirectoryService(List<string> createdSink) : ITempDirectoryService
    {
        public string? LastCreated { get; private set; }

        public string CreateTempDirectory()
        {
            string dir = Directory.CreateTempSubdirectory("rescene-create-test-").FullName;
            LastCreated = dir;
            createdSink.Add(dir);   // tracked so the test fixture can clean it up
            return dir;
        }

        public void Cleanup(string? tempDir)
        {
            if (!string.IsNullOrEmpty(tempDir) && Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }

    private sealed class FakeFileDialogService : IFileDialogService
    {
        public Task<string?> OpenFileAsync(string title, IReadOnlyList<string> filters) => Task.FromResult<string?>(null);
        public Task<IReadOnlyList<string>> OpenFilesAsync(string title, IReadOnlyList<string> filters) => Task.FromResult<IReadOnlyList<string>>([]);
        public Task<string?> SaveFileAsync(string title, string defaultExtension, IReadOnlyList<string> filters, string? defaultFileName = null) => Task.FromResult<string?>(null);
        public Task<string?> OpenFolderAsync(string title) => Task.FromResult<string?>(null);
        public Task<bool> ShowConfirmAsync(string title, string message) => Task.FromResult(true);
        public Task<string?> PromptForTextAsync(string title, string message, string initialValue) => Task.FromResult<string?>(null);
    }

    private sealed class FakeSrrEditingService : ISrrEditingService
    {
        public List<string> StoredFileNames { get; } = [];
        public List<string> Calls { get; } = [];

        public void AddStoredFiles(string srrFilePath, IReadOnlyList<(string StoredName, string FilePath)> files) { }
        public void RemoveStoredFiles(string srrFilePath, IReadOnlyList<string> storedNames) { }
        public Task RenameStoredFileAsync(string srrPath, string oldName, string newName, CancellationToken ct = default) => Task.CompletedTask;
        public Task MoveStoredFileAsync(string srrPath, string storedName, int offset, CancellationToken ct = default) => Task.CompletedTask;

        public IReadOnlyList<StoredFileInfo> GetStoredFiles(string srrFilePath)
        {
            Calls.Add(nameof(GetStoredFiles));
            return StoredFileNames.Select(n => new StoredFileInfo(n, 0L)).ToList();
        }

        public Task<string?> ExtractStoredFileAsync(string srrFilePath, string outputDirectory, string storedName, CancellationToken ct = default)
            => Task.FromResult<string?>(null);
    }

    // ── Helpers ─────────────────────────────────────────────

    private CreateSrrWizardViewModel CreateFacade(
        out FakeSrrCreationService srr,
        out FakeSrrEditingService editing,
        out FakeTempDirectoryService temp)
    {
        srr = new FakeSrrCreationService();
        editing = new FakeSrrEditingService();
        temp = new FakeTempDirectoryService(_tempPaths);
        var fileDialog = new FakeFileDialogService();

        var creator = new CreatorViewModel(srr, new FakeSrsCreationService(), fileDialog, temp, new FakeAppSettingsService());
        // Keep the build trivial and deterministic: no sample/vobsub/fix phases, no folder scan.
        creator.AutoCreateSRS = false;
        creator.CreateVobsubSRR = false;
        creator.StoreFixRar = false;
        creator.AutoIncludeFiles = false;

        var editor = new SrrEditorViewModel(editing, fileDialog, temp);
        return new CreateSrrWizardViewModel(creator, editor, temp);
    }

    private string CreateTempSfv()
    {
        string path = Path.Combine(Path.GetTempPath(), $"rescene-{Guid.NewGuid():N}.sfv");
        File.WriteAllText(path, "movie.rar 00000000\n");
        _tempPaths.Add(path);
        return path;
    }

    public void Dispose()
    {
        foreach (string p in _tempPaths)
        {
            try
            {
                if (Directory.Exists(p))
                {
                    Directory.Delete(p, recursive: true);
                }
                else if (File.Exists(p))
                {
                    File.Delete(p);
                }
            }
            catch { /* best effort */ }
        }
    }

    // ── BuildSucceeded gating ───────────────────────────────

    [Fact]
    public async Task PrepareDraft_BuildsDraftUnderTempDir_AndSetsBuildSucceeded()
    {
        CreateSrrWizardViewModel vm = CreateFacade(out FakeSrrCreationService srr, out _, out FakeTempDirectoryService temp);
        vm.Creator.InputPath = CreateTempSfv();

        vm.PrepareDraft();
        await vm.Creator.CreateSRRCommand.ExecutionTask!;

        Assert.True(vm.Creator.BuildSucceeded);
        Assert.False(vm.Creator.SuppressOverwriteConfirm);   // consumed by the run
        Assert.Equal(1, srr.Calls);
        Assert.Equal(vm.Creator.OutputPath, srr.LastOutputPath);
        Assert.Equal(temp.LastCreated, Path.GetDirectoryName(vm.Creator.OutputPath));
        Assert.EndsWith(".srr", vm.Creator.OutputPath, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task PrepareDraft_OnBuildFailure_LeavesBuildSucceededFalse()
    {
        CreateSrrWizardViewModel vm = CreateFacade(out FakeSrrCreationService srr, out _, out _);
        srr.Succeed = false;
        vm.Creator.InputPath = CreateTempSfv();

        vm.PrepareDraft();
        await vm.Creator.CreateSRRCommand.ExecutionTask!;

        Assert.False(vm.Creator.BuildSucceeded);
    }

    // ── AdoptDraftIntoEditor ────────────────────────────────

    [Fact]
    public void AdoptDraftIntoEditor_LoadsDraftAndSuggestsSiblingOutput()
    {
        CreateSrrWizardViewModel vm = CreateFacade(out _, out FakeSrrEditingService editing, out _);
        editing.StoredFileNames.AddRange(["movie.nfo", "movie.sfv"]);
        string sfv = CreateTempSfv();
        vm.Creator.InputPath = sfv;
        vm.Creator.OutputPath = @"X:\draft\movie.srr";   // pretend the draft was built here

        vm.AdoptDraftIntoEditor();

        Assert.Equal(["movie.nfo", "movie.sfv"], vm.Editor.StoredFiles.Select(f => f.Name));
        Assert.Equal(FieldGuidance.SuggestSiblingPath(sfv, ".srr"), vm.Editor.OutputPath);
        Assert.Equal(FieldState.Info, vm.Editor.OutputStatus.State);
    }

    // ── PropertyChanged forwarding ──────────────────────────

    [Fact]
    public void CreatorChange_RaisesFacadePropertyChanged()
    {
        CreateSrrWizardViewModel vm = CreateFacade(out _, out _, out _);
        bool raised = false;
        vm.PropertyChanged += (_, e) => raised |= e.PropertyName == nameof(vm.Creator);

        vm.Creator.InputPath = "x";

        Assert.True(raised);
    }

    [Fact]
    public void EditorChange_RaisesFacadePropertyChanged()
    {
        CreateSrrWizardViewModel vm = CreateFacade(out _, out _, out _);
        bool raised = false;
        vm.PropertyChanged += (_, e) => raised |= e.PropertyName == nameof(vm.Editor);

        vm.Editor.SourcePath = "y";

        Assert.True(raised);
    }

    // ── Reset ───────────────────────────────────────────────

    [Fact]
    public async Task Reset_ClearsBothSubViewModels()
    {
        CreateSrrWizardViewModel vm = CreateFacade(out _, out FakeSrrEditingService editing, out _);
        editing.StoredFileNames.Add("movie.nfo");
        vm.Creator.InputPath = CreateTempSfv();
        vm.PrepareDraft();
        await vm.Creator.CreateSRRCommand.ExecutionTask!;
        vm.AdoptDraftIntoEditor();

        Assert.True(vm.Creator.BuildSucceeded);
        Assert.NotEmpty(vm.Editor.StoredFiles);

        vm.Reset();

        Assert.False(vm.Creator.BuildSucceeded);
        Assert.Equal(string.Empty, vm.Creator.InputPath);
        Assert.Empty(vm.Editor.StoredFiles);
        Assert.Equal(string.Empty, vm.Editor.SourcePath);
    }
}
