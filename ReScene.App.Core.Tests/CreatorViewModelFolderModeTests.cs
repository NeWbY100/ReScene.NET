using ReScene.App.Core.Models;
using ReScene.App.Core.Services;
using ReScene.App.Core.ViewModels;
using ReScene.SRR;
using ReScene.SRS;

namespace ReScene.App.Core.Tests;

/// <summary>
/// Test matrix for folder-mode input handling on <see cref="CreatorViewModel"/> (see
/// docs/superpowers/plans/2026-07-19-multiset-srr-creation.md) — the generation-guarded background
/// release scan (mirroring <c>InspectorViewModel</c>'s <c>_loadGeneration</c> house pattern), the
/// collections/status it populates, the OutputPath auto-vs-user tracking, music-only gating, and
/// the folder <c>Create</c> branch that calls <see cref="ISRRCreationService.CreateFromInputsAsync"/>.
/// File-mode behavior is covered by <see cref="CreatorViewModelTests"/>; this file only regression-
/// checks that file mode still takes the old single-SFV path.
/// </summary>
public sealed class CreatorViewModelFolderModeTests : TempDirTestBase
{
    // ── Fakes ───────────────────────────────────────────────

    private sealed class FakeSRRCreationService : ISRRCreationService
    {
        public event EventHandler<SRRCreationProgressEventArgs>? Progress { add { } remove { } }

        public bool Succeed { get; set; } = true;

        public string? LastMethod { get; private set; }
        public int InputsCalls { get; private set; }
        public string? LastOutputPath { get; private set; }
        public IReadOnlyList<string>? LastInputFiles { get; private set; }
        public string? LastRootFolder { get; private set; }
        public bool? LastStoreRelativePaths { get; private set; }
        public IReadOnlyList<StoredFileEntry>? LastAdditionalFiles { get; private set; }

        public Task<SRRCreationResult> CreateFromRARAsync(string outputPath, IReadOnlyList<string> rarVolumePaths,
            IReadOnlyList<StoredFileEntry>? storedFiles, SRRCreationOptions options, CancellationToken ct)
        {
            LastMethod = "RAR";
            LastOutputPath = outputPath;
            LastAdditionalFiles = storedFiles;
            return Build();
        }

        public Task<SRRCreationResult> CreateFromSFVAsync(string outputPath, string sfvFilePath,
            IReadOnlyList<StoredFileEntry>? additionalFiles, SRRCreationOptions options, CancellationToken ct)
        {
            LastMethod = "SFV";
            LastOutputPath = outputPath;
            LastAdditionalFiles = additionalFiles;
            return Build();
        }

        public Task<SRRCreationResult> CreateFromInputsAsync(string outputPath, IReadOnlyList<string> inputFiles,
            string? rootFolder, bool storeRelativePaths, IReadOnlyList<StoredFileEntry>? additionalFiles,
            SRRCreationOptions options, CancellationToken ct)
        {
            LastMethod = "Inputs";
            InputsCalls++;
            LastOutputPath = outputPath;
            LastInputFiles = inputFiles;
            LastRootFolder = rootFolder;
            LastStoreRelativePaths = storeRelativePaths;
            LastAdditionalFiles = additionalFiles;
            return Build();
        }

        private Task<SRRCreationResult> Build() => Task.FromResult(new SRRCreationResult
        {
            Success = Succeed,
            ErrorMessage = Succeed ? null : "boom",
        });
    }

    private sealed class FakeSRSCreationService : ISRSCreationService
    {
        public event EventHandler<SRSCreationProgressEventArgs>? Progress { add { } remove { } }
        public event EventHandler<SRSScanProgressEventArgs>? ScanProgress { add { } remove { } }

        public Task<SRSCreationResult> CreateAsync(string outputPath, string sampleFilePath, SRSCreationOptions options, CancellationToken ct)
            => Task.FromResult(new SRSCreationResult { Success = true, SRSFileSize = 1 });
    }

    /// <summary>Returns the same canned result for every root — good enough for tests that don't
    /// care about concurrency, only about how the VM applies a completed scan.</summary>
    private sealed class StubReleaseScanner(ReleaseScanResult result) : IReleaseScanner
    {
        public int Calls { get; private set; }

        public ReleaseScanResult Scan(string releaseRoot, CancellationToken ct = default)
        {
            Calls++;
            return result;
        }
    }

    /// <summary>
    /// Blocks (via a manual-reset event) when scanning <see cref="GatedRoot"/>, so a test can
    /// observe the VM mid-scan before releasing it; scanning any OTHER root returns
    /// <see cref="OtherResult"/> immediately — lets a single scanner instance stand in for both
    /// "the gated scan" and "a later scan that outruns it" (mirrors GatedCompareService in
    /// FileCompareViewModelMKVTests).
    /// </summary>
    private sealed class GatedReleaseScanner : IReleaseScanner
    {
        private readonly ManualResetEventSlim _release = new(false);
        public ManualResetEventSlim Entered { get; } = new(false);

