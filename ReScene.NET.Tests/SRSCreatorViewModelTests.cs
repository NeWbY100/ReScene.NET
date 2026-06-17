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

    private sealed class FakeFileDialogService : NoOpFileDialogService
    {
        public bool ConfirmResult { get; set; } = true;
        public int ConfirmCalls { get; private set; }

        public override Task<bool> ShowConfirmAsync(string title, string message)
        {
            ConfirmCalls++;
            return Task.FromResult(ConfirmResult);
        }

        public override bool Confirm(string title, string message) => true;
    }

    private static SRSCreatorViewModel CreateVm(out FakeSrsCreationService srs, out FakeFileDialogService dialog)
    {
        srs = new FakeSrsCreationService();
        dialog = new FakeFileDialogService();
        return new SRSCreatorViewModel(srs, dialog, new NoOpTempDirectoryService(), new NoOpAppSettingsService());
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

    // ── CanExecute notification wiring ──────────────────────

    [Fact]
    public void IsISOSource_Change_RaisesCreateCommandCanExecuteChanged()
    {
        // Regression: IsISOSource gates CanCreateSRS (the ISO branch requires a
        // selected media file), but it lacked [NotifyCanExecuteChangedFor(
        // nameof(CreateSRSCommand))], so toggling it left the Create button's
        // enabled state stale in the UI.
        SRSCreatorViewModel vm = CreateVm(out _, out _);
        bool raised = false;
        vm.CreateSRSCommand.CanExecuteChanged += (_, _) => raised = true;

        vm.IsISOSource = true;

        Assert.True(raised, "Toggling IsISOSource must re-evaluate CreateSRSCommand.CanExecute.");
    }

    [Fact]
    public void CanCreateSRS_IsoSourceWithoutSelection_IsFalse_ThenTrueWhenSelected()
    {
        SRSCreatorViewModel vm = CreateVm(out _, out _);
        vm.InputPath = @"C:\rel\disc.iso";
        vm.OutputPath = @"C:\rel\sample.srs";
        vm.IsISOSource = true;

        Assert.False(vm.CreateSRSCommand.CanExecute(null)); // no media member selected yet

        vm.SelectedISOMediaFile = "VIDEO_TS/VTS_01_1.VOB";

        Assert.True(vm.CreateSRSCommand.CanExecute(null));
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
