using ReScene.App.Core.Models;
using ReScene.App.Core.Services;
using ReScene.App.Core.ViewModels;
using ReScene.App.Core.ViewModels.Reconstruction;
using ReScene.Core;
using ReScene.SRR;

namespace ReScene.App.Core.Tests;

/// <summary>
/// Drives the reconstruction run loop with a fake brute-force service that writes committed files into
/// the guarded scratch work-root exactly as the library would, proving the headline fix (#3): a verified
/// keyed set's output is relocated into <c>OutputPath\output</c>; scratch removal is a user OPT-IN
/// (<c>CleanupReconstructionWorkFiles</c>) — the removal-asserting tests opt in via
/// <see cref="FakeAppSettingsService"/>, while the default keeps each set's work-root for diagnostics
/// (per-attempt rar logs, input copies). The legacy empty-key set stays byte-identical; a cancelled
/// in-flight set's scratch follows the same gate (cases g, byte-identical, headline, default-keep).
/// </summary>
public sealed class ReconstructorRelocationRunTests : TempDirTestBase
{
    private sealed class InlineUiDispatcher : IUiDispatcher
    {
        public void Invoke(Action action) => action();
        public void Post(Action action) => action();
        public void Post(Action action, UiDispatcherPriority priority) => action();
        public bool CheckAccess() => true;
    }

    /// <summary>Fake brute-force service that runs a supplied handler for each set and writes real files.</summary>
    private sealed class ScriptedBruteForceService : IBruteForceService
    {
        public event EventHandler<BruteForceProgressEventArgs>? Progress { add { } remove { } }
        public event EventHandler<BruteForceStatusChangedEventArgs>? StatusChanged { add { } remove { } }
        public event EventHandler<LogEventArgs>? LogMessage { add { } remove { } }
        public event EventHandler<FileCopyProgressEventArgs>? FileCopyProgress { add { } remove { } }
        public event EventHandler<CRCValidationProgressEventArgs>? CRCValidationProgress { add { } remove { } }
        public event EventHandler<TimestampPreservationFailedEventArgs>? TimestampPreservationFailed { add { } remove { } }

        public required Func<BruteForceOptions, BruteForceRunResult> OnRun { get; init; }

        public Task<BruteForceRunResult> RunAsync(BruteForceOptions options, CancellationToken cancellationToken = default)
            => Task.FromResult(OnRun(options));
    }

    private static ReconstructorViewModel CreateVm(ScriptedBruteForceService brute, IAppSettingsService? settings = null, IFileMover? mover = null) =>
        new(brute, new NoOpFileDialogService(), new InlineUiDispatcher(), new TestUiTimerFactory(),
            settings, tempDir: null, launcher: null, fileMover: mover);

    /// <summary>Records every move without performing it, so a test can assert the DESTINATION.</summary>
    private sealed class RecordingFileMover : IFileMover
    {
        public List<(string Source, string Destination)> Moves { get; } = [];

        public void Move(string source, string destination) => Moves.Add((source, destination));
    }

    /// <summary>Settings double opting into clearing the scratch work-roots (the pre-setting behaviour).</summary>
    private static FakeAppSettingsService CleanupOptIn() =>
        new() { Settings = new AppSettings { CleanupReconstructionWorkFiles = true } };

    private static SRRArchiveSet MakeSet(string key, string dir, params string[] volumes)
    {
        var set = new SRRArchiveSet { Key = key, Directory = dir };
        foreach (string v in volumes)
        {
            set.VolumeNames.Add(v);
        }

        return set;
    }

    private static ReconstructionImportState ImportWith(params SRRArchiveSet[] sets) => new()
    {
        ArchiveSets = sets,
        OriginalRARFileNames = [.. sets.SelectMany(s => s.VolumeNames)],
    };