        public required string GatedRoot { get; init; }
        public ReleaseScanResult GatedResult { get; init; } = EmptyResult;
        public ReleaseScanResult OtherResult { get; init; } = EmptyResult;

        public void Release() => _release.Set();

        public ReleaseScanResult Scan(string releaseRoot, CancellationToken ct = default)
        {
            if (!string.Equals(releaseRoot, GatedRoot, StringComparison.OrdinalIgnoreCase))
            {
                return OtherResult;
            }

            Entered.Set();
            _release.Wait(CancellationToken.None);
            return GatedResult;
        }
    }

    /// <summary>Returns a different canned result per root — for tests exercising two distinct
    /// folders (e.g. an errored root followed by a good one) without needing gating.</summary>
    private sealed class MultiRootReleaseScanner(Dictionary<string, ReleaseScanResult> resultsByRoot) : IReleaseScanner
    {
        public ReleaseScanResult Scan(string releaseRoot, CancellationToken ct = default) => resultsByRoot[releaseRoot];
    }

    /// <summary>Throws an UNEXPECTED (non-OCE) exception from <c>Scan</c> — the very fault
    /// <c>RarProofInspector.Inspect</c>'s narrow IOException/UnauthorizedAccessException catch (or a
    /// RAR-parser fault) would let escape, to prove the catch-all doesn't strand the busy state.</summary>
    private sealed class ThrowingReleaseScanner(Exception toThrow) : IReleaseScanner
    {
        public ReleaseScanResult Scan(string releaseRoot, CancellationToken ct = default) => throw toThrow;
    }

    private static readonly ReleaseScanResult EmptyResult = new([], [], [], [], [], []);

    // ── Helpers ─────────────────────────────────────────────

    private static CreatorViewModel CreateVm(IReleaseScanner scanner, out FakeSRRCreationService srr)
    {
        srr = new FakeSRRCreationService();
        return new CreatorViewModel(srr, new FakeSRSCreationService(), new NoOpFileDialogService(),
            new NoOpTempDirectoryService(), new NoOpAppSettingsService(), new TestUiDispatcher(), scanner)
        {
            // File-mode's own disk scan/materialization phases are irrelevant here and would
            // otherwise touch real disk when a test transitions InputPath to a file; keep it off.
            AutoIncludeFiles = false,
            AutoCreateSRS = false,
            CreateVobsubSRR = false,
            StoreFixRAR = false,
        };
    }

    private string CreateFolder(string? name = null)
    {
        string root = Path.Combine(TempDir, name ?? $"release-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        return root;
    }

    private static string Touch(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "x");
        return path;
    }

    // ── 1. Scan populates collections + status ───────────────

    [Fact]
    public async Task FolderInput_PopulatesDetectedSetsStoredFilesSamplesSubs_AndOkStatus()
    {
        string root = CreateFolder();
        string aSfv = Path.Combine(root, "a.sfv");
        string bSfv = Path.Combine(root, "CD2", "b.sfv");
        string nfo = Path.Combine(root, "release.nfo");
        string sample = Path.Combine(root, "Sample", "movie.sample.mkv");
        string subSfv = Path.Combine(root, "Subs", "subs.sfv");

        var scan = new ReleaseScanResult(
            MainSets: [new ReleaseSetInput(aSfv, "a.sfv"), new ReleaseSetInput(bSfv, "CD2/b.sfv")],
            SampleFiles: [sample],
            SubtitleSfvs: [subSfv],
            StoredFiles: [nfo],
            MusicSfvs: [],
            Warnings: []);

        CreatorViewModel vm = CreateVm(new StubReleaseScanner(scan), out _);

        vm.InputPath = root;
        await vm.LastFolderScan!;

        Assert.Equal(2, vm.DetectedSets.Count);
        Assert.Equal(aSfv, vm.DetectedSets[0].SfvOrRarPath);
        Assert.Equal(bSfv, vm.DetectedSets[1].SfvOrRarPath);

        Assert.Single(vm.StoredFiles);
        Assert.Equal(nfo, vm.StoredFiles[0].FullPath);
        Assert.Equal("release.nfo", vm.StoredFiles[0].StoredName);

        Assert.Equal([sample], vm.ExtraSampleFiles);
        Assert.Equal([subSfv], vm.ExtraSubtitleSfvFiles);

        Assert.Equal(FieldState.Ok, vm.InputStatus.State);
        // The set-count segment reuses DetectedSetsSummary's grammar ("2 RAR sets"), consistent
        // with the detected-sets list's automation Name — no "(s)" pluralization noise.
        Assert.Contains("2 RAR sets", vm.InputStatus.Message, StringComparison.Ordinal);
        Assert.Contains("1 sample(s)", vm.InputStatus.Message, StringComparison.Ordinal);
        Assert.Contains("1 stored file(s)", vm.InputStatus.Message, StringComparison.Ordinal);
    }

    // ── 2. Stale-scan-discard ─────────────────────────────────

