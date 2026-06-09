using ReScene.NET.Models;
using ReScene.NET.Services;
using ReScene.NET.ViewModels;
using ReScene.SRS;

namespace ReScene.NET.Tests;

/// <summary>
/// Tests the "no full movie → warn before creating" gating and the main-file status guidance in
/// the SRS creator. The creation pipeline itself is faked; only orchestration is exercised.
/// </summary>
public sealed class SRSCreatorViewModelTests : IDisposable
{
    private readonly List<string> _tempFiles = [];

    // ── Fakes ───────────────────────────────────────────────

    private sealed class FakeSrsCreationService : ISrsCreationService
    {
        public event EventHandler<SRSCreationProgressEventArgs>? Progress { add { } remove { } }
        public event EventHandler<SRSScanProgressEventArgs>? ScanProgress { add { } remove { } }

        public int Calls { get; private set; }
        public string? LastMainFile { get; private set; }

        public Task<SRSCreationResult> CreateAsync(string outputPath, string sampleFilePath, SRSCreationOptions options, CancellationToken ct)
        {
            Calls++;
            LastMainFile = options.MainFilePath;
            return Task.FromResult(new SRSCreationResult { Success = true });
        }
    }

    private sealed class FakeFileDialogService : IFileDialogService
    {
        public bool ConfirmResult { get; set; } = true;
        public int ConfirmCalls { get; private set; }

        public Task<string?> OpenFileAsync(string title, IReadOnlyList<string> filters) => Task.FromResult<string?>(null);
        public Task<IReadOnlyList<string>> OpenFilesAsync(string title, IReadOnlyList<string> filters) => Task.FromResult<IReadOnlyList<string>>([]);
        public Task<string?> SaveFileAsync(string title, string defaultExtension, IReadOnlyList<string> filters, string? defaultFileName = null) => Task.FromResult<string?>(null);
        public Task<string?> OpenFolderAsync(string title) => Task.FromResult<string?>(null);

        public Task<bool> ShowConfirmAsync(string title, string message)
        {
            ConfirmCalls++;
            return Task.FromResult(ConfirmResult);
        }

        public Task<string?> PromptForTextAsync(string title, string message, string initialValue) => Task.FromResult<string?>(null);
    }

    private sealed class FakeTempDirectoryService : ITempDirectoryService
    {
        public string CreateTempDirectory() => throw new InvalidOperationException("Temp dir should not be created in these tests.");
        public void Cleanup(string? tempDir) { }
    }

    private sealed class FakeAppSettingsService : IAppSettingsService
    {
        public event EventHandler? Changed { add { } remove { } }
        public AppSettings Load() => new();
        public void Save(AppSettings settings) { }
    }

    private static SRSCreatorViewModel CreateVm(out FakeSrsCreationService srs, out FakeFileDialogService dialog)
    {
        srs = new FakeSrsCreationService();
        dialog = new FakeFileDialogService();
        return new SRSCreatorViewModel(srs, dialog, new FakeTempDirectoryService(), new FakeAppSettingsService());
    }

    private string CreateTempFile(string ext)
    {
        string path = Path.Combine(Path.GetTempPath(), $"rescene-{Guid.NewGuid():N}{ext}");
        File.WriteAllText(path, "x");
        _tempFiles.Add(path);
        return path;
    }

    public void Dispose()
    {
        foreach (string p in _tempFiles)
        {
            try { File.Delete(p); } catch { /* best effort */ }
        }
    }

    // ── MainFileStatus / HasValidMainFile ───────────────────

    [Fact]
    public void MainFileStatus_IsInfo_WhenEmpty()
    {
        SRSCreatorViewModel vm = CreateVm(out _, out _);
        Assert.Equal(FieldState.Info, vm.MainFileStatus.State);
        Assert.False(vm.HasValidMainFile);
    }

    [Fact]
    public void MainFileStatus_IsWarning_WhenMissing()
    {
        SRSCreatorViewModel vm = CreateVm(out _, out _);
        vm.MainFilePath = @"C:\does\not\exist.mkv";
        Assert.Equal(FieldState.Warning, vm.MainFileStatus.State);
        Assert.False(vm.HasValidMainFile);
    }

    [Fact]
    public void MainFileStatus_IsOk_WhenExists()
    {
        SRSCreatorViewModel vm = CreateVm(out _, out _);
        vm.MainFilePath = CreateTempFile(".mkv");
        Assert.Equal(FieldState.Ok, vm.MainFileStatus.State);
        Assert.True(vm.HasValidMainFile);
    }

    // ── No-movie confirmation gating ────────────────────────

    [Fact]
    public async Task Create_WithoutMovie_ConfirmDeclined_DoesNotCreate()
    {
        SRSCreatorViewModel vm = CreateVm(out FakeSrsCreationService srs, out FakeFileDialogService dialog);
        vm.InputPath = @"C:\rel\sample.mkv";
        vm.OutputPath = @"C:\rel\sample.srs";
        dialog.ConfirmResult = false;   // user declines the "signature-only?" warning

        await vm.CreateSRSCommand.ExecuteAsync(null);

        Assert.Equal(1, dialog.ConfirmCalls);
        Assert.Equal(0, srs.Calls);
        Assert.False(vm.IsCreating);
    }

    [Fact]
    public async Task Create_WithoutMovie_ConfirmAccepted_Creates()
    {
        SRSCreatorViewModel vm = CreateVm(out FakeSrsCreationService srs, out FakeFileDialogService dialog);
        vm.InputPath = @"C:\rel\sample.mkv";
        vm.OutputPath = @"C:\rel\sample.srs";
        dialog.ConfirmResult = true;

        await vm.CreateSRSCommand.ExecuteAsync(null);

        Assert.Equal(1, dialog.ConfirmCalls);
        Assert.Equal(1, srs.Calls);
    }

    [Fact]
    public async Task Create_WithValidMovie_DoesNotWarn_AndPassesItToTheService()
    {
        SRSCreatorViewModel vm = CreateVm(out FakeSrsCreationService srs, out FakeFileDialogService dialog);
        string movie = CreateTempFile(".mkv");
        vm.InputPath = @"C:\rel\sample.mkv";
        vm.OutputPath = @"C:\rel\sample.srs";
        vm.MainFilePath = movie;

        await vm.CreateSRSCommand.ExecuteAsync(null);

        Assert.Equal(0, dialog.ConfirmCalls);   // a movie is set, so no warning
        Assert.Equal(1, srs.Calls);
        Assert.Equal(movie, srs.LastMainFile);  // the movie reaches the creation service
    }

    [Fact]
    public async Task Create_WithSuppressFlag_SkipsWarning_AndConsumesFlag()
    {
        SRSCreatorViewModel vm = CreateVm(out FakeSrsCreationService srs, out FakeFileDialogService dialog);
        vm.InputPath = @"C:\rel\sample.mkv";
        vm.OutputPath = @"C:\rel\sample.srs";
        vm.SuppressNoMovieConfirm = true;   // e.g. the wizard already warned

        await vm.CreateSRSCommand.ExecuteAsync(null);

        Assert.Equal(0, dialog.ConfirmCalls);
        Assert.Equal(1, srs.Calls);
        Assert.False(vm.SuppressNoMovieConfirm);   // one-shot consumed
    }
}
