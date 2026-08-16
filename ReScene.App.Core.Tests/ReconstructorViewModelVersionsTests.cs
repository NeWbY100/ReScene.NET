using System.Collections.Specialized;
using ReScene.App.Core.Services;
using ReScene.App.Core.ViewModels;
using ReScene.App.Core.ViewModels.Reconstruction;
using ReScene.Core;
using ReScene.SRR;

namespace ReScene.App.Core.Tests;

public sealed class ReconstructorViewModelVersionsTests : IDisposable
{
    private readonly List<string> _tempDirs = [];

    public void Dispose()
    {
        foreach (string d in _tempDirs)
        {
            try
            { Directory.Delete(d, recursive: true); }
            catch { /* best effort */ }
        }
    }

    /// <summary>Creates a real WinRAR versions folder containing one "winrar-NNN" subfolder (with a
    /// console-binary stub) per version, so setting WinRARPath drives the actual async folder scan.
    /// The stub is named for the platform's binary (rar.exe on Windows, rar elsewhere), or the
    /// scanner would find no versions at all.</summary>
    private string MakeWinRARFolder(params int[] versions)
    {
        string root = Path.Combine(Path.GetTempPath(), "rvm-versions-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        _tempDirs.Add(root);
        foreach (int v in versions)
        {
            string dir = Path.Combine(root, $"winrar-{v}");
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, RarExecutable.FileName), "stub");
        }

