using ReScene.App.Core.Models;
using ReScene.App.Core.Services;
using ReScene.App.Core.ViewModels;
using ReScene.Core;

namespace ReScene.App.Core.Tests;

/// <summary>
/// Verifies that <see cref="ReconstructorViewModel.ArchiveSetStatus"/> is set correctly after
/// importing an SRR: Info for a multi-set release, None for a single-set release.
/// </summary>
public class ReconstructorViewModelArchiveSetTests
{
    // ── Fakes ───────────────────────────────────────────────

    /// <summary>Inert brute-force service — never invoked during import-only tests.</summary>
    private sealed class InertBruteForceService : IBruteForceService
    {
        public event EventHandler<BruteForceProgressEventArgs>? Progress { add { } remove { } }
        public event EventHandler<BruteForceStatusChangedEventArgs>? StatusChanged { add { } remove { } }
        public event EventHandler<LogEventArgs>? LogMessage { add { } remove { } }
        public event EventHandler<FileCopyProgressEventArgs>? FileCopyProgress { add { } remove { } }
        public event EventHandler<CRCValidationProgressEventArgs>? CRCValidationProgress { add { } remove { } }
        public event EventHandler<TimestampPreservationFailedEventArgs>? TimestampPreservationFailed { add { } remove { } }

        public Task<BruteForceRunResult> RunAsync(BruteForceOptions options, CancellationToken cancellationToken = default)
            => Task.FromResult(new BruteForceRunResult(true, null));
    }

    /// <summary>Dispatcher that runs everything inline so tests need no UI thread.</summary>
    private sealed class InlineUiDispatcher : IUiDispatcher
    {
        public void Invoke(Action action) => action();
        public void Post(Action action) => action();
        public void Post(Action action, UiDispatcherPriority priority) => action();
        public bool CheckAccess() => true;
    }

    /// <summary>
    /// Dialog service that returns <paramref name="fixturePath"/> from <see cref="OpenFileAsync"/> (first
    /// call only) and behaves as no-op for everything else.
    /// </summary>
    private sealed class FixtureDialogService(string fixturePath) : NoOpFileDialogService
    {
        private int _openFileCalls;

        public override Task<string?> OpenFileAsync(string title, IReadOnlyList<string> filters, string? initialPath = null)
        {
            if (_openFileCalls++ == 0)
            {
                return Task.FromResult<string?>(fixturePath);
            }
            return Task.FromResult<string?>(null);
        }
    }

    /// <summary>
    /// Dialog service that returns each of <paramref name="paths"/> in order from successive
    /// <see cref="OpenFileAsync"/> calls (then <c>null</c> once exhausted) — for tests that import
    /// more than one SRR into the same view model instance.
    /// </summary>
    private sealed class SequentialFixtureDialogService(params string[] paths) : NoOpFileDialogService
    {
        private int _index;

        public override Task<string?> OpenFileAsync(string title, IReadOnlyList<string> filters, string? initialPath = null) =>
            Task.FromResult(_index < paths.Length ? paths[_index++] : null);
    }

    /// <summary>
    /// <see cref="ITempDirectoryService"/> that creates real temp directories and records every
    /// create/cleanup so a test can assert the SFV extract is tracked and released.
    /// </summary>
    private sealed class RecordingTempDirectoryService : ITempDirectoryService
    {
        public List<string> Created { get; } = [];
        public List<string?> Cleaned { get; } = [];