    [Fact]
    public async Task StaleScan_Discarded_WhenNewerInputSupersedes()
    {
        string rootA = CreateFolder("A");
        string rootB = CreateFolder("B");
        string aSet = Path.Combine(rootA, "a.sfv");
        string bSet = Path.Combine(rootB, "b.sfv");

        var gated = new GatedReleaseScanner
        {
            GatedRoot = rootA,
            GatedResult = new ReleaseScanResult([new ReleaseSetInput(aSet, "a.sfv")], [], [], [], [], []),
            OtherResult = new ReleaseScanResult([new ReleaseSetInput(bSet, "b.sfv")], [], [], [], [], []),
        };
        CreatorViewModel vm = CreateVm(gated, out _);

        vm.InputPath = rootA;
        Task scanA = vm.LastFolderScan!;
        Assert.True(gated.Entered.Wait(TimeSpan.FromSeconds(5)));

        vm.InputPath = rootB;
        Task scanB = vm.LastFolderScan!;
        await scanB;

        Assert.Single(vm.DetectedSets);
        Assert.Equal(bSet, vm.DetectedSets[0].SfvOrRarPath);

        // Unblock A's stuck call and let its (now-stale) completion run its course; it must not
        // resurrect A's state over B's.
        gated.Release();
        await scanA;

        Assert.Single(vm.DetectedSets);
        Assert.Equal(bSet, vm.DetectedSets[0].SfvOrRarPath);
    }

    // ── 3. IsScanning lifecycle + Create gating while scanning ──

    [Fact]
    public async Task IsScanning_TrueWhileGated_FalseAfter_AndCreateDisabledWhileScanning()
    {
        string root = CreateFolder();
        var gated = new GatedReleaseScanner { GatedRoot = root };
        CreatorViewModel vm = CreateVm(gated, out _);
        vm.OutputPath = Path.Combine(TempDir, "out.srr");

        vm.InputPath = root;
        Assert.True(gated.Entered.Wait(TimeSpan.FromSeconds(5)));

        Assert.True(vm.IsScanning);
        Assert.False(vm.CreateSRRCommand.CanExecute(null));

        gated.Release();
        await vm.LastFolderScan!; // deterministically wait for the dispatcher-posted apply

        Assert.False(vm.IsScanning);
        Assert.True(vm.CreateSRRCommand.CanExecute(null));
    }

    // ── 4. Music-only folder ──────────────────────────────────

    [Fact]
    public async Task MusicOnlyFolder_SetsErrorStatus_AndCreateCannotExecute()
    {
        string root = CreateFolder();
        string musicSfv = Path.Combine(root, "album.sfv");
        var scan = new ReleaseScanResult([], [], [], [], [musicSfv], ["Rescued as a music set (unsupported until Spec 2): " + musicSfv]);
        CreatorViewModel vm = CreateVm(new StubReleaseScanner(scan), out _);
        vm.OutputPath = Path.Combine(TempDir, "out.srr");

        vm.InputPath = root;
        await vm.LastFolderScan!;

        Assert.Equal(FieldState.Error, vm.InputStatus.State);
        Assert.False(vm.CreateSRRCommand.CanExecute(null));
    }

    // ── 5-7. OutputPath auto-vs-user tracking ─────────────────

    [Fact]
    public async Task OutputPath_AutoFilledOnScan_WhenBlank()
    {
        string root = CreateFolder("My.Release-GRP");
        CreatorViewModel vm = CreateVm(new StubReleaseScanner(EmptyResult), out _);

        vm.InputPath = root;
        await vm.LastFolderScan!;

        Assert.Equal(Path.Combine(TempDir, "My.Release-GRP.srr"), vm.OutputPath);
    }

    [Fact]
    public async Task OutputPath_ReplacedOnRescan_WhileStillAuto()
    {
        string rootA = CreateFolder("Release.A-GRP");
        string rootB = CreateFolder("Release.B-GRP");
        var scanner = new StubReleaseScanner(EmptyResult);
        CreatorViewModel vm = CreateVm(scanner, out _);

        vm.InputPath = rootA;
        await vm.LastFolderScan!;
        Assert.Equal(Path.Combine(TempDir, "Release.A-GRP.srr"), vm.OutputPath);

        vm.InputPath = rootB;
        await vm.LastFolderScan!;
        Assert.Equal(Path.Combine(TempDir, "Release.B-GRP.srr"), vm.OutputPath);
    }

    [Fact]
    public async Task OutputPath_PreservedAfterUserEdit_NotReplacedOnRescan()
    {
        string rootA = CreateFolder("Release.A-GRP");
        string rootB = CreateFolder("Release.B-GRP");
        var scanner = new StubReleaseScanner(EmptyResult);
        CreatorViewModel vm = CreateVm(scanner, out _);

        vm.InputPath = rootA;
        await vm.LastFolderScan!;

        string userChosen = Path.Combine(TempDir, "my-own-name.srr");
        vm.OutputPath = userChosen;

        vm.InputPath = rootB;
        await vm.LastFolderScan!;

        Assert.Equal(userChosen, vm.OutputPath);
    }

    // ── 8, 15, 17. Folder Create branch service args ──────────