        return root;
    }

    private sealed class InlineUiDispatcher : IUiDispatcher
    {
        public void Invoke(Action action) => action();
        public void Post(Action action) => action();
        public void Post(Action action, UiDispatcherPriority priority) => action();
        public bool CheckAccess() => true;
    }

    /// <summary>
    /// Dispatcher that DEFERS marshalled actions onto a queue instead of running them inline. The
    /// async folder scan marshals its result via Invoke; queueing lets the test drain the scan Task
    /// and then run that continuation on the TEST thread via <see cref="Pump"/> — so nothing mutates
    /// the view-model concurrently and the scan landing is fully deterministic.
    /// </summary>
    private sealed class QueueingUiDispatcher : IUiDispatcher
    {
        private readonly Queue<Action> _queue = new();
        public void Invoke(Action action) => _queue.Enqueue(action);
        public void Post(Action action) => _queue.Enqueue(action);
        public void Post(Action action, UiDispatcherPriority priority) => _queue.Enqueue(action);
        public bool CheckAccess() => true;

        /// <summary>Runs every queued action on the calling thread, in order.</summary>
        public void Pump()
        {
            while (_queue.Count > 0)
            {
                _queue.Dequeue()();
            }
        }
    }

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

    /// <summary>
    /// Queues <see cref="Invoke"/> instead of running it, so a test can hold an async scan's
    /// completion callback and let something else happen first.
    /// </summary>
    private sealed class DeferringInvokeDispatcher : IUiDispatcher
    {
        private readonly Queue<Action> _deferred = new();

        public void Invoke(Action action) => _deferred.Enqueue(action);
        public void Post(Action action) => action();
        public void Post(Action action, UiDispatcherPriority priority) => action();
        public bool CheckAccess() => true;

        public void Pump()
        {
            while (_deferred.Count > 0)
            {
                _deferred.Dequeue()();
            }
        }
    }

    private static ReconstructorViewModel CreateVm(IUiDispatcher? dispatcher = null)
        => new(new InertBruteForceService(), new NoOpFileDialogService(),
               dispatcher ?? new InlineUiDispatcher(), new TestUiTimerFactory(), settingsService: null);

    private static readonly IReadOnlyList<InstalledRARVersion> Installed =
    [
        new(500, "winrar-500", "p500"),
        new(560, "winrar-560", "p560"),
        new(602, "winrar-602", "p602"),
        new(624, "winrar-624", "p624"),
    ];

    private static int[] Ticked(ReconstructorViewModel vm) =>
        [.. vm.VersionGroups.SelectMany(g => g.Leaves).Where(l => l.IsChecked).Select(l => l.Version).OrderBy(v => v)];

    [Fact]
    public void ApplyScanResult_ImportIntent_TicksAllInstalledInEnabledMajors()
    {
        ReconstructorViewModel vm = CreateVm();
        vm.Version2 = vm.Version3 = vm.Version4 = vm.Version7 = false;
        vm.Version5 = true;
        vm.Version6 = true;

        vm.ApplyScanResult(Installed, folderScanned: true);

        Assert.True(vm.HasScannedVersions);
        int[] expectedTicked = [500, 560, 602, 624];
        Assert.Equal(expectedTicked, Ticked(vm));
        Assert.Equal(2, vm.VersionGroups.Count);   // 5.x and 6.x
    }

    [Fact]
    public void FolderScannedThenImport_ReTicksToNewMajors()
    {
        ReconstructorViewModel vm = CreateVm();
        vm.Version5 = true;
        vm.Version6 = true;
        vm.ApplyScanResult(Installed, folderScanned: true);

        // Simulate an SRR import that maps only to 6.x
        vm.Version5 = false;
        vm.Version6 = true;
        vm.LoadPendingVersionSelection(null);   // import path: no explicit list, reconcile from majors

        int[] expectedTicked = [602, 624];
        Assert.Equal(expectedTicked, Ticked(vm));
    }

    [Fact]
    public void ExplicitSelection_TicksSubset_DropsMissing_ThenClears()
    {
        ReconstructorViewModel vm = CreateVm();
        vm.LoadPendingVersionSelection([560, 624, 999]);   // config load sets pending
        vm.ApplyScanResult(Installed, folderScanned: true);

        int[] expectedTicked = [560, 624];
        Assert.Equal(expectedTicked, Ticked(vm));

        // A subsequent scan with no new intent must NOT re-apply the (now consumed) pending list;
        // it falls back to majors. With no majors enabled, nothing is ticked.
        vm.Version2 = vm.Version3 = vm.Version4 = vm.Version5 = vm.Version6 = vm.Version7 = false;
        vm.ApplyScanResult(Installed, folderScanned: true);
        Assert.Empty(Ticked(vm));
    }

    [Fact]
    public void ManualLeafToggle_SyncsMajorBooleans()
    {
        ReconstructorViewModel vm = CreateVm();
        vm.Version5 = true;
        vm.Version6 = true;
        vm.ApplyScanResult(Installed, folderScanned: true);

        foreach (RARVersionLeaf leaf in vm.VersionGroups.First(g => g.Major == 6).Leaves)
        {
            leaf.IsChecked = false;   // untick all of 6.x
        }

        Assert.True(vm.Version5);
        Assert.False(vm.Version6);   // synced from tree
    }

    [Fact]
    public void SelectedLeafVersions_ReflectsTicksAscending()
    {
        ReconstructorViewModel vm = CreateVm();
        vm.LoadPendingVersionSelection([624, 500]);
        vm.ApplyScanResult(Installed, folderScanned: true);

        int[] expectedVersions = [500, 624];
        Assert.Equal(expectedVersions, vm.SelectedLeafVersions.ToArray());
    }

    [Fact]
    public async Task ChangingWinRARPath_ResetsScannedState_SoConfigSelectionSurvivesNewScan()
    {
        // Folder A is already scanned (mirrors the automatic startup scan from settings); folder B is
        // a config's target with a disjoint set of versions. Both are real dirs so the WinRARPath
        // changes drive the actual OnWinRARPathChanged / async-scan path — where the pending selection
        // used to be lost. A queueing dispatcher makes each scan's landing deterministic (pumped on
        // the test thread), so no assertion races the scan continuation.
        string folderA = MakeWinRARFolder(400);          // major 4 only
        string folderB = MakeWinRARFolder(560, 624);     // majors 5 and 6

        var dispatcher = new QueueingUiDispatcher();
        ReconstructorViewModel vm = new(new InertBruteForceService(), new NoOpFileDialogService(),
            dispatcher, new TestUiTimerFactory(), settingsService: null);

        // Folder A scanned: run the scan Task, then pump its queued ApplyScanResult onto this thread.
        vm.WinRARPath = folderA;
        await vm.LastVersionScan!;
        dispatcher.Pump();
        Assert.True(vm.HasScannedVersions);

        // Changing to a different folder must SYNCHRONOUSLY mark the tree as not-yet-scanned; B's scan
        // continuation is only queued (not yet pumped), so this reads the fix's direct effect.
        // Without the fix this stays true (folder A's stale scanned state).
        vm.WinRARPath = folderB;
        Assert.False(vm.HasScannedVersions);

        // Mirror ConfigMapper.Apply's ordering: the pending selection is applied while B's scan is
        // still in flight. Because HasScannedVersions is now false, ApplyReconcile KEEPS the pending
        // list (rather than consuming it against folder A's stale scan and losing it).
        vm.LoadPendingVersionSelection([560, 624]);

        // B's scan lands: drain the Task, then pump its queued ApplyScanResult. The surviving pending
        // selection now ticks exactly the configured versions.
        await vm.LastVersionScan!;
        dispatcher.Pump();
        int[] expectedTicked = [560, 624];
        Assert.Equal(expectedTicked, Ticked(vm));
    }

    [Fact]
    public void ApplyScanResult_EmptyFolder_ShowsHint_NoGroups()
    {
        ReconstructorViewModel vm = CreateVm();

        vm.ApplyScanResult([], folderScanned: false);

        Assert.Empty(vm.VersionGroups);
        Assert.True(vm.ShowNoVersionsHint);
        Assert.False(vm.HasScannedVersions);
    }

    /// <summary>Two folders that both parse to version 390, distinguished only by folder name.</summary>
    private static readonly IReadOnlyList<InstalledRARVersion> SameVersionVariants =
    [
        new(390, "winrar-390", "path-390"),
        new(390, "winrar-390-beta1", "path-390-beta1", "beta1"),
    ];

    [Fact]
    public async Task BuildSharedSettings_UntickedVariantLeaf_ExcludesItsFolder()
    {
        // audit #36: unticking one same-version variant leaf must exclude ONLY that folder, even
        // though both leaves collapse to version 390.
        ReconstructorViewModel vm = CreateVm();
        vm.Version3 = true;                                  // major 3 enabled → both 390 leaves tick
        vm.ApplyScanResult(SameVersionVariants, folderScanned: true);

        RARVersionLeaf beta = vm.VersionGroups.SelectMany(g => g.Leaves).Single(l => l.FolderName == "winrar-390-beta1");
        beta.IsChecked = false;                             // untick the beta variant only

        SharedReconstructionSettings shared = await vm.BuildSharedSettingsAsync(CancellationToken.None);

        Assert.Equal(["winrar-390"], shared.SelectedVersionFolders);

        // And the folder allow-list flows through the planner into the engine options.
        var set = new SRRArchiveSet { Key = "", Directory = "" };
        set.VolumeNames.Add("x.rar");
        BruteForceOptions opts = ArchiveSetPlanner.BuildOptionsForSet(set, shared,
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));
        Assert.Equal(["winrar-390"], opts.RAROptions.AllowedVersionFolders);
    }

    [Fact]
    public async Task BuildSharedSettings_NoScan_LeavesFolderAllowListEmpty()
    {
        // With no real scan (broad fallback ranges), the run must NOT be folder-filtered.
        ReconstructorViewModel vm = CreateVm();

        SharedReconstructionSettings shared = await vm.BuildSharedSettingsAsync(CancellationToken.None);

        Assert.Empty(shared.SelectedVersionFolders);
    }

    // ── #6: scan-safe InstalledVersions capture ─────────────────────────────────────────────────────

    [Fact]
    public async Task BuildSharedSettings_StaleScanAfterHasScannedVersionsCleared_InstalledVersionsEmpty()
    {
        // Mirrors OnWinRARPathChanged: a WinRARPath change clears HasScannedVersions synchronously
        // but leaves _lastScan stale until the new folder's scan lands. InstalledVersions must read
        // the scan-state guard (like SelectedVersionFolders already does), not the stale list.
        ReconstructorViewModel vm = CreateVm();
        vm.ApplyScanResult(Installed, folderScanned: true); // _lastScan = Installed, HasScannedVersions = true
        vm.HasScannedVersions = false;                       // simulate the WinRARPath-change effect

        SharedReconstructionSettings shared = await vm.BuildSharedSettingsAsync(CancellationToken.None);

        Assert.Empty(shared.InstalledVersions);
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

    [Fact]
    public async Task RunArchiveSetsForTestAsync_AwaitsInFlightRescan_UsesCompletedScanForFormatSelection()
    {
        // Pad the folder with many non-WinRAR subdirectories so the second (in-flight) scan below
        // takes measurably longer than the run loop's own incidental delays elsewhere — without this
        // padding, a fast scan could race-complete "by accident" even without the fix under test.
        string folder = MakeWinRARFolder(390);
        for (int i = 0; i < 3000; i++)
        {
            Directory.CreateDirectory(Path.Combine(folder, $"decoy-{i}"));
        }

        string releaseDir = Path.Combine(Path.GetTempPath(), "rvm-release-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(releaseDir);
        _tempDirs.Add(releaseDir);

        var brute = new ScriptedBruteForceService { OnRun = o => WriteBruteSuccess(o, "b.rar") };
        ReconstructorViewModel vm = new(brute, new NoOpFileDialogService(), new InlineUiDispatcher(), new TestUiTimerFactory(), settingsService: null);

        vm.WinRARPath = folder;
        await vm.LastVersionScan!;
        Assert.True(vm.HasScannedVersions);
        int[] expectedTicked = [390];
        Assert.Equal(expectedTicked, Ticked(vm)); // only 3.90 so far — not RAR5-capable

        // A new WinRAR 5.60 appears in the same folder. Major 5 was silently cleared by the first
        // scan's SyncMajorsFromTree (no 5.x leaf existed yet to keep it ticked) — re-enable it, as a
        // user would by ticking the major-5 checkbox, before the manual rescan below.
        vm.Version5 = true;
        string dir560 = Path.Combine(folder, "winrar-560");
        Directory.CreateDirectory(dir560);
        File.WriteAllText(Path.Combine(dir560, RarExecutable.FileName), "stub");

        // RescanVersions kicks off a new scan that is deliberately NOT awaited here —
        // RunArchiveSetsForTestAsync itself must await it (the fix under test).
        vm.RescanVersionsCommand.Execute(null);
        Assert.True(vm.HasScannedVersions); // RescanVersions never clears it for a valid folder (#39)

        vm.ReleasePath = releaseDir;
        vm.OutputPath = releaseDir;
        vm.CompleteAllVolumes = false;

        var set = new SRRArchiveSet { Key = "b", Directory = "" };
        set.VolumeNames.Add("b.rar");
        set.RARVersion = 50; // RAR5 — only satisfiable once the new 5.60 scan has landed
        vm.SetImportStateForTest(new ReconstructionImportState { ArchiveSets = [set], OriginalRARFileNames = ["b.rar"] });

        await vm.RunArchiveSetsForTestAsync(CancellationToken.None);

        Assert.True(vm.LastRunSucceeded, string.Join(Environment.NewLine, vm.LogEntries));
        int[] expectedAfterScan = [390, 560];
        Assert.Equal(expectedAfterScan, Ticked(vm)); // proves the completed (not stale) scan landed
    }
    // ── _suppressGroupSync ───────────────────────────────────
    //
    // The flag guards OnGroupSelectionChanged, which mirrors "any leaf in this major is ticked" onto
    // the six coarse VersionN bools. Two regions raise it, and they are NOT equivalent - measured,
    // not assumed:
    //
    //   RebuildVersionGroups  BEHAVIOURAL. The rebuild adds groups one at a time, so a
    //                         SelectionChanged arriving mid-rebuild would sync against a
    //                         PARTIALLY-BUILT tree. Removing the flag and forcing that re-entrancy
    //                         produces a spurious Version6=False write, corrected to True a moment
    //                         later - an observer receives a transient False then True. The
    //                         test below pins exactly that.
    //
    //   SetAllLeaves          ALSO BEHAVIOURAL, but only visible in the INTERLEAVING. The VersionN
    //                         write sequence alone is byte-for-byte identical with the flag and
    //                         without it, which is what a first measurement here wrongly concluded
    //                         meant "performance only". What differs is when those writes happen
    //                         RELATIVE to the tree's own SelectionChanged notifications: without the
    //                         flag a subscriber invoked during the bulk update already sees the
    //                         major bool updated, with it the major changes only after every leaf
    //                         has. The theory below pins that.
    //
    // Neither region's notifications are bound in AXAML today - the view binds VersionGroups, not
    // the six bools - so the consequence is stated as what an observer receives, not as UI flicker.

    [Fact]
    public void RebuildVersionGroups_SuppressesMajorSync_SoNoSyncEverSeesAPartiallyBuiltTree()
    {
        ReconstructorViewModel vm = CreateVm();
        vm.Version5 = vm.Version6 = true;

        List<string> majorWrites = [];
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(vm.Version5) or nameof(vm.Version6))
            {
                majorWrites.Add($"{e.PropertyName}={(e.PropertyName == nameof(vm.Version5) ? vm.Version5 : vm.Version6)}");
            }
        };

        // Force the re-entrancy the flag exists for. ObservableCollection raises CollectionChanged
        // SYNCHRONOUSLY from Add, so this runs in the middle of the rebuild, with only some groups
        // in place; flipping a leaf there raises its group's SelectionChanged.
        bool reentered = false;
        vm.VersionGroups.CollectionChanged += (_, e) =>
        {
            if (e.Action == NotifyCollectionChangedAction.Add && !reentered)
            {
                reentered = true;
                var group = (RARVersionGroup)e.NewItems![0]!;
                group.Leaves[0].IsChecked = !group.Leaves[0].IsChecked;
            }
        };

        vm.ApplyScanResult(Installed, folderScanned: true);

        Assert.True(reentered, "the re-entrant hook never ran, so this test proves nothing");
        // Version5/Version6 were seeded to the values the rebuild will settle on, so the legitimate
        // post-rebuild sync writes nothing. Any recorded write is therefore transient evidence of a
        // sync that ran against a partially-built tree.

        // No major bool was written DURING the rebuild. Without the flag the mid-rebuild sync sees a
        // tree that has no major-6 group yet and writes Version6=False, then corrects it.
        Assert.Empty(majorWrites);
        Assert.True(vm.Version5);
        Assert.True(vm.Version6);
    }

    [Theory]
    [InlineData(false)]   // Select None: every callback must still see the pre-bulk true
    [InlineData(true)]    // Select All:  every callback must still see the pre-bulk false
    public void BulkLeafUpdate_DefersTheMajorSync_UntilEveryLeafHasChanged(bool selectAll)
    {
        // The view-model subscribes each group's SelectionChanged when it BUILDS the group, so it is
        // always the first subscriber; a test subscriber added afterwards therefore observes the
        // state its handler left behind.
        //
        // With the flag, that handler returns immediately during a bulk update, so every callback
        // sees the major bool as it was before the bulk started. Without it, the callback for the
        // last leaf sees the major already flipped - the bulk operation stops being atomic to any
        // observer of the tree.
        ReconstructorViewModel vm = CreateVm();
        vm.Version5 = vm.Version6 = true;
        vm.ApplyScanResult(Installed, folderScanned: true);

        // Put the tree in the state the bulk command will move it AWAY from.
        if (selectAll)
        {
            vm.SelectNoVersionsCommand.Execute(null);
        }

        bool major5Before = vm.Version5;
        Assert.Equal(!selectAll, major5Before);

        RARVersionGroup group5 = vm.VersionGroups.Single(g => g.Major == 5);
        List<bool> version5SeenDuringSelectionChanged = [];
        group5.SelectionChanged += (_, _) => version5SeenDuringSelectionChanged.Add(vm.Version5);

        if (selectAll)
        {
            vm.SelectAllVersionsCommand.Execute(null);
        }
        else
        {
            vm.SelectNoVersionsCommand.Execute(null);
        }

        Assert.NotEmpty(version5SeenDuringSelectionChanged);
        Assert.All(version5SeenDuringSelectionChanged, seen => Assert.Equal(major5Before, seen));

        // ...and the major did settle to the new value once the bulk finished.
        Assert.Equal(selectAll, vm.Version5);
    }

    [Fact]
    public async Task RescanAfterTheFolderDisappeared_DiscardsTheStillRunningScanOfIt()
    {
        // TriggerVersionScan's invalid-path branch bumps the scan token itself, so a scan already
        // running against the folder cannot land afterwards and mark the tree scanned. Nothing
        // tested that increment: removing it left all 796 tests passing.
        //
        // It matters only on the RESCAN path. Reached through the WinRARPath setter,
        // InvalidateAndStartScan has already bumped the token, so the branch's own increment is
        // redundant there - a first version of this test drove the setter and passed with the
        // increment deleted, i.e. for the wrong reason entirely.
        //
        // Deterministic without timing: the scan's completion is marshalled through
        // IUiDispatcher.Invoke, so a dispatcher that QUEUES Invoke holds it while the folder goes.
        var dispatcher = new DeferringInvokeDispatcher();
        ReconstructorViewModel vm = CreateVm(dispatcher);

        string versionsDir = Directory.CreateTempSubdirectory("rescene-versions-").FullName;
        try
        {
            Directory.CreateDirectory(Path.Combine(versionsDir, "winrar-500"));
            File.WriteAllText(
                Path.Combine(versionsDir, "winrar-500", OperatingSystem.IsWindows() ? "rar.exe" : "rar"),
                string.Empty);

            vm.WinRARPath = versionsDir;
            await vm.LastVersionScan!;   // the scan finished; its apply is sitting in the queue
            Assert.False(vm.HasScannedVersions, "the apply must still be queued, not applied");

            // The folder disappears while its scan's result is still pending. WinRARPath is
            // unchanged, so nothing else bumps the token.
            Directory.Delete(versionsDir, recursive: true);
            vm.RescanVersionsCommand.Execute(null);

            dispatcher.Pump();   // the stale completion finally runs

            Assert.False(vm.HasScannedVersions,
                "a scan of a folder that no longer exists must not mark the tree as scanned");
            Assert.Empty(vm.VersionGroups);
        }
        finally
        {
            if (Directory.Exists(versionsDir))
            {
                Directory.Delete(versionsDir, recursive: true);
            }
        }
    }

    [Fact]
    public void MajorSync_InterleavesEachReadWithItsWrite_SoALaterMajorSeesAnEarlierSubscribersEdit()
    {
        // SyncMajorsFromTree reads and writes one major at a time, and each write can synchronously
        // raise PropertyChanged. A subscriber that mutates a LATER major's leaves from an earlier
        // major's notification is therefore seen by the reads that follow.
        //
        // Batching the six predicates and writing them afterwards - which an extraction of this
        // projection is very tempted to do - produces the same values in the same order while
        // silently losing that. This pins it.
        ReconstructorViewModel vm = CreateVm();
        vm.Version5 = vm.Version6 = true;
        vm.ApplyScanResult(Installed, folderScanned: true);

        // Clear everything first, so the Select All below genuinely FLIPS Version5 false->true and
        // raises its notification. Without this the value never changes and nothing fires.
        vm.SelectNoVersionsCommand.Execute(null);
        Assert.False(vm.Version5);
        Assert.False(vm.Version6);

        RARVersionGroup group6 = vm.VersionGroups.Single(g => g.Major == 6);
        bool untickedDuringTheSync = false;
        vm.PropertyChanged += (_, e) =>
        {
            // Version5 is written BEFORE Version6 is read.
            if (e.PropertyName == nameof(vm.Version5) && !untickedDuringTheSync)
            {
                untickedDuringTheSync = true;
                foreach (RARVersionLeaf leaf in group6.Leaves)
                {
                    leaf.IsChecked = false;
                }
            }
        };

        // Any sync entry point will do; a bulk tick runs one sync at the end.
        vm.SelectAllVersionsCommand.Execute(null);

        Assert.True(untickedDuringTheSync, "the re-entrant edit never ran, so this test proves nothing");
        Assert.False(vm.Version6, "the read of major 6 must have seen the edit made while major 5 was written");
    }

}