    /// <summary>Writes one brute-force committed volume under the run's scratch <c>output</c> dir.</summary>
    private static BruteForceRunResult WriteBruteSuccess(BruteForceOptions options, string volumeName)
    {
        string dir = Path.Combine(options.OutputDirectoryPath, "output");
        Directory.CreateDirectory(dir);
        string file = Path.Combine(dir, volumeName);
        File.WriteAllText(file, "vol");
        var combo = new WinningCombo(500, []);
        return new BruteForceRunResult(true, combo) { Matches = [new CommittedMatch(combo, [file])] };
    }

    // ── headline (#3): keyed single set relocates; scratch removal now requires the cleanup opt-in ──

    [Fact]
    public async Task Run_KeyedSingleSet_RelocatesToOutput_AndRemovesScratch()
    {
        // Scratch removal is now a user opt-in (CleanupReconstructionWorkFiles); this test opts in to
        // keep asserting the clearing behaviour.
        var brute = new ScriptedBruteForceService { OnRun = o => WriteBruteSuccess(o, "store_little.rar") };
        ReconstructorViewModel vm = CreateVm(brute, CleanupOptIn());
        vm.WinRARPath = TempDir;
        vm.ReleasePath = TempDir;
        vm.OutputPath = TempDir;
        vm.CompleteAllVolumes = false;
        vm.SetImportStateForTest(ImportWith(MakeSet("store_little", "", "store_little.rar")));

        await vm.RunArchiveSetsForTestAsync(CancellationToken.None);

        Assert.True(File.Exists(Path.Combine(TempDir, "output", "store_little.rar")));
        Assert.False(Directory.Exists(Path.Combine(TempDir, ".rescene-work"))
            && Directory.EnumerateFileSystemEntries(Path.Combine(TempDir, ".rescene-work")).Any());
        Assert.True(vm.LastRunSucceeded);
    }