    [Fact]
    public async Task FolderCreate_CallsCreateFromInputsAsync_WithOrderedPathsRootStoreRelativeAndStoredFiles()
    {
        string root = CreateFolder("Some.Release-GRP");
        string aSfv = Path.Combine(root, "a.sfv");
        string bSfv = Path.Combine(root, "CD2", "b.sfv");
        string nfo = Path.Combine(root, "release.nfo");

        var scan = new ReleaseScanResult(
            [new ReleaseSetInput(aSfv, "a.sfv"), new ReleaseSetInput(bSfv, "CD2/b.sfv")],
            [], [], [nfo], [], []);
        CreatorViewModel vm = CreateVm(new StubReleaseScanner(scan), out FakeSRRCreationService srr);

        vm.InputPath = root;
        await vm.LastFolderScan!;
        string outputPath = Path.Combine(TempDir, "out.srr");
        vm.OutputPath = outputPath;

        await vm.CreateSRRCommand.ExecuteAsync(null);

        Assert.Equal(1, srr.InputsCalls);
        Assert.Equal("Inputs", srr.LastMethod);
        Assert.Equal(outputPath, srr.LastOutputPath);
        Assert.Equal([aSfv, bSfv], srr.LastInputFiles);
        Assert.Equal(root, srr.LastRootFolder);
        Assert.True(srr.LastStoreRelativePaths);
        Assert.NotNull(srr.LastAdditionalFiles);
        Assert.Single(srr.LastAdditionalFiles!);
        Assert.Equal("release.nfo", srr.LastAdditionalFiles![0].StoredName);
        Assert.Equal(nfo, srr.LastAdditionalFiles![0].FullPath);
        Assert.True(vm.BuildSucceeded);
    }

    [Fact]
    public async Task StorageOnlyTree_CreateEnabled_HeaderOnlyWriterCallCaptured()
    {
        // No RAR sets and no music — Create should still be enabled and build a header-only SRR
        // from the stored files alone.
        string root = CreateFolder();
        string nfo = Path.Combine(root, "release.nfo");
        var scan = new ReleaseScanResult([], [], [], [nfo], [], []);
        CreatorViewModel vm = CreateVm(new StubReleaseScanner(scan), out FakeSRRCreationService srr);

        vm.InputPath = root;
        await vm.LastFolderScan!;
        vm.OutputPath = Path.Combine(TempDir, "out.srr");

        Assert.True(vm.CreateSRRCommand.CanExecute(null));

        await vm.CreateSRRCommand.ExecuteAsync(null);

        Assert.Equal("Inputs", srr.LastMethod);
        Assert.Empty(srr.LastInputFiles!);
        Assert.Single(srr.LastAdditionalFiles!);
    }

    [Fact]
    public async Task MixedMainAndMusicTree_MusicExcludedFromCreateInputs()
    {
        string root = CreateFolder();
        string mainSfv = Path.Combine(root, "movie.sfv");
        string musicSfv = Path.Combine(root, "OST", "album.sfv");

        // Not music-only (a main set exists), so the music SFV is simply absent from MainSets —
        // DetectedSets/the Create call must never include it.
        var scan = new ReleaseScanResult([new ReleaseSetInput(mainSfv, "movie.sfv")], [], [], [], [musicSfv], []);
        CreatorViewModel vm = CreateVm(new StubReleaseScanner(scan), out FakeSRRCreationService srr);

        vm.InputPath = root;
        await vm.LastFolderScan!;
        vm.OutputPath = Path.Combine(TempDir, "out.srr");

        Assert.Single(vm.DetectedSets);
        Assert.DoesNotContain(vm.DetectedSets, s => s.SfvOrRarPath == musicSfv);

        await vm.CreateSRRCommand.ExecuteAsync(null);

        Assert.Equal([mainSfv], srr.LastInputFiles);
    }

    // ── 9. File-mode regression ────────────────────────────────

    [Fact]
    public async Task FileModeCreate_StillCallsCreateFromSFVAsync_Regression()
    {
        string root = CreateFolder();
        string sfv = Touch(Path.Combine(root, "movie.sfv"));
        File.WriteAllText(sfv, "movie.rar 00000000\n");
        Touch(Path.Combine(root, "movie.rar"));

        CreatorViewModel vm = CreateVm(new StubReleaseScanner(EmptyResult), out FakeSRRCreationService srr);

        vm.InputPath = sfv;
        vm.OutputPath = Path.Combine(TempDir, "out.srr");

        await vm.CreateSRRCommand.ExecuteAsync(null);

        Assert.Equal("SFV", srr.LastMethod);
        Assert.Equal(0, srr.InputsCalls);
    }

    // ── 9b. IsFolderMode reflects the folder/file state with change notification, so the view can
    //        disable the "Store fix RAR" checkbox in folder mode. ──

