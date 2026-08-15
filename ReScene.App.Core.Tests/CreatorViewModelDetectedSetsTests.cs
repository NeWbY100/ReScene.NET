using System.ComponentModel;
using ReScene.App.Core.Models;
using ReScene.App.Core.Services;
using ReScene.App.Core.ViewModels;
using ReScene.SRR;
using ReScene.SRS;

namespace ReScene.App.Core.Tests;

/// <summary>
/// Accessibility-driven VM additions: the
/// <see cref="CreatorViewModel.HasDetectedSets"/> bool the folder-mode list/collapse binds to (a
/// real int→bool binding fix — Avalonia has no implicit <c>Count</c>→<c>bool</c> conversion), the
/// grammatically-correct <see cref="CreatorViewModel.DetectedSetsSummary"/> exposed as the
/// detected-sets list's automation Name, and the "Scanning release folder…" busy status announced
/// through the existing <see cref="CreatorViewModel.InputStatus"/> live region at scan start.
/// </summary>
public sealed class CreatorViewModelDetectedSetsTests : TempDirTestBase
{
    // ── Inert / stub doubles ─────────────────────────────────

    private sealed class InertSRRCreationService : ISRRCreationService
    {
        public event EventHandler<SRRCreationProgressEventArgs>? Progress { add { } remove { } }

        public Task<SRRCreationResult> CreateFromRARAsync(string outputPath, IReadOnlyList<string> rarVolumePaths,
            IReadOnlyList<StoredFileEntry>? storedFiles, SRRCreationOptions options, CancellationToken ct)
            => Task.FromResult(new SRRCreationResult { Success = true });

        public Task<SRRCreationResult> CreateFromSFVAsync(string outputPath, string sfvFilePath,
            IReadOnlyList<StoredFileEntry>? additionalFiles, SRRCreationOptions options, CancellationToken ct)
            => Task.FromResult(new SRRCreationResult { Success = true });

        public Task<SRRCreationResult> CreateFromInputsAsync(string outputPath, IReadOnlyList<string> inputFiles,
            string? rootFolder, bool storeRelativePaths, IReadOnlyList<StoredFileEntry>? additionalFiles,
            SRRCreationOptions options, CancellationToken ct)
            => Task.FromResult(new SRRCreationResult { Success = true });
    }

    private sealed class InertSRSCreationService : ISRSCreationService
    {
        public event EventHandler<SRSCreationProgressEventArgs>? Progress { add { } remove { } }
        public event EventHandler<SRSScanProgressEventArgs>? ScanProgress { add { } remove { } }

        public Task<SRSCreationResult> CreateAsync(string outputPath, string sampleFilePath, SRSCreationOptions options, CancellationToken ct)
            => Task.FromResult(new SRSCreationResult { Success = true });
    }

    private sealed class StubReleaseScanner(ReleaseScanResult result) : IReleaseScanner
    {
        public ReleaseScanResult Scan(string releaseRoot, CancellationToken ct = default) => result;
    }

    /// <summary>Blocks scanning <see cref="GatedRoot"/> until <see cref="Release"/>, so a test can
    /// observe the VM's mid-scan state (mirrors the folder-mode test's gated scanner).</summary>
    private sealed class GatedReleaseScanner : IReleaseScanner
    {
        private readonly ManualResetEventSlim _release = new(false);
        public ManualResetEventSlim Entered { get; } = new(false);
        public required string GatedRoot { get; init; }
        public ReleaseScanResult GatedResult { get; init; } = EmptyResult;

        public void Release() => _release.Set();

        public ReleaseScanResult Scan(string releaseRoot, CancellationToken ct = default)
        {
            Entered.Set();
            _release.Wait(CancellationToken.None);
            return GatedResult;
        }
    }

    private static readonly ReleaseScanResult EmptyResult = new([], [], [], [], [], []);

    private static CreatorViewModel CreateVm(IReleaseScanner scanner) =>
        new(new InertSRRCreationService(), new InertSRSCreationService(), new NoOpFileDialogService(),
            new NoOpTempDirectoryService(), new NoOpAppSettingsService(), new TestUiDispatcher(), scanner)
        {
            AutoIncludeFiles = false,
            AutoCreateSRS = false,
            CreateVobsubSRR = false,
            StoreFixRAR = false,
        };

    private string CreateFolder(string? name = null)
    {
        string root = Path.Combine(TempDir, name ?? $"release-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        return root;
    }

    // ── HasDetectedSets: flips true on a scan that finds sets, false once cleared ──

    [Fact]
    public async Task HasDetectedSets_TrueAfterScanPopulates_FalseAfterInputCleared()
    {
        string root = CreateFolder();
        string aSfv = Path.Combine(root, "a.sfv");
        var scan = new ReleaseScanResult([new ReleaseSetInput(aSfv, "a.sfv")], [], [], [], [], []);
        CreatorViewModel vm = CreateVm(new StubReleaseScanner(scan));

        Assert.False(vm.HasDetectedSets);

        vm.InputPath = root;
        await vm.LastFolderScan!;
        Assert.True(vm.HasDetectedSets);

        // Leaving folder mode clears DetectedSets — HasDetectedSets must fall back to false.
        vm.InputPath = string.Empty;
        Assert.False(vm.HasDetectedSets);
    }

    [Fact]
    public void HasDetectedSets_RaisesPropertyChanged_WhenDetectedSetsMutated()
    {
        CreatorViewModel vm = CreateVm(new StubReleaseScanner(EmptyResult));

        var raised = new List<string?>();
        ((INotifyPropertyChanged)vm).PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        vm.DetectedSets.Add(new ReleaseSetInput(@"C:\rel\a.sfv", "a.sfv"));

        Assert.Contains(nameof(CreatorViewModel.HasDetectedSets), raised);
        Assert.Contains(nameof(CreatorViewModel.DetectedSetsSummary), raised);
    }

    // ── DetectedSetsSummary: grammatically-correct 0 / 1 / N ──

    [Fact]
    public void DetectedSetsSummary_UsesCorrectGrammar_ForZeroOneMany()
    {
        CreatorViewModel vm = CreateVm(new StubReleaseScanner(EmptyResult));

        Assert.Equal("No RAR sets", vm.DetectedSetsSummary);

        vm.DetectedSets.Add(new ReleaseSetInput(@"C:\rel\a.sfv", "a.sfv"));
        Assert.Equal("1 RAR set", vm.DetectedSetsSummary);

        vm.DetectedSets.Add(new ReleaseSetInput(@"C:\rel\b.sfv", "b.sfv"));
        Assert.Equal("2 RAR sets", vm.DetectedSetsSummary);
    }

    // ── Scan-start busy status → result on completion (single live-region transition) ──

    [Fact]
    public async Task InputStatus_IsScanningInfo_AtScanStart_ThenResult_OnCompletion()
    {
        string root = CreateFolder();
        var gated = new GatedReleaseScanner { GatedRoot = root };
        CreatorViewModel vm = CreateVm(gated);

        vm.InputPath = root;
        Assert.True(gated.Entered.Wait(TimeSpan.FromSeconds(5)));

        // Busy announcement while the background scan runs.
        Assert.True(vm.IsScanning);
        Assert.Equal(FieldState.Info, vm.InputStatus.State);
        Assert.Equal("Scanning release folder…", vm.InputStatus.Message);

        // Completion overwrites it with the result — a single announced busy→result transition.
        gated.Release();
        await vm.LastFolderScan!;

        Assert.False(vm.IsScanning);
        Assert.Equal(FieldState.Ok, vm.InputStatus.State);
        Assert.NotEqual("Scanning release folder…", vm.InputStatus.Message);
    }
}