    [Fact]
    public async Task Run_Default_KeepsScratchWorkRoot_AndLogsKeptPath()
    {
        // DEFAULT behaviour (no opt-in): the set's scratch work-root — per-attempt rar logs, input
        // copies, attempted archives — survives the run for diagnostics, and its path is logged.
        var brute = new ScriptedBruteForceService { OnRun = o => WriteBruteSuccess(o, "store_little.rar") };
        ReconstructorViewModel vm = CreateVm(brute);
        vm.WinRARPath = TempDir;
        vm.ReleasePath = TempDir;
        vm.OutputPath = TempDir;
        vm.CompleteAllVolumes = false;
        vm.SetImportStateForTest(ImportWith(MakeSet("store_little", "", "store_little.rar")));

        await vm.RunArchiveSetsForTestAsync(CancellationToken.None);

        // Relocation still commits the verified volume to the real output tree.
        Assert.True(File.Exists(Path.Combine(TempDir, "output", "store_little.rar")));
        Assert.True(vm.LastRunSucceeded);

        // The scratch work-root is kept and findable via the logged path.
        string scratchRoot = Path.Combine(TempDir, ".rescene-work");
        Assert.True(Directory.Exists(scratchRoot) && Directory.EnumerateDirectories(scratchRoot).Any(),
            "the set's work-root should survive under .rescene-work by default");
        Assert.Contains(vm.LogEntries, l => l.Contains("Work files kept: ", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Run_SetFailsBeforeScratchExists_DoesNotLogKeptWorkFiles()
    {
        // Peer-review F1: a set can fail before its work root is ever created (an unsatisfiable
        // per-set version requirement throws in BuildOptionsForSet), and the default keep branch must
        // not point the user at a diagnostics folder that does not exist.
        var brute = new ScriptedBruteForceService { OnRun = o => WriteBruteSuccess(o, o.RAROptions.OriginalRARFileNames[0]) };
        ReconstructorViewModel vm = CreateVm(brute);
        vm.Version5 = false;
        vm.Version6 = false; // only 3.x/4.x enabled — a RAR5 set is unsatisfiable
        vm.ReleasePath = TempDir;
        vm.OutputPath = TempDir;
        vm.CompleteAllVolumes = false;
        SRRArchiveSet bad = MakeSet("b", "", "b.rar");
        bad.RARVersion = 50;
        vm.SetImportStateForTest(ImportWith(bad));

        await vm.RunArchiveSetsForTestAsync(CancellationToken.None);

        Assert.Contains(vm.LogEntries, l => l.Contains("Set b failed:", StringComparison.Ordinal));
        Assert.DoesNotContain(vm.LogEntries, l => l.Contains("Work files kept:", StringComparison.Ordinal));
        Assert.False(vm.LastRunSucceeded);
    }

    // ── byte-identical: the legacy empty-key set keeps output at OutputPath\output, no scratch ──

    [Fact]
    public async Task Run_LegacyEmptyKeySet_KeepsOutputInPlace_NoScratchCreated()
    {
        var brute = new ScriptedBruteForceService { OnRun = o => WriteBruteSuccess(o, "x.rar") };
        ReconstructorViewModel vm = CreateVm(brute);
        vm.WinRARPath = TempDir;
        vm.ReleasePath = TempDir;
        vm.OutputPath = TempDir;
        vm.CompleteAllVolumes = false;
        // No archive sets → ResolveSets synthesizes a single flat set with an empty key (the legacy path).
        vm.SetImportStateForTest(new ReconstructionImportState { OriginalRARFileNames = ["x.rar"] });

        await vm.RunArchiveSetsForTestAsync(CancellationToken.None);

        Assert.True(File.Exists(Path.Combine(TempDir, "output", "x.rar"))); // already final, byte-identical
        Assert.False(Directory.Exists(Path.Combine(TempDir, ".rescene-work"))); // no scratch used at all
        Assert.True(vm.LastRunSucceeded);
    }

    // ── (g) cancel mid-set (cleanup opted in): in-flight scratch removed; committed set untouched ──

    [Fact]
    public async Task Run_CancelDuringSecondSet_CleansItsScratch_LeavesFirstSetCommitted()
    {
        // Pins the CLEARING behaviour, so this test opts into CleanupReconstructionWorkFiles.
        using var cts = new CancellationTokenSource();
        var brute = new ScriptedBruteForceService
        {
            OnRun = o =>
            {
                if (o.RAROptions.OriginalRARFileNames.Contains("b.rar"))
                {
                    // Second set: write a partial scratch, then cancel — the loop must break and clean it.
                    Directory.CreateDirectory(Path.Combine(o.OutputDirectoryPath, "output"));
                    File.WriteAllText(Path.Combine(o.OutputDirectoryPath, "output", "b.rar"), "partial");
                    cts.Cancel();
                    return new BruteForceRunResult(true, new WinningCombo(500, []));
                }

                return WriteBruteSuccess(o, "a.rar");
            },
        };
        ReconstructorViewModel vm = CreateVm(brute, CleanupOptIn());
        vm.WinRARPath = TempDir;
        vm.ReleasePath = TempDir;
        vm.OutputPath = TempDir;
        vm.CompleteAllVolumes = false;
        vm.SetImportStateForTest(ImportWith(MakeSet("a", "", "a.rar"), MakeSet("b", "", "b.rar")));

        // The loop breaks on cancellation and returns normally (StartAsync raises the OCE afterwards).
        await vm.RunArchiveSetsForTestAsync(cts.Token);

        Assert.True(File.Exists(Path.Combine(TempDir, "output", "a.rar")));      // set 1 committed
        Assert.False(File.Exists(Path.Combine(TempDir, "output", "b.rar")));     // set 2 never relocated
        Assert.False(Directory.Exists(ReconstructionPathGuard.ResolveScratchChild(TempDir, "b"))); // scratch cleaned
    }

    // ── final-review Important: a keyed set whose work-root resolution throws is a per-set failure, ──
    //    not a whole-run abort. WorkRootFor is computed before the per-set try; a throw there must be
    //    recorded as THIS set's failure and the loop must continue to the next set.

    [Fact]
    public async Task Run_WorkRootResolutionThrows_FailsThatSetOnly_SiblingStillCommits()
    {
        var brute = new ScriptedBruteForceService
        {
            OnRun = o => WriteBruteSuccess(o, o.RAROptions.OriginalRARFileNames[0]),
        };
        ReconstructorViewModel vm = CreateVm(brute);
        vm.WinRARPath = TempDir;
        vm.ReleasePath = TempDir;
        vm.OutputPath = TempDir;
        vm.CompleteAllVolumes = false;

        // The failing set is ordered first, so proving the loop still reached and committed the sibling
        // proves it did NOT abort every remaining set on the throw.
        SRRArchiveSet bad = MakeSet("bad", "", "bad.rar");
        SRRArchiveSet good = MakeSet("good", "", "good.rar");
        vm.SetImportStateForTest(ImportWith(bad, good));

        // Make ONLY the first set's guarded scratch child a junction that escapes the reserved scratch
        // root, so its WorkRootFor -> ResolveScratchChild throws (ArgumentException from the escape
        // guard) OUTSIDE the per-set try. The sibling's scratch child does not exist and resolves normally.
        string badScratch = ReconstructionPathGuard.ResolveScratchChild(TempDir, "bad");
        Directory.CreateDirectory(Path.GetDirectoryName(badScratch)!); // the reserved .rescene-work root
        TestDirLink.Create(badScratch, Path.Combine(TempDir, "escape-scratch")); // target is outside .rescene-work

        // Must NOT throw: the WorkRootFor failure is caught per-set, never propagated out of the loop.
        await vm.RunArchiveSetsForTestAsync(CancellationToken.None);

        Assert.True(File.Exists(Path.Combine(TempDir, "output", "good.rar")));   // sibling continued & committed
        Assert.False(File.Exists(Path.Combine(TempDir, "output", "bad.rar")));   // failed set produced no output
        Assert.Contains(vm.LogEntries, l => l.Contains("Set bad failed:", StringComparison.Ordinal)); // recorded as this set's failure
        Assert.False(vm.LastRunSucceeded);                                       // summary ran; not all sets matched
    }

    [Fact]
    public async Task Run_WorkRootResolutionAccessDenied_CaughtPerSet_DoesNotAbortRun()
    {
        var brute = new ScriptedBruteForceService
        {
            OnRun = o => WriteBruteSuccess(o, o.RAROptions.OriginalRARFileNames[0]),
        };
        ReconstructorViewModel vm = CreateVm(brute);
        vm.WinRARPath = TempDir;
        vm.ReleasePath = TempDir;
        vm.OutputPath = TempDir;
        vm.CompleteAllVolumes = false;

        SRRArchiveSet one = MakeSet("one", "", "one.rar");
        SRRArchiveSet two = MakeSet("two", "", "two.rar");
        vm.SetImportStateForTest(ImportWith(one, two));

        // Deny inspection of the shared reserved scratch root itself: every keyed set's WorkRootFor ->
        // ResolveScratchChild -> ResolveReal must throw UnauthorizedAccessException as it descends into
        // it. Each throw must be caught per-set so the run reaches ReportSetSummary rather than aborting.
        // (A shared denied root fails BOTH keyed sets, so sibling-continuation is proven separately by the
        // junction test above; here the point is that an access-denied throw does not escape the loop.)
        string scratchRoot = Path.Combine(TempDir, ReconstructionPathGuard.ScratchDirName);
        Directory.CreateDirectory(scratchRoot);
        AclDenyHelper.DenyAccess(scratchRoot);
        try
        {
            await vm.RunArchiveSetsForTestAsync(CancellationToken.None); // must NOT throw
        }
        finally
        {
            AclDenyHelper.RestoreAccess(scratchRoot); // restore BEFORE temp-dir cleanup
        }

        Assert.Contains(vm.LogEntries, l => l.Contains("Set one failed:", StringComparison.Ordinal));
        Assert.Contains(vm.LogEntries, l => l.Contains("Set two failed:", StringComparison.Ordinal));
        Assert.False(vm.LastRunSucceeded); // summary ran and marked the run failed — the run did not abort
    }
    // ── Live reads during relocation ─────────────────────────
    //
    // OutputPath and CompleteAllVolumes are read DURING relocation, after the brute-force awaits, and
    // neither control is disabled while a run is in progress - so an edit made mid-run is honoured.
    // Probing the extracted runner found both unguarded: snapshotting either at run start left all
    // 800 tests passing, which is exactly the regression a seam like this invites.

    [Fact]
    public async Task OutputPathChangedMidRun_RelocationTargetsTheNewPath()
    {
        string original = Path.Combine(TempDir, "original");
        string redirected = Path.Combine(TempDir, "redirected");
        Directory.CreateDirectory(original);
        Directory.CreateDirectory(redirected);

        ReconstructorViewModel? vm = null;
        var mover = new RecordingFileMover();
        var brute = new ScriptedBruteForceService
        {
            OnRun = o =>
            {
                BruteForceRunResult result = WriteBruteSuccess(o, "store_little.rar");
                vm!.OutputPath = redirected;   // the user retargets while the engine is running
                return result;
            },
        };

        vm = CreateVm(brute, CleanupOptIn(), mover);
        vm.WinRARPath = TempDir;
        vm.ReleasePath = TempDir;
        vm.OutputPath = original;
        vm.CompleteAllVolumes = false;
        vm.SetImportStateForTest(ImportWith(MakeSet("store_little", "", "store_little.rar")));

        await vm.RunArchiveSetsForTestAsync(CancellationToken.None);

        // Compared against the RESOLVED roots, because that is what relocation produces:
        // ResolveOutputChild routes the destination through ReconstructionPathGuard.ResolveReal,
        // so a temp directory reached through a link comes back spelled as its target. On macOS
        // that happens with nobody arranging it — Path.GetTempPath() returns /var/folders/…, and
        // /var is a symlink to /private/var — so a raw prefix check failed there while passing on
        // Windows and Linux. It also made the negative assertion below vacuous on macOS, since a
        // /var/… needle can never appear in a /private/var/… destination.
        (_, string destination) = Assert.Single(mover.Moves);
        Assert.StartsWith(ReconstructionPathGuard.ResolveReal(redirected), destination, StringComparison.Ordinal);
        Assert.DoesNotContain(
            Path.Combine(ReconstructionPathGuard.ResolveReal(original), "output"),
            destination,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task CompleteAllVolumesSetMidRun_IsHonouredAndRejectsAPartialSet()
    {
        // The set declares two volumes but only one is committed. With CompleteAllVolumes read live,
        // the flag flipped on mid-run makes relocation refuse the partial result; with a run-start
        // snapshot of false it would accept and move it.
        ReconstructorViewModel? vm = null;
        var mover = new RecordingFileMover();
        var brute = new ScriptedBruteForceService
        {
            OnRun = o =>
            {
                BruteForceRunResult result = WriteBruteSuccess(o, "two_vol.rar");
                vm!.CompleteAllVolumes = true;
                return result;
            },
        };

        vm = CreateVm(brute, CleanupOptIn(), mover);
        vm.WinRARPath = TempDir;
        vm.ReleasePath = TempDir;
        vm.OutputPath = TempDir;
        vm.CompleteAllVolumes = false;
        vm.SetImportStateForTest(ImportWith(MakeSet("two_vol", "", "two_vol.rar", "two_vol.r00")));

        await vm.RunArchiveSetsForTestAsync(CancellationToken.None);

        Assert.Empty(mover.Moves);
        Assert.False(vm.LastRunSucceeded);
    }

}