    [Fact]
    public async Task IsFolderMode_TracksFolderVsFileInput_WithChangeNotification()
    {
        string root = CreateFolder();
        string aSfv = Path.Combine(root, "a.sfv");
        var scan = new ReleaseScanResult([new ReleaseSetInput(aSfv, "a.sfv")], [], [], [], [], []);
        CreatorViewModel vm = CreateVm(new StubReleaseScanner(scan), out _);

        var changes = new List<bool>();
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(CreatorViewModel.IsFolderMode))
            {
                changes.Add(vm.IsFolderMode);
            }
        };

        Assert.False(vm.IsFolderMode); // fresh VM: file mode

        vm.InputPath = root;
        await vm.LastFolderScan!;
        Assert.True(vm.IsFolderMode);
        Assert.Contains(true, changes); // entering folder mode raised the notification

        // Switching to a plain file leaves folder mode; the checkbox re-enables.
        string sfv = Touch(Path.Combine(TempDir, "single.sfv"));
        vm.InputPath = sfv;
        Assert.False(vm.IsFolderMode);
        Assert.Contains(false, changes); // leaving folder mode raised the notification too
    }

    // ── 10. Every input-change kind discards a stale folder scan ──

    [Theory]
    [InlineData("file")]
    [InlineData("blank")]
    [InlineData("nonexistent")]
    public async Task InputChange_ToNonFolder_DiscardsStaleFolderScan(string kind)
    {
        string root = CreateFolder();
        string set = Path.Combine(root, "a.sfv");
        var gated = new GatedReleaseScanner
        {
            GatedRoot = root,
            GatedResult = new ReleaseScanResult([new ReleaseSetInput(set, "a.sfv")], [], [], [], [], []),
        };
        CreatorViewModel vm = CreateVm(gated, out _);

        vm.InputPath = root;
        Task scanA = vm.LastFolderScan!;
        Assert.True(gated.Entered.Wait(TimeSpan.FromSeconds(5)));

        string newInput = kind switch
        {
            "file" => Touch(Path.Combine(TempDir, "single.sfv")),
            "blank" => string.Empty,
            _ => Path.Combine(TempDir, "does-not-exist"),
        };
        vm.InputPath = newInput;

        gated.Release();
        await scanA;

        Assert.Empty(vm.DetectedSets);
        Assert.False(vm.IsScanning);
    }

    // ── 11. CanCreate false while scanning / for music-only, with notification ──

    [Fact]
    public async Task CanCreate_False_WhileScanning_WithCommandNotification()
    {
        string root = CreateFolder();
        var gated = new GatedReleaseScanner { GatedRoot = root };
        CreatorViewModel vm = CreateVm(gated, out _);
        vm.OutputPath = Path.Combine(TempDir, "out.srr");

        int notifications = 0;
        vm.CreateSRRCommand.CanExecuteChanged += (_, _) => notifications++;

        vm.InputPath = root;
        Assert.True(gated.Entered.Wait(TimeSpan.FromSeconds(5)));
        Assert.False(vm.CreateSRRCommand.CanExecute(null));
        Assert.True(notifications > 0);

        int afterScanStart = notifications;
        gated.Release();
        await vm.LastFolderScan!; // deterministically wait for the dispatcher-posted apply

        Assert.True(notifications > afterScanStart);
        Assert.True(vm.CreateSRRCommand.CanExecute(null));
    }

    [Fact]
    public async Task CanCreate_False_ForMusicOnly_WithCommandNotification()
    {
        string root = CreateFolder();
        string musicSfv = Path.Combine(root, "album.sfv");
        var scan = new ReleaseScanResult([], [], [], [], [musicSfv], []);
        CreatorViewModel vm = CreateVm(new StubReleaseScanner(scan), out _);
        vm.OutputPath = Path.Combine(TempDir, "out.srr");

        int notifications = 0;
        vm.CreateSRRCommand.CanExecuteChanged += (_, _) => notifications++;

        vm.InputPath = root;
        await vm.LastFolderScan!;

        Assert.False(vm.CreateSRRCommand.CanExecute(null));
        Assert.True(notifications > 0);
    }

    // ── 12-13. Result paths absolute; StoredNames root-relative ──

    [Fact]
    public async Task DetectedSets_And_StoredFiles_ResultPaths_AreAbsolute()
    {
        string root = CreateFolder();
        string set = Path.Combine(root, "CD1", "a.sfv");
        string stored = Path.Combine(root, "release.nfo");
        var scan = new ReleaseScanResult([new ReleaseSetInput(set, "CD1/a.sfv")], [], [], [stored], [], []);
        CreatorViewModel vm = CreateVm(new StubReleaseScanner(scan), out _);

        vm.InputPath = root;
        await vm.LastFolderScan!;

        Assert.True(Path.IsPathRooted(vm.DetectedSets[0].SfvOrRarPath));
        Assert.True(Path.IsPathRooted(vm.StoredFiles[0].FullPath));
    }

    [Fact]
    public async Task StoredFiles_StoredName_IsRootRelative_WithForwardSlashes()
    {
        string root = CreateFolder();
        string nested = Path.Combine(root, "Proof", "shot.jpg");
        var scan = new ReleaseScanResult([], [], [], [nested], [], []);
        CreatorViewModel vm = CreateVm(new StubReleaseScanner(scan), out _);

        vm.InputPath = root;
        await vm.LastFolderScan!;

        Assert.Equal("Proof/shot.jpg", vm.StoredFiles[0].StoredName);
    }

    // ── 14. Warnings: status shows a count, log carries every one, in order ──

    [Fact]
    public async Task AllWarnings_LoggedInOrder_StatusShowsCount()
    {
        string root = CreateFolder();
        List<string> warnings = ["first warning", "second warning", "third warning"];
        var scan = new ReleaseScanResult([], [], [], [], [], warnings);
        CreatorViewModel vm = CreateVm(new StubReleaseScanner(scan), out _);

        vm.InputPath = root;
        await vm.LastFolderScan!;

        Assert.Contains("3 warning(s)", vm.InputStatus.Message, StringComparison.Ordinal);

        List<string> loggedWarnings = [.. vm.LogEntries.Where(e => e.Contains("WARNING:", StringComparison.Ordinal))];
        Assert.Equal(warnings.Count, loggedWarnings.Count);
        for (int i = 0; i < warnings.Count; i++)
        {
            Assert.Contains(warnings[i], loggedWarnings[i], StringComparison.Ordinal);
        }
    }

    // ── 16. Filesystem root ────────────────────────────────────

    [Fact]
    public void FilesystemRoot_TrailingSeparator_ErrorStatus_NoAutoOutputPath()
    {
        string driveRoot = Path.GetPathRoot(TempDir)!; // e.g. "C:\" — has a trailing separator
        var scanner = new StubReleaseScanner(EmptyResult);
        CreatorViewModel vm = CreateVm(scanner, out _);
        string previousOutput = Path.Combine(TempDir, "pre-existing.srr");
        vm.OutputPath = previousOutput;

        vm.InputPath = driveRoot;

        Assert.Equal(FieldState.Error, vm.OutputStatus.State);
        Assert.Equal(FieldState.Error, vm.InputStatus.State);
        Assert.Equal(previousOutput, vm.OutputPath); // never overwritten with an auto name
        Assert.Equal(0, scanner.Calls); // never actually scans a filesystem root
        Assert.False(vm.IsScanning);
        Assert.False(vm.CreateSRRCommand.CanExecute(null)); // no empty creation from a rejected input
    }

    // ── _scanCts lifecycle race ──────────────────────────────

    [Fact]
    public async Task RapidInputSwitching_WithoutAwaiting_NeverThrows()
    {
        // RunFolderScanAsync's cleanup used to run on a background thread (ConfigureAwait(false) +
        // a bare `finally` that disposed/null'd _scanCts directly), racing OnInputPathChanged's
        // cancellation of the SAME field on the UI thread. CancellationTokenSource forbids
        // concurrent Cancel()/Dispose() on one instance (ObjectDisposedException — a crash), and a
        // background finally could null out a newer scan's live CTS (TOCTOU). A fast (non-gated)
        // scanner plus many unawaited switches maximizes the odds of overlapping a background
        // completion with the next switch's synchronous cancel — the exact window the bug needed.
        var scanner = new StubReleaseScanner(EmptyResult);
        CreatorViewModel vm = CreateVm(scanner, out _);

        var pending = new List<Task>();
        for (int i = 0; i < 200; i++)
        {
            vm.InputPath = CreateFolder();
            if (vm.LastFolderScan is { } scan)
            {
                pending.Add(scan);
            }
        }

        // Drains every scan's Task (including already-superseded ones) so an exception that escaped
        // a background thread — the original crash — surfaces here instead of being silently lost
        // on a thread-pool thread nobody observed.
        await Task.WhenAll(pending);
    }

    // ── Cross-mode OutputPath auto-vs-user provenance ──

    [Fact]
    public async Task FileAutoFill_SwitchToFolder_OutputPathReplacedWithFolderAutoValue()
    {
        string root = CreateFolder("Release.Folder-GRP");
        string file = Touch(Path.Combine(TempDir, "movie.sfv"));
        CreatorViewModel vm = CreateVm(new StubReleaseScanner(EmptyResult), out _);

        vm.InputPath = file; // file-mode auto-fill now records provenance too
        string fileAutoValue = vm.OutputPath;
        Assert.Equal(Path.Combine(TempDir, "movie.srr"), fileAutoValue);

        vm.InputPath = root;
        await vm.LastFolderScan!;

        Assert.Equal(Path.Combine(TempDir, "Release.Folder-GRP.srr"), vm.OutputPath);
        Assert.NotEqual(fileAutoValue, vm.OutputPath);
    }

    [Fact]
    public async Task FileAutoFill_UserEdits_SwitchToFolder_OutputPathPreserved()
    {
        string root = CreateFolder("Release.Folder-GRP");
        string file = Touch(Path.Combine(TempDir, "movie.sfv"));
        CreatorViewModel vm = CreateVm(new StubReleaseScanner(EmptyResult), out _);

        vm.InputPath = file;
        string userChosen = Path.Combine(TempDir, "my-own-name.srr");
        vm.OutputPath = userChosen;

        vm.InputPath = root;
        await vm.LastFolderScan!;

        Assert.Equal(userChosen, vm.OutputPath);
    }

    [Fact]
    public async Task FolderAutoFill_SwitchToFile_OutputPathReplacedNotStaleFolder()
    {
        string root = CreateFolder("Release.Folder-GRP");
        string file = Touch(Path.Combine(TempDir, "movie.sfv"));
        CreatorViewModel vm = CreateVm(new StubReleaseScanner(EmptyResult), out _);

        vm.InputPath = root;
        await vm.LastFolderScan!;
        string folderAutoValue = vm.OutputPath;
        Assert.Equal(Path.Combine(TempDir, "Release.Folder-GRP.srr"), folderAutoValue);

        vm.InputPath = file; // typed directly (not via BrowseInputCommand) — still re-derives

        Assert.Equal(Path.Combine(TempDir, "movie.srr"), vm.OutputPath);
        Assert.NotEqual(folderAutoValue, vm.OutputPath);
    }

    [Fact]
    public async Task FolderAutoFill_UserEdits_SwitchToFile_OutputPathPreserved()
    {
        string root = CreateFolder("Release.Folder-GRP");
        string file = Touch(Path.Combine(TempDir, "movie.sfv"));
        CreatorViewModel vm = CreateVm(new StubReleaseScanner(EmptyResult), out _);

        vm.InputPath = root;
        await vm.LastFolderScan!;

        string userChosen = Path.Combine(TempDir, "my-own-name.srr");
        vm.OutputPath = userChosen;

        vm.InputPath = file;

        Assert.Equal(userChosen, vm.OutputPath);
    }

    // ── Folder error paths must gate Create, not fail open ──

    [Fact]
    public async Task PriorSuccessfulScan_ThenFilesystemRoot_InputStatusNotStale_CanCreateFalse_CollectionsEmpty()
    {
        string root = CreateFolder();
        string aSfv = Path.Combine(root, "a.sfv");
        string bSfv = Path.Combine(root, "b.sfv");
        var scan = new ReleaseScanResult(
            [new ReleaseSetInput(aSfv, "a.sfv"), new ReleaseSetInput(bSfv, "b.sfv")], [], [], [], [], []);
        CreatorViewModel vm = CreateVm(new StubReleaseScanner(scan), out _);
        vm.OutputPath = Path.Combine(TempDir, "out.srr");

        vm.InputPath = root;
        await vm.LastFolderScan!;
        Assert.Equal(2, vm.DetectedSets.Count);
        Assert.Equal(FieldState.Ok, vm.InputStatus.State);

        string driveRoot = Path.GetPathRoot(TempDir)!;
        vm.InputPath = driveRoot;

        // InputStatus must not keep showing the prior scan's success summary once the collections
        // behind it have been wiped.
        Assert.Equal(FieldState.Error, vm.InputStatus.State);
        Assert.False(vm.CreateSRRCommand.CanExecute(null));
        Assert.Empty(vm.DetectedSets);
    }

    [Fact]
    public async Task ScannerRootError_SetsErrorStatus_CanCreateFalse_NoEmptyCreation()
    {
        string root = CreateFolder();
        var rootError = ReleaseScanResult.RootError(root, "Access to the path is denied.");
        CreatorViewModel vm = CreateVm(new StubReleaseScanner(rootError), out FakeSRRCreationService srr);
        vm.OutputPath = Path.Combine(TempDir, "out.srr");

        vm.InputPath = root;
        await vm.LastFolderScan!;

        Assert.Equal(FieldState.Error, vm.InputStatus.State);
        Assert.False(vm.CreateSRRCommand.CanExecute(null));
        Assert.Equal(0, srr.InputsCalls); // no empty/header-only creation from an unreadable root
    }

    [Fact]
    public async Task AfterRootError_SubsequentSuccessfulScan_ClearsErrorAndEnablesCreate()
    {
        string rootA = CreateFolder("A");
        string rootB = CreateFolder("B");
        string bSet = Path.Combine(rootB, "b.sfv");
        var resultsByRoot = new Dictionary<string, ReleaseScanResult>
        {
            [rootA] = ReleaseScanResult.RootError(rootA, "Access to the path is denied."),
            [rootB] = new ReleaseScanResult([new ReleaseSetInput(bSet, "b.sfv")], [], [], [], [], []),
        };
        CreatorViewModel vm = CreateVm(new MultiRootReleaseScanner(resultsByRoot), out _);
        vm.OutputPath = Path.Combine(TempDir, "out.srr");

        vm.InputPath = rootA;
        await vm.LastFolderScan!;
        Assert.Equal(FieldState.Error, vm.InputStatus.State);
        Assert.False(vm.CreateSRRCommand.CanExecute(null));

        vm.InputPath = rootB;
        await vm.LastFolderScan!;

        Assert.Equal(FieldState.Ok, vm.InputStatus.State);
        Assert.True(vm.CreateSRRCommand.CanExecute(null));
    }

    [Fact]
    public async Task ScannerThrowsUnexpectedException_NotStrandedScanning_ErrorStatus_CanCreateFalse()
    {
        // RunFolderScanAsync used to catch ONLY OperationCanceledException, and
        // RarProofInspector.Inspect catches only IOException/UnauthorizedAccessException — so an
        // unexpected throw (ArgumentException/NotSupportedException/SecurityException from a
        // FileStream, or a RAR-parser fault) would fault the background Task, the UI completion Post
        // would never run, and IsScanning + InputStatus would stay stranded on "Scanning…" (Create
        // disabled, a11y live region stuck) until the user re-inputs. The catch-all must fail
        // closed: clear IsScanning and gate Create with an Error status, like the root-enumeration
        // error.
        string root = CreateFolder();
        CreatorViewModel vm = CreateVm(new ThrowingReleaseScanner(new InvalidOperationException("kaboom")), out FakeSRRCreationService srr);
        vm.OutputPath = Path.Combine(TempDir, "out.srr");

        vm.InputPath = root;
        await vm.LastFolderScan!;

        Assert.False(vm.IsScanning);                          // not stuck busy
        Assert.Equal(FieldState.Error, vm.InputStatus.State); // not stranded on Info "Scanning…"
        Assert.False(vm.CreateSRRCommand.CanExecute(null));   // fail closed — no empty/header-only SRR
        Assert.Equal(0, srr.InputsCalls);
    }
    // ── Scan-outcome side effects ────────────────────────────

    [Fact]
    public async Task LeavingFolderMode_ClearsSelectionsAlongWithTheCollections()
    {
        // ClearFolderScanResults clears SelectedStoredFile, SelectedExtraSample and
        // SelectedExtraSubtitle as well as the collections themselves. Moving the lifecycle behind
        // setter delegates makes it easy to carry the collections across and forget the selections,
        // leaving a selection pointing at an item no longer in its list.
        //
        // NOTE the path this exercises: a SUCCESSFUL scan clears the collections individually and
        // deliberately does NOT null the selections (a bound control does that through its own
        // two-way binding). ClearFolderScanResults — the method that also nulls them — runs when
        // folder mode is LEFT, and on the root-error and scan-fault paths.
        string root = CreateFolder();
        CreatorViewModel vm = CreateVm(new StubReleaseScanner(new ReleaseScanResult([], [], [], [], [], [])), out _);

        vm.InputPath = root;
        await vm.LastFolderScan!;

        vm.StoredFiles.Add(new CreatorViewModel.StoredFileItem
        {
            FullPath = Path.Combine(TempDir, "stale.nfo"),
            StoredName = "stale.nfo",
        });
        vm.SelectedStoredFile = vm.StoredFiles[0];
        vm.ExtraSampleFiles.Add(Path.Combine(TempDir, "stale-clip.mkv"));
        vm.SelectedExtraSample = vm.ExtraSampleFiles[0];
        vm.ExtraSubtitleSfvFiles.Add(Path.Combine(TempDir, "stale-subs.sfv"));
        vm.SelectedExtraSubtitle = vm.ExtraSubtitleSfvFiles[0];

        // Each selection must be genuinely non-null first, or its assertion below is vacuous.
        Assert.NotNull(vm.SelectedStoredFile);
        Assert.NotNull(vm.SelectedExtraSample);
        Assert.NotNull(vm.SelectedExtraSubtitle);

        vm.InputPath = string.Empty; // leaves folder mode

        Assert.Null(vm.SelectedExtraSample);
        Assert.Null(vm.SelectedExtraSubtitle);
        Assert.Null(vm.SelectedStoredFile);
        Assert.Empty(vm.StoredFiles);
        Assert.Empty(vm.ExtraSampleFiles);
        Assert.Empty(vm.ExtraSubtitleSfvFiles);
    }

    [Fact]
    public async Task Scan_UpdatesActionHint_OnSuccessfulCompletionToo()
    {
        // Every scan outcome calls UpdateActionHint — success included, not only the failure paths.
        // A refactor that wires the hint update onto the error branches alone would leave the hint
        // stale after a successful scan.
        string root = CreateFolder();
        CreatorViewModel vm = CreateVm(new StubReleaseScanner(new ReleaseScanResult([], [], [], [], [], [])), out _);

        // A USER-owned output path, set first, so the scan's auto-fill does not run. That matters:
        // auto-filling would change OutputPath, and the resulting OnOutputPathChanged would refresh
        // the hint by itself — masking whether scan completion refreshes it at all.
        string userOutput = Path.Combine(TempDir, "chosen-" + Guid.NewGuid().ToString("N") + ".srr");
        vm.OutputPath = userOutput;

        // A sentinel proves the post-scan value is genuinely recomputed rather than merely still
        // being whatever the earlier hook left. Setting it BEFORE the input change is safe: for a
        // FOLDER input, OnInputPathChanged starts the scan and returns without touching the hint.
        const string sentinel = "sentinel — must be recomputed when the scan completes";
        vm.ActionHint = sentinel;

        vm.InputPath = root;
        await vm.LastFolderScan!;

        // With input and output both set and nothing outstanding, the hint must be empty — and the
        // only thing that can have recomputed it is scan completion, since OutputPath never moved.
        Assert.Equal(userOutput, vm.OutputPath);
        Assert.Equal(string.Empty, vm.ActionHint);
    }
}