        public string CreateTempDirectory()
        {
            string dir = Path.Combine(Path.GetTempPath(), "ReScene.App.Core.Tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            Created.Add(dir);
            return dir;
        }

        public void Cleanup(string? tempDir)
        {
            Cleaned.Add(tempDir);
            if (!string.IsNullOrEmpty(tempDir) && Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }

    private static ReconstructorViewModel CreateVm(string fixturePath, ITempDirectoryService? tempDir = null)
    {
        string srrPath = Path.Combine(AppContext.BaseDirectory, "TestData", fixturePath);
        Assert.True(File.Exists(srrPath), $"Fixture not found: {srrPath}");

        return new ReconstructorViewModel(
            new InertBruteForceService(),
            new FixtureDialogService(srrPath),
            uiDispatcher: new InlineUiDispatcher(),
            timerFactory: new TestUiTimerFactory(),
            settingsService: null,
            tempDir: tempDir);
    }

    private static async Task ImportAsync(ReconstructorViewModel vm) => await vm.ImportSRRCommand.ExecuteAsync(null);

    // ── Tests ───────────────────────────────────────────────

    [Fact]
    public async Task ArchiveSetStatus_MultipleSets_ShowsInfo()
    {
        ReconstructorViewModel vm = CreateVm(
            Path.Combine("cleanup_script", "007.A.View.To.A.Kill.1985.UE.iNTERNAL.DVDRip.XviD-iNCiTE.fine_2cd.srr"));

        await ImportAsync(vm);

        Assert.Equal(FieldState.Info, vm.ArchiveSetStatus.State);
        Assert.Contains("2 archive sets", vm.ArchiveSetStatus.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ArchiveSetStatus_SingleSet_IsNone()
    {
        ReconstructorViewModel vm = CreateVm("store_little.srr");

        await ImportAsync(vm);

        Assert.Equal(FieldState.None, vm.ArchiveSetStatus.State);
    }

    [Fact]
    public async Task ImportSRR_ExtractsStoredSfvToTrackedTemp_AndCleanupDeletesIt()
    {
        var temp = new RecordingTempDirectoryService();
        ReconstructorViewModel vm = CreateVm(
            Path.Combine("cleanup_script", "007.A.View.To.A.Kill.1985.UE.iNTERNAL.DVDRip.XviD-iNCiTE.fine_2cd.srr"),
            temp);

        await ImportAsync(vm);

        // The stored SFV is extracted into a directory obtained from the temp service (not a raw
        // %TEMP% path), and that directory survives the import because VerificationPath points into it.
        string sfvDir = Assert.Single(temp.Created);
        Assert.False(string.IsNullOrEmpty(vm.VerificationPath));
        Assert.StartsWith(sfvDir, vm.VerificationPath, StringComparison.Ordinal);
        Assert.True(Directory.Exists(sfvDir));

        vm.Cleanup();

        // Shutdown cleanup deletes it — no temp directory leaks per SRR import.
        Assert.Contains(sfvDir, temp.Cleaned);
        Assert.False(Directory.Exists(sfvDir));
    }

    [Fact]
    public async Task ImportSRR_NoStoredFiles_RetiresPreviousAutoExtractedSfv()
    {
        string sfvSrrPath = Path.Combine(AppContext.BaseDirectory, "TestData",
            "cleanup_script", "007.A.View.To.A.Kill.1985.UE.iNTERNAL.DVDRip.XviD-iNCiTE.fine_2cd.srr");
        string noStoredFilesSrrPath = Path.Combine(AppContext.BaseDirectory, "TestData", "store_little.srr");
        Assert.True(File.Exists(sfvSrrPath), $"Fixture not found: {sfvSrrPath}");
        Assert.True(File.Exists(noStoredFilesSrrPath), $"Fixture not found: {noStoredFilesSrrPath}");

        var temp = new RecordingTempDirectoryService();
        var vm = new ReconstructorViewModel(
            new InertBruteForceService(),
            new SequentialFixtureDialogService(sfvSrrPath, noStoredFilesSrrPath),
            uiDispatcher: new InlineUiDispatcher(),
            timerFactory: new TestUiTimerFactory(),
            settingsService: null,
            tempDir: temp);

        // Import A: has an embedded SFV, so it auto-extracts and VerificationPath points into it.
        await ImportAsync(vm);
        string sfvDir = Assert.Single(temp.Created);
        Assert.False(string.IsNullOrEmpty(vm.VerificationPath));

        // Import B: no stored files at all. B must not be verified against A's stale SFV — the
        // previous auto-extracted SFV is retired (VerificationPath cleared, temp dir deleted) even
        // though B itself has nothing to extract.
        await ImportAsync(vm);

        Assert.Equal(string.Empty, vm.VerificationPath);
        Assert.Null(vm.SfvTempDirForTest);
        Assert.Contains(sfvDir, temp.Cleaned);
        Assert.False(Directory.Exists(sfvDir));
    }
}
