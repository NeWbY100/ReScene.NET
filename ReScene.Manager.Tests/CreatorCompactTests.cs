using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Automation.Peers;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using ReScene.App.Core.Models;
using ReScene.App.Core.Services;
using ReScene.App.Core.ViewModels;
using ReScene.Manager.Behaviors;
using ReScene.Manager.Services;
using ReScene.Manager.Views;
using ReScene.SRR;
using ReScene.SRS;

namespace ReScene.Manager.Tests;

/// <summary>
/// Small-window layout degradation tests for <see cref="CreatorView"/> (switch height DERIVED
/// from the view's own measured expanded floor — see <see cref="Threshold"/> — config row
/// AutoToStar 110 compact / 80 help-open / 500 expanded, log 80, Help body MaxHeight 40, compact
/// CI bound <see cref="CompactInvariantRig.CiBound"/> == 307, pinned band ceiling 75, compact
/// worst floor &lt;= 307). The largest converted view: band 1's config ScrollViewer hosts a GRID
/// (not a StackPanel, unlike every prior converted view) so the pre-existing Stored Files
/// GridSplitter/DataGrid pair can live inside it — the first real consumer of
/// <see cref="CompactHeightBehavior"/>'s DESCENDANT RowSizes application and
/// <see cref="CompactRowMode.PixelRestore"/> in a shipped view (both already unit-proven at the
/// behavior level: <c>CompactHeightBehaviorTests.DescendantGridRowSizes_FollowTheRootsMode</c> /
/// <c>RowSizes_ApplyOnCompact_RestorePreservingSplitterDrag</c>).
/// </summary>
public class CreatorCompactTests
{
    // ── Inert VM construction (mirrors CreatorViewTests.CreateViewModel) ──

    private sealed class InertSrrCreationService : ISRRCreationService
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

    private sealed class InertReleaseScanner : IReleaseScanner
    {
        public ReleaseScanResult Scan(string releaseRoot, CancellationToken ct = default) => new([], [], [], [], [], []);
    }

    private sealed class InertSrsCreationService : ISRSCreationService
    {
        public event EventHandler<SRSCreationProgressEventArgs>? Progress { add { } remove { } }
        public event EventHandler<SRSScanProgressEventArgs>? ScanProgress { add { } remove { } }

        public Task<SRSCreationResult> CreateAsync(string outputPath, string sampleFilePath, SRSCreationOptions options, CancellationToken ct)
            => Task.FromResult(new SRSCreationResult { Success = true });
    }

    private sealed class InertTempDirectoryService : ITempDirectoryService
    {
        public string CreateTempDirectory() => Path.GetTempPath();
        public void Cleanup(string? tempDir) { }
    }

    private sealed class DefaultAppSettingsService : IAppSettingsService
    {
        public event EventHandler? Changed { add { } remove { } }
        public AppSettings Load() => new();
        public void Save(AppSettings settings) { }
    }

    private sealed class InlineUiDispatcher : IUiDispatcher
    {
        public void Invoke(Action action) => action();
        public void Post(Action action) => action();
        public void Post(Action action, UiDispatcherPriority priority) => action();
        public bool CheckAccess() => true;
    }

    private static CreatorViewModel CreateVm() =>
        new(
            new InertSrrCreationService(),
            new InertSrsCreationService(),
            new AvaloniaFileDialogService(static () => null), // headless: dialogs no-op, never block
            new InertTempDirectoryService(),
            new DefaultAppSettingsService(),
            new InlineUiDispatcher(),
            new InertReleaseScanner());

    private static CreatorViewModel.StoredFileItem Item(string fullPath, string storedName) =>
        new() { FullPath = fullPath, StoredName = storedName };

    private static CreatorView BuildWorstCase()
    {
        CreatorViewModel vm = CreateVm();
        ForceWorstCase(vm);
        return new CreatorView { DataContext = vm };
    }

    /// <summary>
    /// This view's switch height, READ BACK from the behavior rather than written down — see
    /// <see cref="CompactInvariantRig.ProbeSwitchPoint"/>. Probed once per test process.
    /// </summary>
    private static double Threshold => _threshold.Value;

    private static readonly Lazy<double> _threshold =
        new(() => CompactInvariantRig.ProbeSwitchPoint(BuildWorstCase));

    private const double CompactInner = 319;   // the canonical 700x450 minimum window

    /// <summary>Comfortably above <see cref="Threshold"/>, clear of the restore hysteresis.</summary>
    private static double ExpandedInner => Threshold + CompactInvariantRig.ExpandedHeadroom;

    /// <summary>
    /// The worst case, forced together: IsScanning true, HasDetectedSets with
    /// 12 sets (capped at 96 DIPs by the pre-existing ScrollViewer), both FieldStatusLines
    /// non-None with realistic wrapping-length messages, IsCreating + ShowProgress (Cancel +
    /// ProgressMessage + ProgressBar all visible), and StoredFiles populated with 8 rows.
    /// </summary>
    private static void ForceWorstCase(CreatorViewModel vm)
    {
        vm.IsScanning = true;
        for (int i = 0; i < 12; i++)
        {
            vm.DetectedSets.Add(new ReleaseSetInput($@"C:\release\disc{i:D2}\movie.sfv", $"disc{i:D2}/movie.sfv"));
        }

        vm.InputStatus = FieldStatus.Warning("No .rar volumes found in \"release-group\". An SRR is built from the release's .rar files — they need to be in this folder next to the .sfv.");
        vm.OutputStatus = FieldStatus.Info("Auto-filled from the release folder name. Change it if needed.");
        vm.IsCreating = true;
        vm.ShowProgress = true;
        vm.ProgressMessage = "Creating SRR: hashing volume 4 of 12...";

        for (int i = 0; i < 8; i++)
        {
            vm.StoredFiles.Add(Item($@"C:\release\file{i:D2}.nfo", $"file{i:D2}.nfo"));
        }
    }

    // ── 1. Invariant (CompactInvariantRig's four checks) — verified to fail against the pre-fix plain Grid layout ──

    /// <summary>
    /// The derivation's own guarantee, in place of the constant this used to pin: whatever the
    /// view's expanded floor measures on this platform, the height it switches at is above it.
    /// </summary>
    [AvaloniaFact]
    public void Invariant_ExpandedModeFloor_UnderDerivedThreshold()
    {
        (Window window, Grid root) = CompactViewRig.HostAt(BuildWorstCase(), ExpandedInner);
        try
        {
            Assert.DoesNotContain("compactHeight", root.Classes);
            double floor = CompactInvariantRig.MeasureFloor(root);
            double threshold = CompactHeightBehavior.GetEffectiveThreshold(root);
            Assert.True(floor < threshold,
                $"expanded-mode floor {floor:F1} must be under the DERIVED threshold {threshold:F1}");
        }
        finally { window.Close(); }
    }

    /// <summary>
    /// The reported defect, on the view it was reported against: clicking into the SRR Creator tab
    /// at a height that calls for compact showed one frame of the expanded layout first.
    /// <para>
    /// A tab's content is not laid out until the tab is first selected, so that selection is the
    /// view's first ever layout — modelled here by attaching the view to a host that starts empty,
    /// which is the same thing without the tab strip's arithmetic in the way. A frame is built from
    /// a completed layout pass, so the executable form of "no flash" is that no completed pass ever
    /// gave this root a real height while it carried the wrong mode.
    /// </para>
    /// <para>
    /// Worth having on the real view and not only on the behavior's own rig: this view runs its own
    /// <c>LayoutUpdated</c> handler to cap the config scroller, and the fix decides in line DURING
    /// a layout pass — so the two now run against each other on every first attach.
    /// </para>
    /// </summary>
    [AvaloniaFact]
    public void FirstVisitToTheTab_BelowTheSwitchPoint_NeverPresentsAnExpandedFrame()
    {
        CreatorView view = BuildWorstCase();
        var root = (Grid)view.Content!;

        List<(double Height, bool Compact)> passes = [];
        root.LayoutUpdated += (_, _) => passes.Add((root.Bounds.Height, root.Classes.Contains("compactHeight")));

        var host = new Decorator();
        var window = new Window { Width = 700, Height = Threshold - 30, Content = host };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        try
        {
            Assert.Empty(passes);   // precondition: never laid out while the "tab" was unselected

            host.Child = view;      // the click into the tab
            for (int i = 0; i < 6; i++)
            {
                Dispatcher.UIThread.RunJobs();
            }

            Assert.Contains("compactHeight", root.Classes);
            Assert.True(passes.Exists(p => p.Height > 0), "no layout pass ever sized the view");

            List<double> expandedFrames = [.. passes.Where(p => p.Height > 0 && !p.Compact).Select(p => p.Height)];
            Assert.True(expandedFrames.Count == 0,
                $"{expandedFrames.Count} of {passes.Count} layout passes were presentable frames in EXPANDED " +
                $"mode below the switch point ({Threshold:F0}) — at heights " +
                string.Join(", ", expandedFrames.Select(h => h.ToString("F0"))));
        }
        finally { window.Close(); }
    }

    /// <summary>
    /// THE invariant: at every height around this view's own switch point, whichever mode is
    /// active actually fits. See
    /// <see cref="CompactInvariantRig.AssertActiveModeFitsAroundSwitchPoint"/> — no height and no
    /// verdict in it is a platform-calibrated number.
    /// </summary>
    [AvaloniaFact]
    public void Invariant_ActiveModeFits_AtEveryHeightAroundTheSwitchPoint() =>
        CompactInvariantRig.AssertActiveModeFitsAroundSwitchPoint("Creator", BuildWorstCase);

    /// <summary>
    /// The REAL, user-facing guarantee <see cref="Invariant_ExpandedModeFloor_UnderDerivedThreshold"/>'s
    /// own <c>MeasureFloor</c> methodology cannot directly observe. MEASURED: with the worst case
    /// forced (12 detected sets capped at 96, 8 stored files, both FieldStatusLines non-None,
    /// Cancel+ProgressMessage+ProgressBar visible), this view's config content — none of which
    /// scrolls independently in EXPANDED mode without the production fix below — sums to ~883
    /// DIPs of natural (unconstrained) height, far exceeding the smallest window this view stays
    /// expanded in (721 DIPs when that was measured). Without <see cref="CreatorView"/>'s own dynamic config-ScrollViewer
    /// MaxHeight cap (ctor remarks), the pinned action band and the entire log would translate
    /// fully below the window's own bottom edge across this whole range — exactly the same
    /// categorical defect already found and fixed the same way for SampleRestorerView. This test
    /// uses REAL arranged rendering (<see cref="CompactViewRig.HostAt"/>) and the clip-aware
    /// <see cref="AssertFullyWithinWindow"/> across the measured-unsafe range (721 through
    /// comfortably past the ~883-DIP floor), plus a height far beyond it, to prove the actual
    /// defect is gone — not merely that one abstract number moved.
    /// </summary>
    [AvaloniaTheory]
    [InlineData(1.0)]     // the smallest possible expanded height
    [InlineData(40.0)]
    [InlineData(100.0)]
    [InlineData(163.0)]   // approximately the measured-unsafe range's own upper edge
    [InlineData(230.0)]
    [InlineData(680.0)]   // comfortably larger -- the cap must not OVER-constrain when there's room to spare
    public void Invariant_ExpandedMode_NeverClipsAcrossUnsafeHeightRange(double dipsAboveSwitchPoint)
    {
        // Offsets, not absolute heights: the range this covers is defined RELATIVE to the height
        // this view switches at, so it follows the derivation onto a platform whose fonts put that
        // switch somewhere else instead of testing a band that no longer means anything there.
        double innerHeight = Threshold + dipsAboveSwitchPoint;

        (Window window, Grid root) = CompactViewRig.HostAt(BuildWorstCase(), innerHeight);
        try
        {
            Assert.DoesNotContain("compactHeight", root.Classes);
            Button createButton = window.GetVisualDescendants().OfType<Button>().Single(b => b.Content is "Create SRR");
            Button cancel = window.GetVisualDescendants().OfType<Button>().Single(b => b.Content is "Cancel");
            ListBox log = window.GetVisualDescendants().OfType<ListBox>().Single(l => l.Classes.Contains("logList"));
            Assert.True(cancel.IsVisible);

            AssertFullyWithinWindow(createButton, window);
            AssertFullyWithinWindow(cancel, window);
            AssertFullyWithinWindow(log, window);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void Invariant_CompactFloor_HelpClosed_WithinCiBound()
    {
        CreatorViewModel vm = CreateVm();
        ForceWorstCase(vm);
        var view = new CreatorView { DataContext = vm };

        (Window window, Grid root) = CompactViewRig.HostAt(view, CompactInner);
        try
        {
            Assert.Contains("compactHeight", root.Classes);
            Assert.False(CompactHeightBehavior.GetHelpOpen(root));
            double floor = CompactInvariantRig.MeasureFloor(root);
            Assert.True(floor <= CompactInvariantRig.CiBound,
                $"compact floor {floor:F1} must be <= {CompactInvariantRig.CiBound}");
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void Invariant_CompactFloor_HelpOpen_WithinCiBound_AndPinnedBandRowSane()
    {
        CreatorViewModel vm = CreateVm();
        ForceWorstCase(vm);
        var view = new CreatorView { DataContext = vm };

        (Window window, Grid root) = CompactViewRig.HostAt(view, CompactInner);
        try
        {
            Assert.Contains("compactHeight", root.Classes);
            Expander helpDisclosure = root.GetVisualDescendants().OfType<Expander>().Single(e => e.Name == "HelpDisclosure");
            helpDisclosure.IsExpanded = true;
            Dispatcher.UIThread.RunJobs();
            Assert.True(CompactHeightBehavior.GetHelpOpen(root));

            // One sum: donation row applied (config row min -> 80) AND the body's own MaxHeight
            // (40) both spend the same 307-DIP budget — never checked independently.
            double floor = CompactInvariantRig.MeasureFloor(root);
            Assert.True(floor <= CompactInvariantRig.CiBound,
                $"compact+HelpOpen floor {floor:F1} must be <= {CompactInvariantRig.CiBound}");

            // Pinned band (row 2) is never the budget donor — its natural height stays small and
            // positive regardless of mode, within CompactInvariantRig.PinnedBandCeiling even with Cancel +
            // ProgressMessage + ProgressBar all forced visible (ForceWorstCase).
            Control pinnedBand = root.Children.OfType<Control>().Single(c => Grid.GetRow(c) == 2);
            Assert.True(pinnedBand.DesiredSize.Height > 0 && pinnedBand.DesiredSize.Height <= CompactInvariantRig.PinnedBandCeiling,
                $"pinned band height {pinnedBand.DesiredSize.Height:F1} out of the expected pinned-row range " +
                $"(0, {CompactInvariantRig.PinnedBandCeiling}]");
        }
        finally { window.Close(); }
    }

    // ── 2. Rendered matrix: compact @319 (700x450), fresh @Threshold, fresh @Threshold+1 ──

    [AvaloniaFact]
    public void RenderedMatrix_CompactAt700x450_ReachabilityNoClipAndTabWalk() =>
        AssertReachabilityNoClipAndTabWalk(CompactInner, expectCompact: true);

    [AvaloniaFact]
    public void RenderedMatrix_FreshAtThresholdExactly_IsExpanded_ReachabilityNoClipAndTabWalk() =>
        AssertReachabilityNoClipAndTabWalk(Threshold, expectCompact: false);

    [AvaloniaFact]
    public void RenderedMatrix_FreshAtThresholdPlusOne_IsExpanded_ReachabilityNoClipAndTabWalk() =>
        AssertReachabilityNoClipAndTabWalk(ExpandedInner, expectCompact: false);

    private static void AssertReachabilityNoClipAndTabWalk(double innerHeight, bool expectCompact)
    {
        AssertNoClip(innerHeight, expectCompact);
        AssertConfigAndActionReachable(innerHeight);
        AssertTabWalk(innerHeight);
    }

    private static void AssertNoClip(double innerHeight, bool expectCompact)
    {
        CreatorViewModel vm = CreateVm();
        ForceWorstCase(vm); // worst case: every conditional forced, grid populated
        var view = new CreatorView { DataContext = vm };

        (Window window, Grid root) = CompactViewRig.HostAt(view, innerHeight);
        try
        {
            Assert.Equal(expectCompact, root.Classes.Contains("compactHeight"));
            CompactInvariantRig.AssertArrangesWithin(root, root.Bounds.Height);
        }
        finally { window.Close(); }
    }

    /// <summary>
    /// Reachability coverage for the LAST option control (App name TextBox — no x:Name of its own, so
    /// distinguished by its Width="400", the only TextBox in the view with that width, mirroring
    /// SRSCreator/SampleRestorer's identical pattern) and the primary action (Create SRR button).
    /// Both routed through the config band's own ScrollViewer, identified by Grid.Row rather than
    /// by uniqueness-among-ScrollViewers — the Help body and the detected-sets scroller are ALSO
    /// bare, non-templated ScrollViewers, so Grid.Row is the only unambiguous handle.
    /// </summary>
    private static void AssertConfigAndActionReachable(double innerHeight)
    {
        CreatorViewModel vm = CreateVm();
        vm.InputPath = @"C:\release\movie.sfv";
        vm.OutputPath = @"C:\release\movie.srr";
        var view = new CreatorView { DataContext = vm };
        (Window window, Grid root) = CompactViewRig.HostAt(view, innerHeight);
        try
        {
            ScrollViewer configScroller = window.GetVisualDescendants().OfType<ScrollViewer>().Single(sv => Grid.GetRow(sv) == 1);

            // The keyboard route's own anchor — see AssertReachableByAllThreeRoutes' own doc for
            // why this view needs one (unlike every other converted view).
            Button keyboardAnchor = window.GetVisualDescendants().OfType<Button>().Single(b => ReferenceEquals(b.Command, vm.AddStoredFileCommand));

            TextBox appName = window.GetVisualDescendants().OfType<TextBox>().Single(t => t.Width == 400);
            AssertReachableByAllThreeRoutes(window, configScroller, appName, keyboardAnchor);

            Button createButton = window.GetVisualDescendants().OfType<Button>().Single(b => b.Content is "Create SRR");
            Assert.True(createButton.IsEffectivelyEnabled, "test precondition: Create SRR must be enabled to be a meaningful keyboard-reachability target");
            AssertReachableByAllThreeRoutes(window, configScroller, createButton, keyboardAnchor);
        }
        finally { window.Close(); }
    }

    /// <summary>
    /// Each route is exercised from a genuine "not yet visible" start (offset reset between
    /// routes) — otherwise the first route scrolling the target into view would make the other two
    /// trivially no-op without ever exercising their own mechanism.
    /// <para>
    /// <paramref name="keyboardAnchor"/> (required, unlike every other converted view's identical
    /// helper): <see cref="CompactViewRig.AssertReachableByKeyboard"/> only auto-establishes a
    /// starting point when NOTHING is already focused, via a single blind Tab press, and this
    /// helper's own job is the narrower claim "once inside the form, is the target reachable by
    /// each of the three routes". Anchoring explicitly keeps that claim honest whatever the walk's
    /// entry point happens to be, and keeps the three routes comparable with each other.
    /// </para>
    /// <para>
    /// It used to be load-bearing for a worse reason: this view's Input row carried unscoped
    /// TabIndex pins, so a blind Tab press from an unfocused window landed there and then cycled
    /// between the row and shell chrome forever, never reaching the rest of the form. That trap is
    /// fixed (KeyboardNavigation.TabNavigation="Local" on the path rows) and
    /// <c>ColdStartTabWalk_EscapesTheInputRow_AndReachesThePrimaryAction</c> now covers the
    /// cold-start path directly, which is where that claim belongs.
    /// </para>
    /// </summary>
    private static void AssertReachableByAllThreeRoutes(Window window, ScrollViewer scroller, Control target, Control keyboardAnchor)
    {
        scroller.Offset = default;
        Dispatcher.UIThread.RunJobs();
        CompactViewRig.AssertReachableByWheel(window, target);

        scroller.Offset = default;
        Dispatcher.UIThread.RunJobs();
        keyboardAnchor.Focus();
        Dispatcher.UIThread.RunJobs();
        CompactViewRig.AssertReachableByKeyboard(window, target);

        scroller.Offset = default;
        Dispatcher.UIThread.RunJobs();
        CompactViewRig.AssertReachableByThumb(window, target);
    }

    /// <summary>
    /// ORDER-ORACLE standard: the expected stop sequence is resolved INDEPENDENTLY, up front, by
    /// unique identity (bound command for Buttons, x:Name or a distinguishing attribute for
    /// TextBoxes/the DataGrid, the sole GridSplitter instance), never derived from a walk's own
    /// observed output.
    /// <para>
    /// Both path rows (Input and Output) are <c>DockPanel</c>s whose Browse buttons are docked
    /// Right and therefore declared FIRST, so their markup order is the reverse of what the user
    /// sees. Each carries explicit <c>TabIndex</c> values to put keyboard order back into visual
    /// order, and — the part that matters for THIS walk —
    /// <c>KeyboardNavigation.TabNavigation="Local"</c> to keep those values scoped to their own
    /// row. Without the scoping the pins were compared against the whole window, whose every other
    /// control carries the default <c>int.MaxValue</c>; the pinned controls therefore sorted apart
    /// from the rest of the form, and a walk entering the form elsewhere only reached them by
    /// running off the end. The order recorded here is the scoped one: each row sits where it
    /// renders. <c>ColdStartTabWalk_EscapesTheInputRow_AndReachesThePrimaryAction</c> covers the
    /// entry point this walk cannot (nothing focused at all), and
    /// <c>PathRows_TabOrderFollowsVisualOrder_DespiteReversedTreeOrder</c> pins the inversion the
    /// pins exist to correct.
    /// </para>
    /// </summary>
    private static void AssertTabWalk(double innerHeight)
    {
        CreatorViewModel vm = CreateVm();
        vm.InputPath = @"C:\release\movie.sfv";
        vm.OutputPath = @"C:\release\movie.srr"; // Create SRR enabled: its own position is pinned, not left unverified
        var view = new CreatorView { DataContext = vm };
        (Window window, Grid root) = CompactViewRig.HostAt(view, innerHeight);
        try
        {
            bool compact = root.Classes.Contains("compactHeight");

            // Independent ground truth, resolved BEFORE any walk runs. The Input path box is the
            // true first stop in BOTH modes — compact merely PREPENDS the Help toggle ahead of it,
            // per every other converted view's identical shape. It was "Add..." while the Input
            // row's unscoped TabIndex pins held it out of the ordinary order; scoping them
            // (CreatorView.axaml) put the row back where it visually is.
            List<Control> independentOrder = ResolveIndependentExpectedOrder(window, vm, compact);
            Control sentinel = independentOrder[0];

            IReadOnlyList<string> fixture = compact ? CompactModeTabOrderFixture : NormalModeTabOrderFixture;

            sentinel.Focus();
            Dispatcher.UIThread.RunJobs();
            CompactViewRig.TabOrderCapture forwardCapture = CompactViewRig.CaptureTabOrderControls(window, root, independentOrder);
            IReadOnlyList<Control> forwardOrder = forwardCapture.Order;
            Assert.Equal(fixture, forwardOrder.Select(CompactViewRig.Describe)); // human-readable regression net
            AssertSameControlSequence(independentOrder, forwardOrder, "forward"); // the actual discriminating check

            // The forward walk's terminal external target must be the SPECIFIC, expected
            // shell-chrome boundary — the rig's own fake shell (CompactViewRig's BuildShell) puts
            // a "_File" MenuItem right after the TabControl in Z-order (same finding as every
            // other converted view's own tab walk against the identical shared shell).
            MenuItem expectedExternalBoundary = window.GetVisualDescendants().OfType<MenuItem>()
                .Single(m => m.Header as string == "_File");
            Assert.True(forwardCapture.FirstExternalTarget is not null,
                "forward capture should have left root's scope onto an external control, not ended via a stable loop within root");
            Assert.True(ReferenceEquals(expectedExternalBoundary, forwardCapture.FirstExternalTarget),
                $"forward capture's terminal external target should be {CompactViewRig.Describe(expectedExternalBoundary)}, " +
                $"not {CompactViewRig.Describe(forwardCapture.FirstExternalTarget)} — same description does not mean same control instance.");

            // REVERSE: anchored at the forward walk's own LAST stop (the unambiguous boundary,
            // proven by the FORWARD exit above), never a presumed starting point. Checked against
            // the INDEPENDENT order's own reversal, and must land back on independentOrder[0] —
            // the actual, empirical proof of which control is genuinely first, rather than an
            // assumption riding on the visual layout.
            CompactViewRig.TabWalkResult reverse = CompactViewRig.RunTabPass(window, forwardOrder[^1], forward: false, independentOrder);

            List<Control> expectedReverseOrder = [.. Enumerable.Reverse(independentOrder)];
            AssertSameControlSequence(expectedReverseOrder, reverse.Order, "reverse");

            Assert.True(ReferenceEquals(reverse.LoopedBackTo, independentOrder[0]),
                $"the reverse walk should land back on {CompactViewRig.Describe(independentOrder[0])} (the independently-resolved " +
                $"first stop), not {CompactViewRig.Describe(reverse.LoopedBackTo)} — this is the actual proof that the forward " +
                "sentinel is genuinely first, not a presumption.");
        }
        finally { window.Close(); }
    }

    /// <summary>
    /// Independent ground truth for this view's tab order — each entry resolved by a UNIQUE
    /// identifier (bound <c>RelayCommand</c> reference for Buttons, x:Name or a distinguishing
    /// attribute for TextBoxes/the DataGrid, the sole GridSplitter, distinct Content strings for
    /// the option CheckBoxes), NEVER by re-deriving from a walk's own observed output. This view's
    /// three "Browse"-labelled buttons do NOT collide by
    /// description — all three carry distinct explicit AutomationProperties.Name values
    /// ("Browse for input file", "Browse folder for release input", "Browse for output path"); the
    /// output one gained its name after this comment was first written, when two of the three were
    /// named and the third still fell back to its Content, and the input one was "Browse input
    /// file" until the naming pass brought it onto the shared "Browse for &lt;target&gt;" phrasing.
    /// The folder one deliberately stays off that phrasing — see
    /// <see cref="PathRows_TabOrderFollowsVisualOrder_DespiteReversedTreeOrder"/> for the
    /// Label-in-Name reason. Resolving by Command reference here
    /// anyway is not redundant caution, it is the same house rule applied uniformly regardless of
    /// whether a REAL collision happens to exist today.
    /// </summary>
    private static List<Control> ResolveIndependentExpectedOrder(Window window, CreatorViewModel vm, bool compact)
    {
        Button add = window.GetVisualDescendants().OfType<Button>().Single(b => ReferenceEquals(b.Command, vm.AddStoredFileCommand));
        Button remove = window.GetVisualDescendants().OfType<Button>().Single(b => ReferenceEquals(b.Command, vm.RemoveStoredFileCommand));
        Button removeAll = window.GetVisualDescendants().OfType<Button>().Single(b => ReferenceEquals(b.Command, vm.RemoveAllStoredFilesCommand));
        Button moveUp = window.GetVisualDescendants().OfType<Button>().Single(b => ReferenceEquals(b.Command, vm.MoveStoredFileUpCommand));
        Button moveDown = window.GetVisualDescendants().OfType<Button>().Single(b => ReferenceEquals(b.Command, vm.MoveStoredFileDownCommand));
        DataGrid storedFilesGrid = window.GetVisualDescendants().OfType<DataGrid>().Single(g => g.Name == "StoredFilesGrid");
        GridSplitter splitter = window.GetVisualDescendants().OfType<GridSplitter>().Single();
        Button outputBrowse = window.GetVisualDescendants().OfType<Button>().Single(b => ReferenceEquals(b.Command, vm.BrowseOutputCommand));
        TextBox outputTextBox = window.GetVisualDescendants().OfType<TextBox>().Single(t => t.Name == "OutputTextBox");
        CheckBox autoInclude = window.GetVisualDescendants().OfType<CheckBox>().Single(c => c.Content is string s && s.StartsWith("Auto-include files", StringComparison.Ordinal));
        CheckBox autoCreateSrs = window.GetVisualDescendants().OfType<CheckBox>().Single(c => c.Content is string s && s.StartsWith("Auto-create SRS", StringComparison.Ordinal));
        CheckBox vobsubSrr = window.GetVisualDescendants().OfType<CheckBox>().Single(c => c.Content is string s && s.StartsWith("Vobsub SRR", StringComparison.Ordinal));
        CheckBox storeFixRar = window.GetVisualDescendants().OfType<CheckBox>().Single(c => c.Content is string s && s.StartsWith("Store fix RAR", StringComparison.Ordinal));
        CheckBox allowCompressed = window.GetVisualDescendants().OfType<CheckBox>().Single(c => c.Content is string s && s.StartsWith("Allow compressed", StringComparison.Ordinal));
        CheckBox osoHashes = window.GetVisualDescendants().OfType<CheckBox>().Single(c => c.Content is string s && s.StartsWith("OSO hashes", StringComparison.Ordinal));
        CheckBox languagesDiz = window.GetVisualDescendants().OfType<CheckBox>().Single(c => c.Content is string s && s.StartsWith("Languages.diz", StringComparison.Ordinal));
        TextBox appName = window.GetVisualDescendants().OfType<TextBox>().Single(t => t.Width == 400);
        Button createSrr = window.GetVisualDescendants().OfType<Button>().Single(b => ReferenceEquals(b.Command, vm.CreateSRRCommand));
        Button saveLog = window.GetVisualDescendants().OfType<Button>().Single(b => ReferenceEquals(b.Command, vm.SaveLogCommand));
        TextBox inputTextBox = window.GetVisualDescendants().OfType<TextBox>().Single(t => t.Name == "InputTextBox");
        Button inputBrowse = window.GetVisualDescendants().OfType<Button>().Single(b => ReferenceEquals(b.Command, vm.BrowseInputCommand));
        Button inputBrowseFolder = window.GetVisualDescendants().OfType<Button>().Single(b => ReferenceEquals(b.Command, vm.BrowseInputFolderCommand));

        // The Input row leads, which is both where it sits visually and where it sits in the tree.
        // It used to come LAST, after the log — not a design choice but the keyboard trap showing
        // through: unscoped TabIndex 0/1/2 sorted those three ahead of the whole window scope, and
        // a walk entering the form anywhere else only ever reached them by running off the end.
        // KeyboardNavigation.TabNavigation="Local" on the row (CreatorView.axaml) scopes the pins
        // to the row, so the row now takes its ordinary place among its siblings.
        List<Control> order =
        [
            inputTextBox, inputBrowse, inputBrowseFolder,
            add, remove, removeAll, moveUp, moveDown, storedFilesGrid, splitter, outputTextBox, outputBrowse,
            autoInclude, autoCreateSrs, vobsubSrr, storeFixRar, allowCompressed, osoHashes, languagesDiz, appName,
            createSrr, saveLog,
        ];

        if (compact)
        {
            ToggleButton helpToggle = window.GetVisualDescendants().OfType<Expander>().Single(e => e.Name == "HelpDisclosure")
                .GetVisualDescendants().OfType<ToggleButton>().Single();
            order.Insert(0, helpToggle);
        }

        return order;
    }

    /// <summary>
    /// Proves <see cref="AssertSameControlSequence"/> — and therefore <see cref="AssertTabWalk"/>'s
    /// own forward/reverse checks — is genuinely sensitive to a POSITIONAL swap, not merely to
    /// controls going missing. Unlike SRSCreator/SampleRestorer this view has no naturally
    /// identically-described sibling pair to swap (see <see cref="ResolveIndependentExpectedOrder"/>'s
    /// own doc), so this swaps two arbitrary, independently-resolved, adjacent stops ("Add..." and
    /// "Remove") instead — <see cref="AssertSameControlSequence"/> compares by REFERENCE, never by
    /// description, so it must catch this swap exactly as readily as a description-colliding one.
    /// </summary>
    [AvaloniaFact]
    public void AssertSameControlSequence_SwappedPositions_FailsNamingTheMismatch()
    {
        CreatorViewModel vm = CreateVm();
        vm.InputPath = @"C:\release\movie.sfv";
        vm.OutputPath = @"C:\release\movie.srr";
        var view = new CreatorView { DataContext = vm };
        (Window window, Grid root) = CompactViewRig.HostAt(view, ExpandedInner);
        try
        {
            List<Control> independentOrder = ResolveIndependentExpectedOrder(window, vm, compact: false);
            Control sentinel = independentOrder[0];
            sentinel.Focus();
            Dispatcher.UIThread.RunJobs();

            IReadOnlyList<Control> forwardOrder = CompactViewRig.CaptureTabOrderControls(window, root, independentOrder).Order;

            List<Control> tampered = [.. independentOrder];
            (tampered[0], tampered[1]) = (tampered[1], tampered[0]); // swap "Add..." (0) and "Remove" (1)

            Xunit.Sdk.FailException ex = Assert.Throws<Xunit.Sdk.FailException>(
                () => AssertSameControlSequence(tampered, forwardOrder, "forward"));

            Assert.Contains("position 0", ex.Message, StringComparison.Ordinal);
            Assert.Contains("same description does not mean same control instance", ex.Message, StringComparison.Ordinal);

            // The untampered, genuinely independent expectation still passes against the SAME real
            // walk — the failure above was the tampering, not an actual defect.
            AssertSameControlSequence(independentOrder, forwardOrder, "forward (untampered, sanity check)");
        }
        finally { window.Close(); }
    }

    /// <summary>
    /// OBJECT-REFERENCE-exact sequence comparison — asserts <paramref name="actual"/> is, position
    /// for position, the SAME control REFERENCES as <paramref name="expected"/>, not merely the
    /// same DESCRIPTIONS. Mirrors every other converted view's own identical helper.
    /// </summary>
    private static void AssertSameControlSequence(IReadOnlyList<Control> expected, IReadOnlyList<Control> actual, string context)
    {
        if (expected.Count != actual.Count)
        {
            Assert.Fail(
                $"{context}: expected {expected.Count} controls but the walk visited {actual.Count} " +
                $"(expected: {string.Join(", ", expected.Select(CompactViewRig.Describe))}; " +
                $"actual: {string.Join(", ", actual.Select(CompactViewRig.Describe))})");
        }

        for (int i = 0; i < expected.Count; i++)
        {
            if (!ReferenceEquals(expected[i], actual[i]))
            {
                Assert.Fail(
                    $"{context}: position {i} expected {CompactViewRig.Describe(expected[i])} but the " +
                    $"walk visited {CompactViewRig.Describe(actual[i])} — same description does not " +
                    "mean same control instance.");
            }
        }
    }

    // ── 3. Tab-order snapshots ────────────────────────────────────────
    //
    // Both entry points below simply invoke the SAME hardened AssertTabWalk (section 2's own
    // tab-walk helper, now ALSO the exact-order/completeness/reverse-boundary authority) at the
    // exact heights RenderedMatrix_CompactAt700x450_... and
    // RenderedMatrix_FreshAtThresholdPlusOne_... already exercise.

    [AvaloniaFact]
    public void TabOrderSnapshot_Normal_MatchesPreChangeFixture() => AssertTabWalk(ExpandedInner);

    [AvaloniaFact]
    public void TabOrderSnapshot_Compact_MatchesSpecSection2Order() => AssertTabWalk(CompactInner);

    // ── 4. Chrome ─────────────────────────────────────────────────────

    [AvaloniaFact]
    public void SingleIntroInstance_ExistsInBothModes()
    {
        CreatorViewModel vm = CreateVm();
        var normalView = new CreatorView { DataContext = vm };
        (Window normalWindow, Grid normalRoot) = CompactViewRig.HostAt(normalView, ExpandedInner);
        try
        {
            Assert.DoesNotContain("compactHeight", normalRoot.Classes);
            Assert.Equal(1, CountIntroInstances(normalWindow));
        }
        finally { normalWindow.Close(); }

        CreatorViewModel vm2 = CreateVm();
        var compactView = new CreatorView { DataContext = vm2 };
        (Window compactWindow, Grid compactRoot) = CompactViewRig.HostAt(compactView, CompactInner);
        try
        {
            Assert.Contains("compactHeight", compactRoot.Classes);
            Assert.Equal(1, CountIntroInstances(compactWindow));
        }
        finally { compactWindow.Close(); }
    }

    private static int CountIntroInstances(Window window) =>
        window.GetVisualDescendants().OfType<TextBlock>()
            .Count(t => t.Text is not null && t.Text.StartsWith("Create an SRR (Scene Release Rescue)", StringComparison.Ordinal));

    [AvaloniaFact]
    public void CompactEntry_HelpStartsCollapsed_BodyReachable_ExpanderResetsOnReentry()
    {
        CreatorViewModel vm = CreateVm();
        var view = new CreatorView { DataContext = vm };
        (Window window, Grid root) = CompactViewRig.HostAt(view, CompactInner);
        try
        {
            Expander helpDisclosure = root.GetVisualDescendants().OfType<Expander>().Single(e => e.Name == "HelpDisclosure");
            Assert.False(helpDisclosure.IsExpanded); // condition 5: compact entry starts collapsed

            helpDisclosure.IsExpanded = true;
            Dispatcher.UIThread.RunJobs();

            ScrollViewer body = helpDisclosure.GetVisualDescendants().OfType<ScrollViewer>().Single();
            Assert.True(body.Focusable);
            Assert.True(body.IsEffectivelyEnabled);

            // Anchor the walk at the Help toggle — compact mode's own first stop. Anchoring
            // explicitly keeps this assertion about the body scroller's reachability rather than
            // about wherever a blind first Tab happens to land; the cold-start entry point has its
            // own test.
            ToggleButton helpToggle = helpDisclosure.GetVisualDescendants().OfType<ToggleButton>().Single();
            helpToggle.Focus();
            Dispatcher.UIThread.RunJobs();
            CompactViewRig.AssertReachableByKeyboard(window, body);

            // Restore to normal, then re-enter compact: durability is compact-SESSION scoped only.
            // Out of compact and comfortably clear of the restore hysteresis, DERIVED rather
            // than a fixed delta: a constant step that clears the switch point on one platform's
            // font metrics can land inside the hysteresis band on another's, leaving this test
            // asserting normal-mode behaviour on a view that never left compact.
            double restoreDelta = (Threshold + 12 + CompactInvariantRig.ExpandedHeadroom) - CompactInner;
            window.Height += restoreDelta;
            Dispatcher.UIThread.RunJobs();
            Assert.DoesNotContain("compactHeight", root.Classes);
            Assert.True(helpDisclosure.IsExpanded); // flat mode: force-expanded

            // The staged-focus guard's actual point: restoring from a focus captured on the body
            // (which just went non-focusable — flat mode's base style, not the compact-only
            // override) must relocate focus, not strand it. RestoreFocusTarget was wired to
            // OutputTextBox in the view's ctor (NOT InputTextBox — recovery lands on a field
            // partway down the form rather than resetting the user to the first row; see the
            // ctor's own remarks for the full rationale and its limits), so that is where it must
            // land.
            TextBox outputTextBox = window.GetVisualDescendants().OfType<TextBox>().Single(t => t.Name == "OutputTextBox");
            Assert.True(outputTextBox.IsFocused,
                "restoring from a focused compact body must relocate focus to the wired RestoreFocusTarget (OutputTextBox), not strand it");
            Assert.Equal("Output path", ControlAutomationPeer.CreatePeerForElement(outputTextBox).GetName());

            window.Height -= restoreDelta;
            Dispatcher.UIThread.RunJobs();
            Assert.Contains("compactHeight", root.Classes);
            Assert.False(helpDisclosure.IsExpanded, "re-entering compact must reset Help to collapsed, not resume the prior session's open state");
        }
        finally { window.Close(); }
    }

    /// <summary>
    /// Regression guard for the keyboard trap this view USED to have, exercised from the entry
    /// point a keyboard-only user actually meets: the window opens with nothing focused and they
    /// press Tab. Every other tab-order test in this file starts from a focused sentinel INSIDE the
    /// form, which entered the order somewhere the trap did not hold — which is why it survived
    /// them all.
    /// <para>
    /// Before the fix, this walk never left the Input row: TabIndex 0/1/2 on those three controls
    /// were compared against the whole window's navigation scope, where every other control carries
    /// the default (int.MaxValue), so the three sorted ahead of the entire form and the walk cycled
    /// among them and the shell chrome forever. Stored Files, Output, Options, Create SRR and the
    /// log were unreachable by keyboard from a cold start.
    /// </para>
    /// <para>
    /// Asserted against the far side of the form specifically, not merely "focus moved": the trap
    /// moved focus perfectly well, round and round. Reaching the primary action is what proves the
    /// order goes somewhere.
    /// </para>
    /// </summary>
    [AvaloniaFact]
    public void ColdStartTabWalk_EscapesTheInputRow_AndReachesThePrimaryAction()
    {
        const int MaxSteps = 40;   // the documented reproduction used ~30; this leaves headroom

        CreatorViewModel vm = CreateVm();

        // The primary action is command-gated on both paths being set, and a DISABLED button is
        // correctly skipped by Tab — so without this the walk could pass the action band and the
        // test would be measuring the command's CanExecute rather than the tab order.
        vm.InputPath = @"C:\release\movie.sfv";
        vm.OutputPath = @"C:\release\movie.srr";

        var view = new CreatorView { DataContext = vm };
        (Window window, Grid root) = CompactViewRig.HostAt(view, ExpandedInner);
        try
        {
            Button createButton = window.GetVisualDescendants().OfType<Button>().Single(b => b.Content is "Create SRR");
            TextBox inputTextBox = window.GetVisualDescendants().OfType<TextBox>().Single(t => t.Name == "InputTextBox");
            Assert.True(createButton.IsEffectivelyEnabled,
                "test precondition: the primary action must be enabled, or Tab skipping it would prove nothing");

            // Cold start: no sentinel, nothing focused — the state a freshly-opened window is in.
            Assert.Null(window.FocusManager?.GetFocusedElement());

            List<Control> forward = [];
            int reachedAt = -1;
            for (int step = 0; step < MaxSteps && reachedAt < 0; step++)
            {
                if (CompactViewRig.StepFocus(window, forward: true) is not { } focused)
                {
                    break;
                }

                forward.Add(focused);
                if (ReferenceEquals(focused, createButton))
                {
                    reachedAt = step;
                }
            }

            Assert.True(reachedAt >= 0,
                $"cold-start Tab never reached the Create SRR button in {MaxSteps} steps — the walk was: " +
                Trail(forward));

            // Reverse from the far side must travel back THROUGH the Input row rather than bouncing
            // in a cycle of its own — the row is reachable from both directions or it is not
            // reachable.
            List<Control> reverse = [];
            bool reachedInput = false;
            for (int step = 0; step < MaxSteps && !reachedInput; step++)
            {
                if (CompactViewRig.StepFocus(window, forward: false) is not { } focused)
                {
                    break;
                }

                reverse.Add(focused);
                reachedInput = ReferenceEquals(focused, inputTextBox);
            }

            Assert.True(reachedInput,
                $"Shift+Tab from the primary action never returned to the Input path box in {MaxSteps} steps — " +
                "the walk was: " + Trail(reverse));
        }
        finally { window.Close(); }
    }

    private static string Trail(IEnumerable<Control> walk) =>
        string.Join(" -> ", walk.Select(CompactViewRig.Describe));

    /// <summary>
    /// Both path rows keep keyboard order equal to VISUAL order, and the reason they need help
    /// doing so is pinned rather than merely described.
    /// <para>
    /// Each row is a <c>DockPanel</c> whose Browse buttons are docked Right and therefore declared
    /// FIRST — docking consumes edges in declaration order, so the rightmost control has to come
    /// first in the markup. The tree order is consequently the exact REVERSE of what the user sees,
    /// and left alone a keyboard user tabs the row backwards. Explicit <c>TabIndex</c> plus
    /// <c>KeyboardNavigation.TabNavigation="Local"</c> is what corrects it: the pins order the row
    /// internally, Local keeps them from being compared against the whole window (which is what
    /// trapped the Input row).
    /// </para>
    /// <para>
    /// The INVERSION itself is asserted, not assumed. If someone ever reorders the markup so tree
    /// order already matches visual order, the pins become unnecessary and this test says so —
    /// rather than silently passing and leaving dead scaffolding behind.
    /// </para>
    /// </summary>
    [AvaloniaFact]
    public void PathRows_TabOrderFollowsVisualOrder_DespiteReversedTreeOrder()
    {
        CreatorViewModel vm = CreateVm();
        var view = new CreatorView { DataContext = vm };
        (Window window, Grid root) = CompactViewRig.HostAt(view, ExpandedInner);
        try
        {
            // Expected UIA names are LITERAL here, never read back off the controls: a test that
            // derives them from the very controls it is checking passes through any rename,
            // including one that strips a name back to a bare "Browse".
            TextBox inputTextBox = window.GetVisualDescendants().OfType<TextBox>().Single(t => t.Name == "InputTextBox");
            Button inputBrowse = window.GetVisualDescendants().OfType<Button>().Single(b => ReferenceEquals(b.Command, vm.BrowseInputCommand));
            Button inputBrowseFolder = window.GetVisualDescendants().OfType<Button>().Single(b => ReferenceEquals(b.Command, vm.BrowseInputFolderCommand));
            AssertRowOrder(window, "Input",
            [
                (inputTextBox, "Input path"),
                (inputBrowse, "Browse for input file"),
                // NOT "Browse for release folder", and this is load-bearing rather than an
                // oversight in the naming pass: this button's visible Content is "Browse folder…",
                // and WCAG 2.5.3 requires the accessible name to CONTAIN it. It is the one Browse
                // button in the app whose Content is not the bare word, so it is the one that
                // cannot take the shared phrasing. CreatorViewFolderBindingTests'
                // FolderBrowseButton_HasLabelInName_AccessibleName pins the same string from the
                // Label-in-Name side.
                (inputBrowseFolder, "Browse folder for release input"),
            ]);

            TextBox outputTextBox = window.GetVisualDescendants().OfType<TextBox>().Single(t => t.Name == "OutputTextBox");
            Button outputBrowse = window.GetVisualDescendants().OfType<Button>().Single(b => ReferenceEquals(b.Command, vm.BrowseOutputCommand));
            AssertRowOrder(window, "Output",
            [
                (outputTextBox, "Output path"),
                (outputBrowse, "Browse for output path"),
            ]);
        }
        finally { window.Close(); }
    }

    /// <summary>
    /// Asserts one path row five ways, in this order:
    /// <list type="number">
    /// <item>every control announces the exact name <paramref name="visualRow"/> declares for it;</item>
    /// <item>the markup order (<c>DockPanel.Children</c>) is the REVERSE of that row's order — the
    /// premise the TabIndex pins exist to correct;</item>
    /// <item>the UIA child order mirrors that same reversal, which is what an assistive technology
    /// walking the tree structurally actually meets (see the comment at that assertion — it is the
    /// one with a user-visible consequence the pins cannot fix);</item>
    /// <item>the rendered left-to-right order really is <paramref name="visualRow"/>'s order;</item>
    /// <item>Tab walks it in that order too.</item>
    /// </list>
    /// </summary>
    private static void AssertRowOrder(
        Window window, string rowName, IReadOnlyList<(Control Control, string ExpectedName)> visualRow)
    {
        List<Control> visualOrder = [.. visualRow.Select(entry => entry.Control)];
        var dockPanel = (DockPanel)visualOrder[0].GetVisualParent()!;

        // Every control announces itself, which is what keeps the reversed UIA order below merely
        // surprising rather than ambiguous — two of this view's buttons render the bare word
        // "Browse" (the Input row's file Browse and the Output row's), so content alone would not
        // distinguish them.
        foreach ((Control control, string expectedName) in visualRow)
        {
            Assert.Equal(expectedName, ControlAutomationPeer.CreatePeerForElement(control).GetName());
        }

        List<Control> treeOrder = [.. dockPanel.Children.OfType<Control>()];
        List<Control> expectedTreeOrder = [.. Enumerable.Reverse(visualOrder)];
        Assert.True(treeOrder.Count == expectedTreeOrder.Count,
            $"{rowName} row: expected {expectedTreeOrder.Count} children, found {treeOrder.Count}");
        for (int i = 0; i < treeOrder.Count; i++)
        {
            Assert.True(ReferenceEquals(treeOrder[i], expectedTreeOrder[i]),
                $"{rowName} row: markup order should be the REVERSE of visual order (docking consumes " +
                $"edges in declaration order) — position {i} holds {CompactViewRig.Describe(treeOrder[i])}, " +
                $"expected {CompactViewRig.Describe(expectedTreeOrder[i])}. If the markup no longer " +
                "inverts, the TabIndex pins on this row have nothing left to correct and should go.");
        }

        // The same reversal is what an ASSISTIVE TECHNOLOGY sees. A UIA tree-walker reads the
        // automation peer tree, which follows the children order above, NOT the TabIndex order —
        // so a screen-reader user navigating this row structurally (rather than by Tab) meets
        // Browse before the path box. Recorded as the known consequence of docking right-first:
        // the pins fix keyboard order and cannot fix tree order, and the row's controls each carry
        // their own AutomationProperties.Name so the reading is unambiguous either way.
        IReadOnlyList<AutomationPeer> peerChildren =
            ControlAutomationPeer.CreatePeerForElement(dockPanel).GetChildren() ?? [];
        List<string> peerOrder = [.. peerChildren.Select(p => p.GetName() ?? string.Empty)];
        List<string> expectedPeerOrder = [.. visualRow.Select(entry => entry.ExpectedName).Reverse()];
        Assert.True(peerOrder.SequenceEqual(expectedPeerOrder),
            $"{rowName} row: the UIA child order should mirror the markup order (reverse of visual) — " +
            $"got [{string.Join(", ", peerOrder)}], expected [{string.Join(", ", expectedPeerOrder)}]");

        for (int i = 1; i < visualOrder.Count; i++)
        {
            Assert.True(visualOrder[i - 1].Bounds.X < visualOrder[i].Bounds.X,
                $"{rowName} row: {CompactViewRig.Describe(visualOrder[i - 1])} should render left of " +
                $"{CompactViewRig.Describe(visualOrder[i])}");
        }

        visualOrder[0].Focus();
        Dispatcher.UIThread.RunJobs();
        for (int i = 1; i < visualOrder.Count; i++)
        {
            Control? next = CompactViewRig.StepFocus(window, forward: true);
            Assert.True(ReferenceEquals(next, visualOrder[i]),
                $"{rowName} row: Tab from {CompactViewRig.Describe(visualOrder[i - 1])} should reach " +
                $"{CompactViewRig.Describe(visualOrder[i])}, not " +
                $"{(next is null ? "<nothing>" : CompactViewRig.Describe(next))}");
        }
    }

    /// <summary>
    /// Pins WHERE a resize-triggered focus recovery lands, which is a preference rather than a
    /// safety requirement now that the Input row's keyboard trap is fixed.
    /// <para>
    /// This guard was originally about that trap: landing recovery on one of the Input row's three
    /// pinned controls would have deposited a keyboard user inside it. Scoping the pins
    /// (KeyboardNavigation.TabNavigation="Local") removed the hazard, so the three negative
    /// assertions below no longer defend against harm — they defend a retained choice: recovery
    /// lands on a named, always-present field partway down the form rather than resetting the user
    /// to the very first row. That is the whole claim. The Options checkboxes and the App-name
    /// field sit between OutputTextBox and the primary action, so it is NOT "the last field before
    /// Create SRR", and no stronger claim than "not the top" is being made for it.
    /// </para>
    /// <para>
    /// Resolves the ACTUAL wired target via <see cref="CompactHeightBehavior.GetRestoreFocusTarget"/>
    /// rather than assuming what it "should" be, so a future retarget shows up here as a decision to
    /// re-make rather than a silent drift.
    /// </para>
    /// </summary>
    [AvaloniaFact]
    public void RestoreFocusTarget_PrefersTheOutputFieldOverTheTopOfTheForm()
    {
        CreatorViewModel vm = CreateVm();
        var view = new CreatorView { DataContext = vm };
        (Window window, Grid root) = CompactViewRig.HostAt(view, ExpandedInner);
        try
        {
            Control? actualTarget = CompactHeightBehavior.GetRestoreFocusTarget(root);
            Assert.NotNull(actualTarget);

            TextBox inputTextBox = window.GetVisualDescendants().OfType<TextBox>().Single(t => t.Name == "InputTextBox");
            Button inputBrowse = window.GetVisualDescendants().OfType<Button>().Single(b => ReferenceEquals(b.Command, vm.BrowseInputCommand));
            Button inputBrowseFolder = window.GetVisualDescendants().OfType<Button>().Single(b => ReferenceEquals(b.Command, vm.BrowseInputFolderCommand));

            Assert.False(ReferenceEquals(actualTarget, inputTextBox),
                "RestoreFocusTarget should not be InputTextBox — a resize would throw focus back to the top of the form.");
            Assert.False(ReferenceEquals(actualTarget, inputBrowse),
                "RestoreFocusTarget should not be the input Browse button — a resize would throw focus back to the top of the form.");
            Assert.False(ReferenceEquals(actualTarget, inputBrowseFolder),
                "RestoreFocusTarget should not be the input Browse-folder button — a resize would throw focus back to the top of the form.");

            // Positive assertion, not just three negatives: the actual wired target is OutputTextBox,
            // carrying its own explicit, computed accessible name.
            TextBox outputTextBox = window.GetVisualDescendants().OfType<TextBox>().Single(t => t.Name == "OutputTextBox");
            Assert.True(ReferenceEquals(actualTarget, outputTextBox));
            Assert.Equal("Output path", ControlAutomationPeer.CreatePeerForElement(outputTextBox).GetName());
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void HelpOpenDonation_ConfigRowMin80_BodyMaxHeight40_AppNameKeyboardReachable_StoredFilesRowStaysAt80()
    {
        CreatorViewModel vm = CreateVm();
        var view = new CreatorView { DataContext = vm };
        (Window window, Grid root) = CompactViewRig.HostAt(view, CompactInner);
        try
        {
            Expander helpDisclosure = root.GetVisualDescendants().OfType<Expander>().Single(e => e.Name == "HelpDisclosure");
            helpDisclosure.IsExpanded = true;
            Dispatcher.UIThread.RunJobs();
            Assert.True(CompactHeightBehavior.GetHelpOpen(root));

            int configRow = Grid.GetRow(window.GetVisualDescendants().OfType<ScrollViewer>().Single(sv => Grid.GetRow(sv) == 1));
            Assert.Equal(80, root.RowDefinitions[configRow].MinHeight);

            ScrollViewer body = helpDisclosure.GetVisualDescendants().OfType<ScrollViewer>().Single();
            Assert.Equal(40, body.MaxHeight);

            // Anchor at "Add..." so this assertion is about the target's reachability from inside
            // the form rather than about wherever a blind first Tab lands; the cold-start entry
            // point has its own test.
            Button keyboardAnchor = window.GetVisualDescendants().OfType<Button>().Single(b => ReferenceEquals(b.Command, vm.AddStoredFileCommand));
            keyboardAnchor.Focus();
            Dispatcher.UIThread.RunJobs();

            TextBox appName = window.GetVisualDescendants().OfType<TextBox>().Single(t => t.Width == 400);
            CompactViewRig.AssertReachableByKeyboard(window, appName);

            // The DESCENDANT row (ConfigGrid row 3, Stored Files) shares the SAME HelpOpenMinHeight
            // (80) as its own CompactMinHeight — donation while Help is open does not further
            // shrink the Stored Files grid beyond its already-compact floor.
            Grid configGrid = window.GetVisualDescendants().OfType<Grid>().Single(g => g.Name == "ConfigGrid");
            Assert.Equal(80, configGrid.RowDefinitions[3].Height.Value);
            Assert.Equal(80, configGrid.RowDefinitions[3].MinHeight);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void CompactBodyScroller_NotFocusableNormally_FocusableAndNamedInCompact()
    {
        CreatorViewModel vm = CreateVm();
        var normalView = new CreatorView { DataContext = vm };
        (Window normalWindow, Grid normalRoot) = CompactViewRig.HostAt(normalView, ExpandedInner);
        try
        {
            // Flat mode force-expands the body (so this scroller IS realized/attached even though
            // the header stays hidden) — it must NOT become a new Tab stop.
            ScrollViewer body = normalRoot.GetVisualDescendants().OfType<Expander>().Single(e => e.Name == "HelpDisclosure")
                .GetVisualDescendants().OfType<ScrollViewer>().Single();
            Assert.False(body.Focusable);
        }
        finally { normalWindow.Close(); }

        CreatorViewModel vm2 = CreateVm();
        var compactView = new CreatorView { DataContext = vm2 };
        (Window compactWindow, Grid compactRoot) = CompactViewRig.HostAt(compactView, CompactInner);
        try
        {
            Expander helpDisclosure = compactRoot.GetVisualDescendants().OfType<Expander>().Single(e => e.Name == "HelpDisclosure");
            helpDisclosure.IsExpanded = true;
            Dispatcher.UIThread.RunJobs();

            ScrollViewer body = helpDisclosure.GetVisualDescendants().OfType<ScrollViewer>().Single();
            Assert.True(body.Focusable);
            // The COMPUTED peer name, not merely the raw attached
            // property — what a screen reader actually announces (mirrors the same
            // ControlAutomationPeer.CreatePeerForElement(...).GetName() pattern used throughout
            // this file for every other focus-recovery/labeled target).
            Assert.Equal("Help content", ControlAutomationPeer.CreatePeerForElement(body).GetName());
        }
        finally { compactWindow.Close(); }
    }

    /// <summary>
    /// All four built-ins exercised with genuine key input against a REAL, attached ScrollViewer —
    /// never a synthetic Offset-setter poke. This view's own intro prose is short enough that it
    /// never genuinely overflows the 40-DIP donation cap at the app's own enforced minimum width,
    /// so — mirroring every other converted view's own identical finding — the body's Text is
    /// temporarily lengthened (synthetic content, this test only) so the four keys can be proven
    /// against REAL overflow.
    /// </summary>
    [AvaloniaFact]
    public void CompactBodyScroller_AllFourPageKeys_MoveOffsetBothWaysAndToExtents()
    {
        CreatorViewModel vm = CreateVm();
        var view = new CreatorView { DataContext = vm };
        (Window window, Grid root) = CompactViewRig.HostAt(view, CompactInner);
        try
        {
            Expander helpDisclosure = root.GetVisualDescendants().OfType<Expander>().Single(e => e.Name == "HelpDisclosure");
            helpDisclosure.IsExpanded = true;
            Dispatcher.UIThread.RunJobs();

            ScrollViewer body = helpDisclosure.GetVisualDescendants().OfType<ScrollViewer>().Single();
            TextBlock introText = body.GetVisualDescendants().OfType<TextBlock>().Single();
            introText.Text = string.Concat(Enumerable.Repeat("Create an SRR from a RAR archive set. ", 20));
            Dispatcher.UIThread.RunJobs();

            body.Focus();
            Dispatcher.UIThread.RunJobs();
            Assert.True(body.IsFocused);

            Assert.True(body.Extent.Height > body.Viewport.Height + 1,
                $"test precondition: body content ({body.Extent.Height:F1}) must exceed its viewport " +
                $"({body.Viewport.Height:F1}) to be genuinely scrollable");

            PressKey(window, PhysicalKey.PageDown);
            double afterPageDown = body.Offset.Y;
            Assert.True(afterPageDown > 0, "PageDown must increase Offset.Y");

            PressKey(window, PhysicalKey.PageUp);
            Assert.True(body.Offset.Y < afterPageDown, "PageUp must decrease Offset.Y");

            PressKey(window, PhysicalKey.End);
            Assert.Equal(body.Extent.Height - body.Viewport.Height, body.Offset.Y, precision: 1);

            PressKey(window, PhysicalKey.Home);
            Assert.Equal(0, body.Offset.Y, precision: 1);
        }
        finally { window.Close(); }
    }

    private static void PressKey(Window window, PhysicalKey key)
    {
        window.KeyPressQwerty(key, RawInputModifiers.None);
        window.KeyReleaseQwerty(key, RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();
    }

    // ── 5. Pinned band (the defect this task exists to fix) ────────────

    /// <summary>
    /// Directly asserts the defect the whole task exists to fix: with band 1 (config, holding the
    /// Input/Stored-Files/Output/Options sections AND the StoredFilesGrid+splitter)
    /// independently scrolled to its top AND its bottom extreme, the pinned Create SRR button
    /// stays fully inside the window the entire time, with Cancel + ProgressMessage + ProgressBar
    /// all forced visible. RED-FIRST: pre-change (today's plain Grid, no scroll clipping at all —
    /// row 6's bottom half is simply pushed off / crushed at 700x450), the equivalent button is
    /// either clipped or measures outside the window under these exact conditions.
    /// </summary>
    [AvaloniaFact]
    public void PinnedActionBand_CreateSRRButtonStaysWithinWindow_BandOneScrolledToTopAndBottom()
    {
        CreatorViewModel vm = CreateVm();
        ForceWorstCase(vm);
        var view = new CreatorView { DataContext = vm };
        (Window window, Grid root) = CompactViewRig.HostAt(view, CompactInner);
        try
        {
            Assert.Contains("compactHeight", root.Classes);
            Button createButton = window.GetVisualDescendants().OfType<Button>().Single(b => b.Content is "Create SRR");
            Button cancelButton = window.GetVisualDescendants().OfType<Button>().Single(b => b.Content is "Cancel");
            Assert.True(cancelButton.IsVisible);

            ScrollViewer configScroller = window.GetVisualDescendants().OfType<ScrollViewer>().Single(sv => Grid.GetRow(sv) == 1);
            Assert.True(configScroller.Extent.Height > configScroller.Viewport.Height,
                "test precondition: band 1 must genuinely overflow so top/bottom are distinct positions");

            configScroller.Offset = new Vector(0, 0);
            Dispatcher.UIThread.RunJobs();
            AssertFullyWithinWindow(createButton, window);

            configScroller.Offset = new Vector(0, configScroller.Extent.Height - configScroller.Viewport.Height);
            Dispatcher.UIThread.RunJobs();
            AssertFullyWithinWindow(createButton, window);
        }
        finally { window.Close(); }
    }

    // ── 6. Stored-files row: splitter drag + compact scroll + wheel handoff ────

    /// <summary>
    /// MEASURED: 10 ArrowDown presses on the focused, keyboard-operable GridSplitter grows
    /// ConfigGrid row 3 by 100 DIPs (10 DIPs/press) at NORMAL size — mirrors
    /// <c>ReconstructorCompactTests.Splitter_FocusableAndNamed_UpDownResizes_...</c>'s own real,
    /// input-driven drag mechanism (never a synthetic RowDefinition.Height poke). ROW 5 (Output,
    /// Auto) needs no explicit floor here — the config band's own ScrollViewer has ample slack at
    /// this VM's default (near-empty) content, so growing row 3 consumes that slack rather than
    /// needing to shrink row 5 at all (confirmed directly: row 5 stays <c>Auto</c> throughout).
    /// <para>
    /// The drag survives a compact round-trip via <see cref="CompactRowMode.PixelRestore"/>'s own
    /// ALREADY-behavior-level-proven capture (<c>CompactHeightBehaviorTests.
    /// RowSizes_ApplyOnCompact_RestorePreservingSplitterDrag</c>) — this is that mechanism's first
    /// exercise through a REAL, shipped view rather than a synthetic two-row test grid.
    /// </para>
    /// </summary>
    [AvaloniaFact]
    public void StoredFilesRow_SplitterDragAtNormalSize_ResizesRow_AndDragSurvivesCompactRoundTrip()
    {
        CreatorViewModel vm = CreateVm();
        var view = new CreatorView { DataContext = vm };
        (Window window, Grid root) = CompactViewRig.HostAt(view, ExpandedInner);
        try
        {
            Grid configGrid = window.GetVisualDescendants().OfType<Grid>().Single(g => g.Name == "ConfigGrid");
            GridSplitter splitter = window.GetVisualDescendants().OfType<GridSplitter>().Single();

            Assert.Equal(150, configGrid.RowDefinitions[3].Height.Value);

            splitter.Focus();
            Dispatcher.UIThread.RunJobs();
            Assert.True(splitter.IsFocused);

            for (int i = 0; i < 10; i++)
            {
                window.KeyPressQwerty(PhysicalKey.ArrowDown, RawInputModifiers.None);
                window.KeyReleaseQwerty(PhysicalKey.ArrowDown, RawInputModifiers.None);
                Dispatcher.UIThread.RunJobs();
            }

            double draggedHeight = configGrid.RowDefinitions[3].Height.Value;
            Assert.True(draggedHeight > 150, $"drag must genuinely resize row 3, was {draggedHeight:F1}");

            // Round-trip: compact overwrites to the descendant PixelRestore compact minimum (80)...
            window.Height = CompactInner + ChromeOverheadFor(window, root);
            Dispatcher.UIThread.RunJobs();
            Assert.Contains("compactHeight", root.Classes);
            Assert.Equal(80, configGrid.RowDefinitions[3].Height.Value);
            Assert.Equal(80, configGrid.RowDefinitions[3].MinHeight);

            // ...and restoring must recover the DRAGGED height, not merely the original 150.
            // Threshold+12 (RestoreSlack), NOT ExpandedInner (Threshold+1): CompactHeightBehavior's
            // restore-only hysteresis needs height >= Threshold+12 to re-expand an ALREADY-compact
            // instance — ExpandedInner is only sufficient for a FRESH instance's first evaluation.
            window.Height = (Threshold + 12) + ChromeOverheadFor(window, root);
            Dispatcher.UIThread.RunJobs();
            Assert.DoesNotContain("compactHeight", root.Classes);
            Assert.True(Math.Abs(draggedHeight - configGrid.RowDefinitions[3].Height.Value) < 0.5,
                $"restoring from compact must recover the user's DRAGGED height ({draggedHeight:F1}), not just the authored NormalHeight (150) — got {configGrid.RowDefinitions[3].Height.Value:F1}");
        }
        finally { window.Close(); }
    }

    /// <summary>
    /// <see cref="CompactViewRig.HostAt"/> only ever sets <c>window.Height</c> ONCE per call
    /// (never re-targets an already-shown window — see its own remarks on why a transient
    /// under/over-shoot could wrongly latch <c>CompactHeightBehavior</c>'s hysteresis). This test
    /// genuinely needs to resize the SAME live window twice (drag, then compact, then restore) to
    /// prove the round-trip, so it reproduces the rig's own chrome-overhead arithmetic directly on
    /// the ALREADY-open window instead.
    /// </summary>
    private static double ChromeOverheadFor(Window window, Grid innerRoot) => window.Height - innerRoot.Bounds.Height;

    /// <summary>
    /// In compact mode the Stored Files row is fixed at its 80-DIP PixelRestore floor regardless of
    /// content — with enough rows to exceed that height, the DataGrid's OWN internal virtualized
    /// scrollbar (not the outer config-band ScrollViewer) is what reaches the remaining rows.
    /// </summary>
    [AvaloniaFact]
    public void CompactMode_StoredFilesRowIsEighty_GridScrollsInternally()
    {
        CreatorViewModel vm = CreateVm();
        for (int i = 0; i < 8; i++)
        {
            vm.StoredFiles.Add(Item($@"C:\release\file{i:D2}.nfo", $"file{i:D2}.nfo"));
        }
        var view = new CreatorView { DataContext = vm };
        (Window window, Grid root) = CompactViewRig.HostAt(view, CompactInner);
        try
        {
            Assert.Contains("compactHeight", root.Classes);
            Grid configGrid = window.GetVisualDescendants().OfType<Grid>().Single(g => g.Name == "ConfigGrid");
            Assert.Equal(80, configGrid.RowDefinitions[3].Height.Value);
            Assert.Equal(80, configGrid.RowDefinitions[3].MinHeight);

            DataGrid grid = window.GetVisualDescendants().OfType<DataGrid>().Single(g => g.Name == "StoredFilesGrid");
            Assert.True(grid.Bounds.Height <= 80 + 0.5, $"grid's own rendered height ({grid.Bounds.Height:F1}) should be pinned to the 80-DIP compact row");

            ScrollBar gridBar = grid.GetVisualDescendants().OfType<ScrollBar>().Single(b => b.Orientation == Orientation.Vertical);
            Assert.True(gridBar.Maximum > 0, "8 rows inside an 80-DIP grid must need the grid's OWN internal virtualization scroll");
        }
        finally { window.Close(); }
    }

    /// <summary>
    /// Real arrow-key/current-row visibility coverage for
    /// StoredFilesGrid's own <c>ScrollHandoffBehavior.Handoff="True"</c> wiring — this needs its
    /// own coverage because, UNLIKE SampleRestorer's SRSEntriesGrid,
    /// this grid has no per-row focusable control (a plain two-column text grid, no checkbox
    /// column) to individually target. Mirrors SampleRestorer's own
    /// <c>Handoff_KeyboardNavigation_ChainsBringIntoViewToOuterViewer_BottomRowEndsFullyVisible</c>
    /// adapted to that difference: focuses the GRID CONTROL ITSELF directly (confirmed, via a
    /// throwaway diagnostic, to be the genuine keyboard entry point here — ordinary arrow-key
    /// browsing on this grid shape never moves focus off the grid onto a cell/row, only its own
    /// <c>SelectedIndex</c>/current-cell state, exactly as <c>ScrollHandoffBehavior</c>'s own
    /// remarks document for DataGrid generally), then drives real ArrowDown presses deep into a
    /// 12-row, virtualized, compact (80-DIP) grid and asserts the LAST row ends fully
    /// clip-aware-visible in the window — proving <c>ScrollHandoffBehavior</c>'s
    /// <c>CurrentCellChanged</c>-&gt;<c>BringIntoView</c> chain reaches the config band's own outer
    /// ScrollViewer even without a per-row control to focus.
    /// </summary>
    [AvaloniaFact]
    public void Handoff_KeyboardArrowNavigation_ChainsBringIntoViewToOuterViewer_LastRowEndsFullyVisible()
    {
        CreatorViewModel vm = CreateVm();
        for (int i = 0; i < 12; i++)
        {
            vm.StoredFiles.Add(Item($@"C:\release\file{i:D2}.nfo", $"file{i:D2}.nfo"));
        }
        var view = new CreatorView { DataContext = vm };
        (Window window, Grid root) = CompactViewRig.HostAt(view, CompactInner);
        try
        {
            ScrollViewer configScroller = window.GetVisualDescendants().OfType<ScrollViewer>().Single(sv => Grid.GetRow(sv) == 1);
            configScroller.Offset = default;
            Dispatcher.UIThread.RunJobs();

            DataGrid grid = window.GetVisualDescendants().OfType<DataGrid>().Single(g => g.Name == "StoredFilesGrid");
            grid.Focus();
            Dispatcher.UIThread.RunJobs();
            Assert.True(grid.IsFocused, "test precondition: the grid control itself (not a per-row control, which this grid shape has none of) must be the genuine keyboard entry point");

            for (int i = 0; i < vm.StoredFiles.Count - 1; i++)
            {
                window.KeyPressQwerty(PhysicalKey.ArrowDown, RawInputModifiers.None);
                window.KeyReleaseQwerty(PhysicalKey.ArrowDown, RawInputModifiers.None);
                Dispatcher.UIThread.RunJobs();
            }

            Assert.Equal(vm.StoredFiles.Count - 1, grid.SelectedIndex);
            Assert.True(grid.IsFocused, "focus should stay on the grid control throughout ordinary (non-edit) arrow-key browsing");

            DataGridRow lastRow = grid.GetVisualDescendants().OfType<DataGridRow>()
                .Single(r => ReferenceEquals(r.DataContext, vm.StoredFiles[^1]));
            AssertFullyWithinWindow(lastRow, window);
        }
        finally { window.Close(); }
    }

    /// <summary>
    /// Wheel at the grid's own extent moves the config band's OWN (band 1) scroller — the platform
    /// default (<c>ScrollViewer.IsScrollChainingEnabled</c>, never overridden in this app), NOT a
    /// custom mechanism: <see cref="ScrollHandoffBehavior"/>'s own wheel path was removed entirely
    /// after being proven redundant with the platform default. Mirrors
    /// <c>SampleRestorerCompactTests.WheelHandoffAtGridExtent_...</c>'s identical
    /// regression guard, adapted: this grid sits at a fixed ConfigGrid ROW (not inside a StackPanel
    /// section), and the grid needs no separate "reveal" stage the way SampleRestorer's did — the
    /// Stored Files row is close enough to compact band 1's own top that a couple of ticks already
    /// bring some sliver of the grid into view from the default (offset-zero) start.
    /// </summary>
    [AvaloniaFact]
    public void WheelHandoffAtGridExtent_PlatformDefaultMovesConfigBandScroller()
    {
        CreatorViewModel vm = CreateVm();
        for (int i = 0; i < 12; i++)
        {
            vm.StoredFiles.Add(Item($@"C:\release\file{i:D2}.nfo", $"file{i:D2}.nfo"));
        }
        var view = new CreatorView { DataContext = vm };
        (Window window, Grid root) = CompactViewRig.HostAt(view, CompactInner);
        try
        {
            DataGrid grid = window.GetVisualDescendants().OfType<DataGrid>().Single(g => g.Name == "StoredFilesGrid");
            ScrollBar gridBar = grid.GetVisualDescendants().OfType<ScrollBar>().Single(b => b.Orientation == Orientation.Vertical);
            ScrollViewer configScroller = window.GetVisualDescendants().OfType<ScrollViewer>().Single(sv => Grid.GetRow(sv) == 1);

            const int MaxRevealTicks = 200;
            for (int tick = 0; tick < MaxRevealTicks && TryVisibleCenterInWindow(grid, window) is null; tick++)
            {
                window.MouseWheel(VisibleCenterInWindow(configScroller, window), new Vector(0, -1));
                Dispatcher.UIThread.RunJobs();
            }
            Assert.NotNull(TryVisibleCenterInWindow(grid, window)); // test precondition: the grid must have SOME visible sliver to wheel "at" it

            const int MaxDriveTicks = 200;
            for (int tick = 0; tick < MaxDriveTicks && gridBar.Value < gridBar.Maximum; tick++)
            {
                window.MouseWheel(VisibleCenterInWindow(grid, window), new Vector(0, -1));
                Dispatcher.UIThread.RunJobs();
            }
            Assert.True(gridBar.Value >= gridBar.Maximum, "test precondition: the grid must genuinely reach its own bottom extent");

            Assert.True(configScroller.Extent.Height > configScroller.Viewport.Height,
                "test precondition: the config band must have genuine room to scroll for the hand-off to be observable");

            double gridBefore = gridBar.Value;
            double outerBefore = configScroller.Offset.Y;

            window.MouseWheel(VisibleCenterInWindow(grid, window), new Vector(0, -1));
            Dispatcher.UIThread.RunJobs();

            Assert.Equal(gridBefore, gridBar.Value); // the grid itself did not move further
            Assert.True(configScroller.Offset.Y > outerBefore,
                $"the config band's own scroller should have moved from {outerBefore}, was {configScroller.Offset.Y}");
        }
        finally { window.Close(); }
    }

    /// <summary>
    /// The center of <paramref name="element"/>'s TRUE visible region — the intersection of its own
    /// translated bounds against EVERY <c>ClipToBounds</c> ancestor's own translated bounds.
    /// Mirrors <c>SampleRestorerCompactTests</c>'s own identical helper. Returns null (never
    /// throws) when the element has no visible region at all.
    /// </summary>
    private static Point? TryVisibleCenterInWindow(Control element, Window window)
    {
        if (!element.IsAttachedToVisualTree() || !element.IsEffectivelyVisible)
        {
            return null;
        }

        if (TransformRect(element, new Rect(element.Bounds.Size), window) is not { } elementInWindow)
        {
            return null;
        }

        Rect visible = new(window.Bounds.Size);
        foreach (Visual ancestor in element.GetVisualAncestors())
        {
            if (ancestor is not Control clipper || !clipper.ClipToBounds)
            {
                continue;
            }

            if (TransformRect(clipper, new Rect(clipper.Bounds.Size), window) is not { } clipperInWindow)
            {
                return null;
            }

            visible = visible.Intersect(clipperInWindow);
        }

        visible = visible.Intersect(elementInWindow);
        return visible.Width > 0 && visible.Height > 0
            ? new Point(visible.X + (visible.Width / 2), visible.Y + (visible.Height / 2))
            : null;
    }

    private static Point VisibleCenterInWindow(Control element, Window window) =>
        TryVisibleCenterInWindow(element, window)
            ?? throw new InvalidOperationException($"{element.GetType().Name} has no visible (clip-aware) region at all.");

    // ── 7. Detected-sets bounding (verifying the EXISTING cap holds inside the new structure) ──

    [AvaloniaFact]
    public void DetectedSetsRegion_With12Sets_StaysWithinExisting96Cap_Compact() =>
        AssertDetectedSetsRegionStaysWithinCap(CompactInner, expectCompact: true);

    [AvaloniaFact]
    public void DetectedSetsRegion_With12Sets_StaysWithinExisting96Cap_Normal() =>
        AssertDetectedSetsRegionStaysWithinCap(ExpandedInner, expectCompact: false);

    private static void AssertDetectedSetsRegionStaysWithinCap(double innerHeight, bool expectCompact)
    {
        CreatorViewModel vm = CreateVm();
        for (int i = 0; i < 12; i++)
        {
            vm.DetectedSets.Add(new ReleaseSetInput($@"C:\release\disc{i:D2}\movie.sfv", $"disc{i:D2}/movie.sfv"));
        }
        var view = new CreatorView { DataContext = vm };
        (Window window, Grid root) = CompactViewRig.HostAt(view, innerHeight);
        try
        {
            Assert.Equal(expectCompact, root.Classes.Contains("compactHeight"));
            Assert.True(vm.HasDetectedSets);

            ScrollViewer detectedSetsScroller = window.GetVisualDescendants().OfType<ScrollViewer>().Single(sv => sv.MaxHeight == 96);
            Assert.True(detectedSetsScroller.IsVisible);
            Assert.True(detectedSetsScroller.Bounds.Height <= 96.5,
                $"detected-sets region height {detectedSetsScroller.Bounds.Height:F1} exceeds the existing 96-DIP cap");

            // The cap must be doing real work here, not merely never binding — 12 realistic-length
            // relative names comfortably exceed 96 DIPs of natural content height.
            Assert.True(detectedSetsScroller.Extent.Height > detectedSetsScroller.Viewport.Height,
                "test precondition: 12 detected sets must genuinely overflow the 96-DIP cap for it to prove anything");
        }
        finally { window.Close(); }
    }

    // ── LabeledBy audit: computed UIA names resolve to their own header ──

    /// <summary>
    /// Both grids/lists in this view use <c>AutomationProperties.LabeledBy</c> to pair themselves
    /// with a sibling header TextBlock (mirrors SampleRestorer's own
    /// <c>SRSEntriesGrid_UIAName_ResolvesToEmbeddedSRSFilesHeader</c>) — resolved via the REAL
    /// automation peer, not the raw attached property, so this proves what a screen reader
    /// actually announces on landing here, not merely that the XAML attribute exists.
    /// </summary>
    [AvaloniaFact]
    public void LabeledByAudit_StoredFilesGridAndLogList_ResolveToTheirOwnHeaders()
    {
        CreatorViewModel vm = CreateVm();
        var view = new CreatorView { DataContext = vm };
        (Window window, Grid root) = CompactViewRig.HostAt(view, ExpandedInner);
        try
        {
            DataGrid storedFilesGrid = window.GetVisualDescendants().OfType<DataGrid>().Single(g => g.Name == "StoredFilesGrid");
            Assert.Equal("Stored Files", ControlAutomationPeer.CreatePeerForElement(storedFilesGrid).GetName());

            ListBox logList = window.GetVisualDescendants().OfType<ListBox>().Single(l => l.Classes.Contains("logList"));
            Assert.Equal("Log", ControlAutomationPeer.CreatePeerForElement(logList).GetName());
        }
        finally { window.Close(); }
    }

    // ── 8. Frame-rig parity (normal-mode pixels unchanged) + splitter (tab-reachable, resizable, focus-visible) ──

    /// <summary>
    /// Same technique as every other converted view's own hardened version (RenderTargetBitmap +
    /// CopyPixels, exact integer pixel size gate BEFORE any byte read, full-buffer compare — no
    /// mask/crop/intersection). LOCAL copy of <c>AssertFullRasterPixelIdentity</c> /
    /// <c>RenderToPixelBuffer</c> (not promoted into the shared rig — promotion is an open
    /// controller decision, per every other converted view's own identical note).
    /// </summary>
    [AvaloniaFact]
    public void FrameRig_NormalMode_HeaderRegionMatchesPreDisclosureShape()
    {
        CreatorViewModel vm = CreateVm();
        var view = new CreatorView { DataContext = vm };
        (Window newWindow, Grid newRoot) = CompactViewRig.HostAt(view, ExpandedInner);
        try
        {
            Assert.DoesNotContain("compactHeight", newRoot.Classes);
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();

            Control newRow0 = newRoot.Children.OfType<Control>().Single(c => Grid.GetRow(c) == 0);
            Size newRowSize = newRow0.Bounds.Size;

            TextBlock newCaption = newRow0.GetVisualDescendants().OfType<TextBlock>().Single();
            Size newCaptionSize = newCaption.Bounds.Size;

            Window oldWindow = BuildPreDisclosureRow0Window();
            try
            {
                oldWindow.Show();
                AvaloniaHeadlessPlatform.ForceRenderTimerTick();
                Dispatcher.UIThread.RunJobs();
                var oldRow0 = (Control)oldWindow.Content!;
                Size oldSize = oldRow0.Bounds.Size;

                Assert.Equal(oldSize.Height, newRowSize.Height, precision: 0);

                // The intro TextBlock's own documented, intentional inset (Margin="0,0,4,0", "per
                // house rule" — matches SRSCreator/Reconstructor's own identical value; this view's
                // intro sentence, like SRSCreator's own, does not push any word across a line-break
                // boundary at the narrower measure, confirmed by the byte-for-byte raster check
                // below).
                double widthNarrowing = oldSize.Width - newCaptionSize.Width;
                Assert.Equal(4.0, widthNarrowing, precision: 0);

                Assert.Equal(oldSize.Width, newRowSize.Width, precision: 0);

                AssertFullRasterPixelIdentity(oldRow0, oldSize, newRow0, newRowSize);
            }
            finally { oldWindow.Close(); }
        }
        finally { newWindow.Close(); }
    }

    /// <summary>
    /// Extends the raster comparison beyond row 0 (every other converted view's own established
    /// practice once a view's config band content is genuinely re-hosted): the Input section's own
    /// caption is band 1's FIRST rendered content, now nested THREE levels deeper than before
    /// (ScrollViewer &gt; ConfigGrid &gt; StackPanel, vs. directly under the old flat Grid) — the
    /// most direct, cheapest proof that none of those new pass-through containers silently narrows
    /// or insets it (no compactScrollInset-style class was added here — deliberately: unlike
    /// SampleRestorer's own StackPanel, which never existed before this kind of task touched it,
    /// this Input StackPanel is genuinely pre-existing markup with a pre-existing zero margin, and
    /// nothing about this task changes that; this test is what proves it, rather than assuming it).
    /// </summary>
    [AvaloniaFact]
    public void FrameRig_NormalMode_InputCaptionMatchesPreChangeShape()
    {
        CreatorViewModel vm = CreateVm();
        var view = new CreatorView { DataContext = vm };
        (Window newWindow, Grid newRoot) = CompactViewRig.HostAt(view, ExpandedInner);
        try
        {
            Assert.DoesNotContain("compactHeight", newRoot.Classes);
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();

            ScrollViewer configScroller = newRoot.Children.OfType<ScrollViewer>().Single(sv => Grid.GetRow(sv) == 1);
            TextBlock newCaption = configScroller.GetVisualDescendants().OfType<TextBlock>()
                .Single(tb => tb.Inlines is [Run { Text: "Input " }, ..]);
            Size newCaptionSize = newCaption.Bounds.Size;

            Window oldWindow = BuildPreConversionInputCaptionWindow();
            try
            {
                oldWindow.Show();
                AvaloniaHeadlessPlatform.ForceRenderTimerTick();
                Dispatcher.UIThread.RunJobs();
                var oldCaption = (Control)oldWindow.Content!;
                Size oldSize = oldCaption.Bounds.Size;

                Assert.Equal(oldSize.Height, newCaptionSize.Height, precision: 0);

                // Zero narrowing at normal size: no inset class was applied to the Input section's
                // own StackPanel, and the wrapping ScrollViewer (VerticalScrollBarVisibility="Auto")
                // reserves no track while nothing is actually scrolling.
                Assert.Equal(oldSize.Width, newCaptionSize.Width, precision: 0);

                AssertFullRasterPixelIdentity(oldCaption, oldSize, newCaption, newCaptionSize);
            }
            finally { oldWindow.Close(); }
        }
        finally { newWindow.Close(); }
    }

    /// <summary>
    /// The two band-scoped tests above prove SPECIFIC
    /// regions; this proves EVERY conversion-touched band at once, at REST, by comparing the
    /// ENTIRE root — the strongest, most direct answer to "every conversion-touched band must be
    /// inside compared pixels." <see cref="OldFullMarkup"/> is the pre-task
    /// <c>CreatorView.axaml</c> VERBATIM (git blob <c>67aa5e8:ReScene.Manager/Views/CreatorView.axaml</c>,
    /// <c>x:Class</c> stripped so <see cref="AvaloniaRuntimeXamlLoader"/> can parse it as a plain
    /// <see cref="UserControl"/>), loaded through the REAL XAML pipeline — deliberately NOT a
    /// hand-built C# object graph like <see cref="BuildPreDisclosureRow0Window"/> /
    /// <see cref="BuildPreConversionInputCaptionWindow"/> above, which each needed a follow-up fix
    /// for missed implicit whitespace <c>Run</c>s (see their own remarks): parsing the frozen,
    /// verbatim XAML STRING through the actual compiler eliminates that entire class of
    /// reconstruction bug structurally, for the whole page at once, rather than one hand-copied
    /// band at a time. <c>typeof(CreatorView).Assembly</c> (not <c>typeof(UserControl).Assembly</c>,
    /// which was tried first and failed to resolve <c>clr-namespace:ReScene.Manager.Controls</c> /
    /// <c>.Behaviors</c>) is what lets the parser resolve <c>controls:FieldStatusLine</c> and
    /// <c>behaviors:TextBoxDropBehavior</c>.
    /// <para>
    /// MEASURED (a throwaway diagnostic, same technique as this test): both roots render at the
    /// EXACT SAME 676x721 integer pixel size, and the ENTIRE 1,949,584-byte buffer (676x721x4) is
    /// byte-for-byte identical — zero differing bytes, not merely zero found through spot-checks.
    /// This is the comprehensive, whole-page confirmation the two narrower band tests above could
    /// only ever sample.
    /// </para>
    /// </summary>
    [AvaloniaFact]
    public void FrameRig_NormalMode_FullRootMatchesPreChangeMarkup()
    {
        var oldView = (UserControl)AvaloniaRuntimeXamlLoader.Parse(OldFullMarkup, typeof(CreatorView).Assembly);
        oldView.DataContext = CreateVm();
        (Window oldWindow, Grid oldRoot) = CompactViewRig.HostAt(oldView, ExpandedInner);
        try
        {
            var newView = new CreatorView { DataContext = CreateVm() };
            (Window newWindow, Grid newRoot) = CompactViewRig.HostAt(newView, ExpandedInner);
            try
            {
                Assert.DoesNotContain("compactHeight", newRoot.Classes);
                AvaloniaHeadlessPlatform.ForceRenderTimerTick();
                Dispatcher.UIThread.RunJobs();

                AssertFullRasterPixelIdentity(oldRoot, oldRoot.Bounds.Size, newRoot, newRoot.Bounds.Size);
            }
            finally { newWindow.Close(); }
        }
        finally { oldWindow.Close(); }
    }

    /// <summary>
    /// A genuine cross-version dragged-state parity DOES
    /// exist, narrower than a full-root comparison but real. The region "everything from the top
    /// of the page down through and including the splitter itself" has an identity that did NOT
    /// change between OLD and NEW: same content, same StoredFilesGrid row, same Pixel-height
    /// resize mechanism for the splitter's "Previous" pane (only the "Next" pane's identity
    /// changed — see the drag-then-release test's own doc below for why THAT makes a full-root
    /// dragged comparison impossible). MEASURED (a throwaway diagnostic): after driving the SAME
    /// real ArrowDown key input on both splitters, the two structures' StoredFilesGrid rows report
    /// the EXACT SAME dragged height (the "delta tracks identically" invariant, asserted directly
    /// below) — and, with the config band's own scrollbar NOT yet engaged (see this test's own
    /// precondition), the CROPPED region above and including the splitter compares byte-for-byte
    /// identical between OLD and NEW.
    /// <para>
    /// The comparable window is genuinely NARROW — MEASURED directly: even a MODEST two-press
    /// (20-DIP) drag on this plain, unpopulated VM already pushes ConfigGrid's own total natural
    /// height just past the config ScrollViewer's dynamic MaxHeight cap (the same mechanism
    /// documented in the view's own ctor remarks, sized for the 883-DIP worst case, which
    /// leaves this plain-VM REST state only ~10-15 DIPs of headroom before it engages) — at that
    /// point NEW shows a real, load-bearing vertical scrollbar with no OLD equivalent (OLD has no
    /// scrolling architecture at all), narrowing NEW's own content by the scrollbar's track width
    /// (MEASURED: 676 DIPs wide down to 660). This is not a defect to route around — it is the
    /// scrolling fallback correctly engaging exactly as designed, on a genuinely NEW-only element.
    /// So this test uses a single ArrowDown press (one real, minimal, still-genuine drag) and
    /// ASSERTS the no-scrollbar precondition explicitly, so a future change to that tight margin
    /// fails this test LOUDLY (naming the violated precondition) instead of silently comparing the
    /// wrong thing. <see cref="FrameRig_NormalMode_SplitterDragThenRelease_FullRootRasterReturnsToRestState"/>
    /// below is the complementary, NEW-only evidence for the LARGER-drag/scrolling regime this
    /// test's own comparable window cannot reach.
    /// </para>
    /// </summary>
    [AvaloniaFact]
    public void FrameRig_NormalMode_SplitterDrag_AboveSplitterRegionMatchesPreChangeMarkup()
    {
        var oldView = (UserControl)AvaloniaRuntimeXamlLoader.Parse(OldFullMarkup, typeof(CreatorView).Assembly);
        oldView.DataContext = CreateVm();
        (Window oldWindow, Grid oldRoot) = CompactViewRig.HostAt(oldView, ExpandedInner);
        try
        {
            var newView = new CreatorView { DataContext = CreateVm() };
            (Window newWindow, Grid newRoot) = CompactViewRig.HostAt(newView, ExpandedInner);
            try
            {
                Assert.DoesNotContain("compactHeight", newRoot.Classes);

                GridSplitter oldSplitter = oldWindow.GetVisualDescendants().OfType<GridSplitter>().Single();
                GridSplitter newSplitter = newWindow.GetVisualDescendants().OfType<GridSplitter>().Single();
                DataGrid oldGrid = oldWindow.GetVisualDescendants().OfType<DataGrid>().Single(g => g.Name == "StoredFilesGrid");
                DataGrid newGrid = newWindow.GetVisualDescendants().OfType<DataGrid>().Single(g => g.Name == "StoredFilesGrid");
                var newConfigGrid = newWindow.GetVisualDescendants().OfType<Grid>().Single(g => g.Name == "ConfigGrid");
                var newConfigScroller = newWindow.GetVisualDescendants().OfType<ScrollViewer>().Single(sv => Grid.GetRow(sv) == 1);

                double oldHeightBefore = oldGrid.Bounds.Height;
                double newHeightBefore = newConfigGrid.RowDefinitions[3].Height.Value;
                Assert.Equal(150, oldHeightBefore);
                Assert.Equal(150, newHeightBefore);

                oldSplitter.Focus();
                Dispatcher.UIThread.RunJobs();
                PressManyTimes(oldWindow, PhysicalKey.ArrowDown, 1);

                newSplitter.Focus();
                Dispatcher.UIThread.RunJobs();
                PressManyTimes(newWindow, PhysicalKey.ArrowDown, 1);

                // The actual "delta tracks identically" invariant: the SAME real key input produces
                // the SAME numeric height change in both structures' shared, unchanged "Previous" pane.
                double oldHeightAfter = oldGrid.Bounds.Height;
                double newHeightAfter = newConfigGrid.RowDefinitions[3].Height.Value;
                Assert.True(oldHeightAfter > oldHeightBefore, "test precondition: the drag must genuinely resize OLD's StoredFilesGrid row");
                Assert.Equal(oldHeightAfter, newHeightAfter);

                // Precondition: the config band's own scrollbar must NOT have engaged — this is the
                // boundary of the comparable window (see this test's own doc).
                Assert.True(newConfigScroller.Extent.Height <= newConfigScroller.Viewport.Height + 0.5,
                    $"test precondition violated: the config band's own scrollbar engaged (Extent {newConfigScroller.Extent.Height:F1} > Viewport {newConfigScroller.Viewport.Height:F1}) — " +
                    "the comparable-region window this test relies on no longer holds at this drag magnitude.");

                // Defocus BOTH splitters onto a neutral control before capturing (a real
                // methodology bug this test's own first draft hit): two separate Windows in the
                // SAME headless Application share only ONE "active" concept, so focusing NEW's
                // splitter silently defocused OLD's — comparing a still-focused (accent-colored)
                // splitter against a freshly-defocused one misreports that expected, unrelated
                // difference as a layout defect. Both must render their shared REST (Transparent)
                // focus-visual state for this comparison to isolate pure layout.
                oldGrid.Focus();
                Dispatcher.UIThread.RunJobs();
                newGrid.Focus();
                Dispatcher.UIThread.RunJobs();

                AvaloniaHeadlessPlatform.ForceRenderTimerTick();
                Dispatcher.UIThread.RunJobs();

                Point? oldBottom = oldSplitter.TranslatePoint(new Point(0, oldSplitter.Bounds.Height), oldRoot);
                Point? newBottom = newSplitter.TranslatePoint(new Point(0, newSplitter.Bounds.Height), newRoot);
                Assert.True(oldBottom is not null && newBottom is not null);

                int oldCropHeight = (int)Math.Ceiling(oldBottom.Value.Y);
                int newCropHeight = (int)Math.Ceiling(newBottom.Value.Y);
                Assert.Equal(oldCropHeight, newCropHeight);

                int width = (int)Math.Ceiling(oldRoot.Bounds.Width);
                Assert.Equal(width, (int)Math.Ceiling(newRoot.Bounds.Width));

                var oldFullSize = new PixelSize(width, (int)Math.Ceiling(oldRoot.Bounds.Height));
                var newFullSize = new PixelSize(width, (int)Math.Ceiling(newRoot.Bounds.Height));
                byte[] oldFull = RenderToPixelBuffer(oldRoot, oldFullSize);
                byte[] newFull = RenderToPixelBuffer(newRoot, newFullSize);

                int stride = width * 4;
                int bytesToCompare = oldCropHeight * stride;
                Assert.True(bytesToCompare > 0 && bytesToCompare <= oldFull.Length && bytesToCompare <= newFull.Length);

                for (int i = 0; i < bytesToCompare; i++)
                {
                    if (oldFull[i] != newFull[i])
                    {
                        Assert.Fail(
                            $"above-splitter region pixel mismatch at ({i % stride / 4}, {i / stride}) — old byte " +
                            $"0x{oldFull[i]:X2} vs new byte 0x{newFull[i]:X2}. Compared {bytesToCompare} bytes " +
                            $"(y < {oldCropHeight}, the splitter's own bottom edge).");
                    }
                }
            }
            finally { newWindow.Close(); }
        }
        finally { oldWindow.Close(); }
    }

    /// <summary>
    /// A genuine, narrow cross-version comparable region DOES exist for a small drag (the test
    /// above) — but it structurally cannot extend to a LARGER drag, because once the config band's
    /// own scrollbar engages (see the test above's own MEASURED boundary), a real, load-bearing
    /// NEW-only element (the scrollbar track/thumb) appears with no OLD equivalent (OLD has no
    /// scrolling architecture at all — it is a flat, unwrapped Grid). Dragging the OLD splitter's
    /// "Next" pane (outer row 6, a Star-sized row containing the ENTIRE Output+Options+Action+Log
    /// composite) is ALSO not the same operation as dragging the NEW splitter's "Next" pane
    /// (ConfigGrid row 5, the Output section alone, Auto-sized) — this restructuring
    /// genuinely changes what the splitter's "Next" pane IS, compounding why no LARGER-drag
    /// cross-version comparison is meaningful.
    /// <para>
    /// The meaningful, ACHIEVABLE equivalent for this regime instead: capture the NEW view's own
    /// full-root raster at REST, engage a real, input-driven LARGER drag (ArrowDown — genuine
    /// keyboard input), capture again (basic sanity: the Stored Files row grew, nothing
    /// negative/degenerate), release the drag back to EXACTLY the original 150 (ArrowUp the same
    /// count — already independently proven exact by
    /// <see cref="StoredFilesRow_SplitterDragAtNormalSize_ResizesRow_AndDragSurvivesCompactRoundTrip"/>),
    /// and assert the FULL-ROOT RASTER after release matches the ORIGINAL rest capture BYTE FOR
    /// BYTE — proving a drag-then-undo cycle leaves no residual visual/layout drift anywhere on
    /// the page (including the scrollbar's own appear/disappear cycle), not just that the one
    /// row's numeric Height value round-trips.
    /// </para>
    /// </summary>
    [AvaloniaFact]
    public void FrameRig_NormalMode_SplitterDragThenRelease_FullRootRasterReturnsToRestState()
    {
        CreatorViewModel vm = CreateVm();
        var view = new CreatorView { DataContext = vm };
        (Window window, Grid root) = CompactViewRig.HostAt(view, ExpandedInner);
        try
        {
            Grid configGrid = window.GetVisualDescendants().OfType<Grid>().Single(g => g.Name == "ConfigGrid");
            GridSplitter splitter = window.GetVisualDescendants().OfType<GridSplitter>().Single();
            Assert.Equal(150, configGrid.RowDefinitions[3].Height.Value);

            // Focus BEFORE capturing the rest baseline (a real bug this test's own first draft
            // hit): the splitter must stay focused throughout the whole drag/release cycle so its
            // OWN :focus-visual color (a real, EXPECTED, and separately-tested difference — see
            // Splitter_FocusVisual_MeetsContrastAgainstBothPanes) is held CONSTANT across both
            // captures. Capturing "rest" before focusing would compare an unfocused splitter
            // against a still-focused one after release and misreport that expected, unrelated
            // color change as a residual layout defect.
            splitter.Focus();
            Dispatcher.UIThread.RunJobs();
            Assert.True(splitter.IsFocused);

            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();
            PixelSize restSize = new((int)Math.Ceiling(root.Bounds.Width), (int)Math.Ceiling(root.Bounds.Height));
            byte[] restPixels = RenderToPixelBuffer(root, restSize);

            PressManyTimes(window, PhysicalKey.ArrowDown, 10);
            double draggedHeight = configGrid.RowDefinitions[3].Height.Value;
            Assert.True(draggedHeight > 150, $"drag must genuinely resize row 3, was {draggedHeight:F1}");
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();

            // Basic sanity on the DRAGGED capture: the page must still render at a sane, positive,
            // unchanged OUTER size (only row 3's internal allocation changed) — not a claim of
            // parity against anything, since (per this test's own doc) no valid OLD-dragged
            // comparison exists.
            PixelSize draggedSize = new((int)Math.Ceiling(root.Bounds.Width), (int)Math.Ceiling(root.Bounds.Height));
            Assert.Equal(restSize, draggedSize);

            PressManyTimes(window, PhysicalKey.ArrowUp, 10);
            Assert.Equal(150, configGrid.RowDefinitions[3].Height.Value);
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();

            PixelSize releasedSize = new((int)Math.Ceiling(root.Bounds.Width), (int)Math.Ceiling(root.Bounds.Height));
            Assert.Equal(restSize, releasedSize);

            byte[] releasedPixels = RenderToPixelBuffer(root, releasedSize);
            Assert.Equal(restPixels.Length, releasedPixels.Length);
            for (int i = 0; i < restPixels.Length; i++)
            {
                if (restPixels[i] != releasedPixels[i])
                {
                    int stride = restSize.Width * 4;
                    Assert.Fail(
                        $"drag-then-release left a residual pixel difference at ({i % stride / 4}, {i / stride}) — " +
                        $"rest byte 0x{restPixels[i]:X2} vs post-release byte 0x{releasedPixels[i]:X2}.");
                }
            }
        }
        finally { window.Close(); }
    }

    /// <summary>
    /// Verbatim reconstruction of the pre-task <c>CreatorView.axaml</c> (git blob
    /// <c>67aa5e8:ReScene.Manager/Views/CreatorView.axaml</c>) as a raw XAML string, <c>x:Class</c>
    /// stripped so it can be loaded via <see cref="AvaloniaRuntimeXamlLoader"/> as a plain
    /// <see cref="UserControl"/> — see <see cref="FrameRig_NormalMode_FullRootMatchesPreChangeMarkup"/>'s
    /// own doc for why parsing the frozen, verbatim markup through the real XAML pipeline (rather
    /// than a hand-built C# object graph) is used for the full-page comparison specifically.
    /// </summary>
    private const string OldFullMarkup = """
        <UserControl xmlns="https://github.com/avaloniaui"
                     xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                     xmlns:controls="clr-namespace:ReScene.Manager.Controls"
                     xmlns:behaviors="clr-namespace:ReScene.Manager.Behaviors"
                     x:CompileBindings="False">

          <Grid Margin="{DynamicResource PageMargin}">
            <Grid.RowDefinitions>
              <RowDefinition Height="Auto" />
              <RowDefinition Height="Auto" />
              <RowDefinition Height="Auto" />
              <RowDefinition Height="Auto" />
              <RowDefinition Height="150" MinHeight="150" />
              <RowDefinition Height="Auto" />
              <RowDefinition Height="*" MinHeight="100" />
            </Grid.RowDefinitions>

            <TextBlock Grid.Row="0"
                       Text="Create an SRR (Scene Release Rescue) file from a RAR archive set. The SRR captures RAR headers and metadata needed to reconstruct the original archives later."
                       Foreground="{DynamicResource ForegroundSecondary}" FontSize="{DynamicResource FontSizeCaption}"
                       TextWrapping="Wrap" Margin="0,0,0,6" />

            <StackPanel Grid.Row="1">
              <TextBlock TextWrapping="Wrap" Margin="0,0,0,2">
                <Run Text="Input " FontWeight="SemiBold" />
                <Run Text="— use " Foreground="{DynamicResource ForegroundSecondary}" FontSize="{DynamicResource FontSizeCaption}" />
                <Run Text="Browse" FontWeight="SemiBold" Foreground="{DynamicResource ForegroundSecondary}" FontSize="{DynamicResource FontSizeCaption}" />
                <Run Text=" for a single set's .sfv or first .rar, or " Foreground="{DynamicResource ForegroundSecondary}" FontSize="{DynamicResource FontSizeCaption}" />
                <Run Text="Browse folder…" FontWeight="SemiBold" Foreground="{DynamicResource ForegroundSecondary}" FontSize="{DynamicResource FontSizeCaption}" />
                <Run Text=" to search a release folder and its subfolders for RAR sets (e.g. multi-disc releases)." Foreground="{DynamicResource ForegroundSecondary}" FontSize="{DynamicResource FontSizeCaption}" />
              </TextBlock>
              <DockPanel Margin="0,0,0,2">
                <Button DockPanel.Dock="Right" Content="Browse folder…"
                        Command="{Binding BrowseInputFolderCommand}"
                        Classes="ghost"
                        TabIndex="2"
                        AutomationProperties.Name="Browse folder for release input"
                        AutomationProperties.HelpText="Pick a release folder to search it and its subfolders for RAR sets."
                        Margin="4,0,0,0" MinWidth="75" />
                <Button DockPanel.Dock="Right" Content="Browse"
                        Command="{Binding BrowseInputCommand}"
                        Classes="ghost"
                        TabIndex="1"
                        AutomationProperties.Name="Browse input file"
                        AutomationProperties.HelpText="Pick a single set's .sfv or first .rar file."
                        Margin="4,0,0,0" MinWidth="75" />
                <TextBox x:Name="InputTextBox" Text="{Binding InputPath}"
                         TabIndex="0"
                         AutomationProperties.Name="Input path"
                         AutomationProperties.HelpText="Accepts a release .sfv/.rar file path or a release folder path"
                         behaviors:TextBoxDropBehavior.DropMode="File" />
              </DockPanel>
              <ProgressBar IsIndeterminate="True" IsVisible="{Binding IsScanning}" Height="4"
                           Margin="0,0,0,2"
                           AutomationProperties.Name="Scanning release folder" />
              <ScrollViewer VerticalScrollBarVisibility="Auto" ScrollViewer.AllowAutoHide="False" MaxHeight="96" IsVisible="{Binding HasDetectedSets}">
                <ItemsControl ItemsSource="{Binding DetectedSets}"
                              AutomationProperties.Name="{Binding DetectedSetsSummary}">
                  <ItemsControl.ItemTemplate>
                    <DataTemplate>
                      <TextBlock Text="{Binding RelativeName}" />
                    </DataTemplate>
                  </ItemsControl.ItemTemplate>
                </ItemsControl>
              </ScrollViewer>
              <controls:FieldStatusLine Status="{Binding InputStatus}" />
            </StackPanel>

            <Border Grid.Row="2" Height="1" Background="{DynamicResource BorderSeparator}" Margin="0,4" />

            <StackPanel Grid.Row="3">
              <TextBlock Text="Stored Files" FontWeight="SemiBold" Margin="0,0,0,2" />
              <DockPanel Margin="0,0,0,2">
                <StackPanel Orientation="Horizontal">
                  <Button Content="Add..." Command="{Binding AddStoredFileCommand}" Classes="ghost" Margin="0,0,4,0" />
                  <Button Content="Remove" Command="{Binding RemoveStoredFileCommand}" Classes="ghost" Margin="0,0,4,0" />
                  <Button Content="Remove All" Command="{Binding RemoveAllStoredFilesCommand}" Classes="ghost" Margin="0,0,4,0" />
                  <Button Content="Move Up" Command="{Binding MoveStoredFileUpCommand}" Classes="ghost" Margin="0,0,4,0" />
                  <Button Content="Move Down" Command="{Binding MoveStoredFileDownCommand}" Classes="ghost" />
                </StackPanel>
                <TextBlock Text="Double-click the Stored As column to edit the name used in the SRR."
                           VerticalAlignment="Center" Margin="8,0,0,0"
                           Foreground="{DynamicResource ForegroundSecondary}" FontSize="{DynamicResource FontSizeCaption}" />
              </DockPanel>
            </StackPanel>

            <DataGrid x:Name="StoredFilesGrid"
                      Grid.Row="4"
                      ItemsSource="{Binding StoredFiles}"
                      SelectedItem="{Binding SelectedStoredFile}"
                      AutoGenerateColumns="False"
                      CanUserReorderColumns="False"
                      CanUserSortColumns="False"
                      GridLinesVisibility="Horizontal"
                      HeadersVisibility="Column"
                      SelectionMode="Single"
                      BorderThickness="0">
              <DataGrid.Columns>
                <DataGridTextColumn Header="File Path" Binding="{Binding FullPath}" IsReadOnly="True" Width="*" />
                <DataGridTextColumn Header="Stored As" Binding="{Binding StoredName}" Width="450" />
              </DataGrid.Columns>
            </DataGrid>

            <GridSplitter Grid.Row="5" Height="5" HorizontalAlignment="Stretch"
                          VerticalAlignment="Center"
                          ResizeDirection="Rows"
                          ResizeBehavior="PreviousAndNext"
                          Background="Transparent" />

            <Grid Grid.Row="6">
              <Grid.RowDefinitions>
                <RowDefinition Height="Auto" />
                <RowDefinition Height="Auto" />
                <RowDefinition Height="Auto" />
                <RowDefinition Height="Auto" />
                <RowDefinition Height="Auto" />
                <RowDefinition Height="Auto" />
                <RowDefinition Height="Auto" />
                <RowDefinition Height="*" MinHeight="40" />
              </Grid.RowDefinitions>

              <StackPanel Grid.Row="0">
                <TextBlock TextWrapping="Wrap" Margin="0,0,0,2">
                  <Run Text="Output " FontWeight="SemiBold" />
                  <Run Text="— Where the .srr file will be written."
                       Foreground="{DynamicResource ForegroundSecondary}" FontSize="{DynamicResource FontSizeCaption}" />
                </TextBlock>
                <DockPanel>
                  <Button DockPanel.Dock="Right" Content="Browse"
                          Command="{Binding BrowseOutputCommand}"
                          Classes="ghost"
                          Margin="4,0,0,0" MinWidth="75" />
                  <TextBox x:Name="OutputTextBox" Text="{Binding OutputPath}"
                           behaviors:TextBoxDropBehavior.DropMode="File" />
                </DockPanel>
                <controls:FieldStatusLine Status="{Binding OutputStatus}" />
              </StackPanel>

              <Border Grid.Row="1" Height="1" Background="{DynamicResource BorderSeparator}" Margin="0,4" />

              <StackPanel Grid.Row="2">
                <TextBlock Text="Options" FontWeight="SemiBold" Margin="0,0,0,2" />
                <CheckBox Content="Auto-include files — Scan release directory for .nfo, .sfv, proof images, .m3u, .cue, .log files."
                          IsChecked="{Binding AutoIncludeFiles}" Margin="0,1" />
                <CheckBox Content="Auto-create SRS — Create .srs files for samples found in Sample/ subdirectory."
                          IsChecked="{Binding AutoCreateSRS}" Margin="0,1" />
                <CheckBox Content="Vobsub SRR — Create nested SRR files for subtitle archives found in Subs/ directories."
                          IsChecked="{Binding CreateVobsubSRR}" Margin="0,1" />
                <CheckBox Content="Store fix RAR — For fix/patch releases, store the main RAR file as proof."
                          IsChecked="{Binding StoreFixRAR}"
                          IsEnabled="{Binding IsFolderMode, Converter={StaticResource InverseBoolConverter}}"
                          AutomationProperties.HelpText="Automatic in folder mode — the release scan decides this"
                          Margin="0,1" />
                <CheckBox Content="Allow compressed — Accept RAR volumes that use compression (method != Store)."
                          IsChecked="{Binding AllowCompressed}" Margin="0,1" />
                <CheckBox Content="OSO hashes — Compute and store OpenSubtitles OSO hashes for archived files."
                          IsChecked="{Binding ComputeOSOHashes}" Margin="0,1" />
                <CheckBox Content="Languages.diz — Extract language metadata from VobSub .idx files and store in the SRR."
                          IsChecked="{Binding GenerateLanguagesDiz}" Margin="0,1,0,4" />
                <DockPanel Margin="0,0,0,2">
                  <TextBlock Text="App name:" VerticalAlignment="Center"
                             Margin="0,0,8,0" />
                  <TextBox Text="{Binding AppName}" Width="400"
                           HorizontalAlignment="Left" />
                  <TextBlock Text="Embedded in the SRR header to identify the creating application."
                             VerticalAlignment="Center" Margin="8,0,0,0"
                             Foreground="{DynamicResource ForegroundSecondary}" FontSize="{DynamicResource FontSizeCaption}" />
                </DockPanel>
              </StackPanel>

              <Border Grid.Row="3" Height="1" Background="{DynamicResource BorderSeparator}" Margin="0,4" />

              <StackPanel Grid.Row="4">
                <DockPanel Margin="0,0,0,2">
                  <StackPanel DockPanel.Dock="Right" Orientation="Horizontal">
                    <Button Content="Create SRR"
                            Command="{Binding CreateSRRCommand}"
                            Classes="primary"
                            Padding="16,4" Margin="0,0,4,0" />
                    <Button Content="Cancel"
                            Command="{Binding CancelCreationCommand}"
                            Classes="cancel"
                            IsVisible="{Binding IsCreating}"
                            Padding="16,4" />
                  </StackPanel>
                  <StackPanel Orientation="Horizontal" VerticalAlignment="Center">
                    <TextBlock Text="{Binding ActionHint}"
                               Foreground="{DynamicResource ForegroundSecondary}"
                               FontSize="{DynamicResource FontSizeCaption}"
                               VerticalAlignment="Center" />
                    <TextBlock Text="{Binding ProgressMessage}"
                               IsVisible="{Binding ShowProgress}"
                               VerticalAlignment="Center" />
                  </StackPanel>
                </DockPanel>
                <ProgressBar Value="{Binding ProgressPercent}"
                             Maximum="100" Height="18"
                             Margin="0,0,0,4"
                             IsVisible="{Binding ShowProgress}" />
              </StackPanel>

              <Border Grid.Row="5" Height="1" Background="{DynamicResource BorderSeparator}" Margin="0,4" />

              <DockPanel Grid.Row="6" Margin="0,0,0,2">
                <Button DockPanel.Dock="Right" Content="Save log..."
                        Command="{Binding SaveLogCommand}"
                        Classes="ghost"
                        Padding="8,2" />
                <TextBlock DockPanel.Dock="Left" Text="Log" FontWeight="SemiBold" VerticalAlignment="Center" />
                <TextBlock x:Name="SaveLogStatus" Text="{Binding SaveLogAnnouncement}"
                           AutomationProperties.LiveSetting="Polite"
                           Foreground="{DynamicResource ForegroundSecondary}" FontSize="{DynamicResource FontSizeCaption}"
                           TextTrimming="CharacterEllipsis" VerticalAlignment="Center" Margin="8,0" />
              </DockPanel>

              <ListBox Grid.Row="7"
                       ItemsSource="{Binding LogEntries}"
                       Classes="logList"
                       FontFamily="{DynamicResource MonoFontFamily}"
                       FontSize="{DynamicResource MonoFontSize}" />
            </Grid>

          </Grid>

        </UserControl>
        """;

    /// <summary>Verbatim reconstruction of CreatorView.axaml's row-0 TextBlock before this task (git history).</summary>
    private static Window BuildPreDisclosureRow0Window()
    {
        var textBlock = new TextBlock
        {
            Text = "Create an SRR (Scene Release Rescue) file from a RAR archive set. The SRR captures RAR headers and metadata needed to reconstruct the original archives later.",
            Foreground = (IBrush?)Application.Current!.FindResource("ForegroundSecondary"),
            FontSize = (double)Application.Current!.FindResource("FontSizeCaption")!,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 6),
        };

        return new Window { Width = CompactInvariantRig.InnerWidth, SizeToContent = SizeToContent.Height, Content = textBlock };
    }

    /// <summary>
    /// Verbatim reconstruction of CreatorView.axaml's Input caption TextBlock before this task
    /// (git history). DIAGNOSED (same finding as SampleRestorerCompactTests' own identical
    /// reconstruction bug): the six &lt;Run&gt; elements sit on separate source lines in the XAML,
    /// and Avalonia's XAML parser collapses each inter-tag newline + indentation into an implicit,
    /// PLAIN (default-styled — does not inherit either neighbor's FontWeight/Foreground/FontSize)
    /// whitespace-only <see cref="Run"/> — CONFIRMED directly (a throwaway diagnostic dump of the
    /// real, live-hosted TextBlock's own <c>Inlines</c> showed exactly 11 entries: the 6 authored
    /// Runs plus 5 implicit " " Runs, one between each adjacent pair). Omitting them would silently
    /// drop 5 spaces and shift every pixel from the second Run onward.
    /// </summary>
    private static Window BuildPreConversionInputCaptionWindow()
    {
        var textBlock = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 2) };
        var secondary = (IBrush?)Application.Current!.FindResource("ForegroundSecondary");
        double captionSize = (double)Application.Current!.FindResource("FontSizeCaption")!;
        textBlock.Inlines!.Add(new Run { Text = "Input ", FontWeight = FontWeight.SemiBold });
        textBlock.Inlines!.Add(new Run { Text = " " });
        textBlock.Inlines!.Add(new Run { Text = "— use ", Foreground = secondary, FontSize = captionSize });
        textBlock.Inlines!.Add(new Run { Text = " " });
        textBlock.Inlines!.Add(new Run { Text = "Browse", FontWeight = FontWeight.SemiBold, Foreground = secondary, FontSize = captionSize });
        textBlock.Inlines!.Add(new Run { Text = " " });
        textBlock.Inlines!.Add(new Run { Text = " for a single set's .sfv or first .rar, or ", Foreground = secondary, FontSize = captionSize });
        textBlock.Inlines!.Add(new Run { Text = " " });
        textBlock.Inlines!.Add(new Run { Text = "Browse folder…", FontWeight = FontWeight.SemiBold, Foreground = secondary, FontSize = captionSize });
        textBlock.Inlines!.Add(new Run { Text = " " });
        textBlock.Inlines!.Add(new Run { Text = " to search a release folder and its subfolders for RAR sets (e.g. multi-disc releases).", Foreground = secondary, FontSize = captionSize });

        return new Window { Width = CompactInvariantRig.InnerWidth, SizeToContent = SizeToContent.Height, Content = textBlock };
    }

    /// <summary>
    /// Proves <see cref="AssertFullRasterPixelIdentity"/>'s size gate genuinely DISCRIMINATES — a
    /// capture-size disagreement fails loudly instead of silently shrinking to the intersection.
    /// Mirrors every other converted view's own identical covering test.
    /// </summary>
    [AvaloniaFact]
    public void AssertFullRasterPixelIdentity_SubDipDriftAcrossARasterLine_FailsInsteadOfShrinkingToTheIntersection()
    {
        CreatorViewModel vm = CreateVm();
        var view = new CreatorView { DataContext = vm };
        (Window newWindow, Grid newRoot) = CompactViewRig.HostAt(view, ExpandedInner);
        try
        {
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();

            Control newRow0 = newRoot.Children.OfType<Control>().Single(c => Grid.GetRow(c) == 0);
            Size newRowSize = newRow0.Bounds.Size;

            Window oldWindow = BuildPreDisclosureRow0Window();
            try
            {
                oldWindow.Show();
                AvaloniaHeadlessPlatform.ForceRenderTimerTick();
                Dispatcher.UIThread.RunJobs();
                var oldRow0 = (Control)oldWindow.Content!;
                Size oldSize = oldRow0.Bounds.Size;

                AssertDriftedSizeFails(new Size(DriftAcrossOneRasterLine(newRowSize.Width), newRowSize.Height));
                AssertDriftedSizeFails(new Size(newRowSize.Width, DriftAcrossOneRasterLine(newRowSize.Height)));

                AssertFullRasterPixelIdentity(oldRow0, oldSize, newRow0, newRowSize);

                void AssertDriftedSizeFails(Size drifted)
                {
                    Assert.Equal(oldSize.Width, drifted.Width, precision: 0);
                    Assert.Equal(oldSize.Height, drifted.Height, precision: 0);

                    Assert.NotEqual(
                        new PixelSize((int)Math.Ceiling(oldSize.Width), (int)Math.Ceiling(oldSize.Height)),
                        new PixelSize((int)Math.Ceiling(drifted.Width), (int)Math.Ceiling(drifted.Height)));

                    Xunit.Sdk.FailException ex = Assert.Throws<Xunit.Sdk.FailException>(
                        () => AssertFullRasterPixelIdentity(oldRow0, oldSize, newRow0, drifted));
                    Assert.Contains("EXACTLY the same integer pixel size", ex.Message, StringComparison.Ordinal);
                    Assert.Contains($"{drifted.Width:F4}x{drifted.Height:F4}", ex.Message, StringComparison.Ordinal);
                }
            }
            finally { oldWindow.Close(); }
        }
        finally { newWindow.Close(); }
    }

    private static double DriftAcrossOneRasterLine(double value) =>
        Math.Ceiling(value) == Math.Round(value) ? Math.Ceiling(value) + 0.4 : Math.Round(value);

    /// <summary>
    /// Renders both controls to a <see cref="RenderTargetBitmap"/> at their OWN true geometry and
    /// requires true byte-for-byte identity of the ENTIRE buffer on BOTH sides — no mask, no crop,
    /// no intersection, no offset. Local copy of every other converted view's own hardened helper.
    /// </summary>
    private static void AssertFullRasterPixelIdentity(Control oldControl, Size oldSize, Control newControl, Size newSize)
    {
        const int BytesPerPixel = 4;

        var oldPixelSize = new PixelSize((int)Math.Ceiling(oldSize.Width), (int)Math.Ceiling(oldSize.Height));
        var newPixelSize = new PixelSize((int)Math.Ceiling(newSize.Width), (int)Math.Ceiling(newSize.Height));

        if (oldPixelSize != newPixelSize)
        {
            Assert.Fail(
                $"the two captures must rasterise to EXACTLY the same integer pixel size before any " +
                $"comparison is meaningful — old {oldPixelSize} (bounds {oldSize.Width:F4}x{oldSize.Height:F4}) " +
                $"vs new {newPixelSize} (bounds {newSize.Width:F4}x{newSize.Height:F4}). A disagreement means " +
                "one capture has a raster column or row with no counterpart in the other; comparing their " +
                "intersection instead would leave that line unproven while still reporting full parity.");
        }

        PixelSize rasterSize = oldPixelSize;
        Assert.True(rasterSize.Width > 0 && rasterSize.Height > 0,
            $"nothing to compare — both captures rasterise to {rasterSize}.");

        byte[] oldPixels = RenderToPixelBuffer(oldControl, rasterSize);
        byte[] newPixels = RenderToPixelBuffer(newControl, rasterSize);

        int stride = rasterSize.Width * BytesPerPixel;

        for (int i = 0; i < oldPixels.Length; i++)
        {
            if (oldPixels[i] == newPixels[i])
            {
                continue;
            }

            Assert.Fail(
                $"header pixel mismatch at ({i % stride / BytesPerPixel}, {i / stride}) — old byte " +
                $"0x{oldPixels[i]:X2} vs new byte 0x{newPixels[i]:X2}. Both captures are {rasterSize} " +
                $"({oldPixels.Length} bytes each) and every byte of both is compared: no mask, no crop, " +
                "no intersection.");
        }
    }

    private static byte[] RenderToPixelBuffer(Control control, PixelSize size)
    {
        using var bitmap = new RenderTargetBitmap(size, new Vector(96, 96));
        bitmap.Render(control);

        byte[] buffer = new byte[size.Width * size.Height * 4];
        var handle = GCHandle.Alloc(buffer, GCHandleType.Pinned);
        try
        {
            bitmap.CopyPixels(new PixelRect(0, 0, size.Width, size.Height), handle.AddrOfPinnedObject(), buffer.Length, size.Width * 4);
        }
        finally
        {
            handle.Free();
        }

        return buffer;
    }

    // ── Splitter (tab-reachable, Up/Down-resizable, bounded by pane minimums, ──
    // ── visible >=3:1 focus indication) ─────────────────────────────────────────────────────

    /// <summary>
    /// Scoped to NORMAL size for this IN-SCROLLER splitter: unlike
    /// Reconstructor's top-level splitter (bounded by two compact-shrinkable panes), this
    /// splitter's own "previous" pane (ConfigGrid row 3) has a HARD compact floor of 80 delivered
    /// by the descendant PixelRestore entry, not by dragging — the splitter's pane-minimum bound is
    /// therefore only a meaningful, exercisable claim at NORMAL size (compact's 80 is fixed
    /// regardless of any drag). It stays focusable/operable in BOTH modes regardless (no
    /// compact-only Focusable override exists anywhere on GridSplitter).
    /// </summary>
    [AvaloniaFact]
    public void Splitter_FocusableAndNamed_UpDownResizesAtNormalSize_ClampsAtMinimum()
    {
        CreatorViewModel vm = CreateVm();
        var view = new CreatorView { DataContext = vm };
        (Window window, Grid root) = CompactViewRig.HostAt(view, ExpandedInner);
        try
        {
            Assert.DoesNotContain("compactHeight", root.Classes);
            GridSplitter splitter = window.GetVisualDescendants().OfType<GridSplitter>().Single();
            Assert.Equal("Resize stored files and output", AutomationProperties.GetName(splitter));

            Grid configGrid = window.GetVisualDescendants().OfType<Grid>().Single(g => g.Name == "ConfigGrid");
            Assert.Equal(150, configGrid.RowDefinitions[3].MinHeight);

            splitter.Focus();
            Dispatcher.UIThread.RunJobs();
            Assert.True(splitter.IsFocused);

            // Up shrinks row 3 toward its own 150-DIP MinHeight (unchanged, normal-mode authored
            // value — the compact 80 floor never applies here).
            PressManyTimes(window, PhysicalKey.ArrowUp, 40);
            Assert.True(configGrid.RowDefinitions[3].Height.Value >= 150 - 0.5,
                $"row 3 clamped below its 150-DIP normal-mode minimum: {configGrid.RowDefinitions[3].Height.Value:F1}");

            // Down grows it back — genuinely resizable, not just clamped at one edge.
            PressManyTimes(window, PhysicalKey.ArrowDown, 20);
            Assert.True(configGrid.RowDefinitions[3].Height.Value > 150,
                $"row 3 should have grown past 150 after 20 ArrowDown presses, was {configGrid.RowDefinitions[3].Height.Value:F1}");
        }
        finally { window.Close(); }
    }

    private static void PressManyTimes(Window window, PhysicalKey key, int count)
    {
        for (int i = 0; i < count; i++)
        {
            window.KeyPressQwerty(key, RawInputModifiers.None);
            window.KeyReleaseQwerty(key, RawInputModifiers.None);
            Dispatcher.UIThread.RunJobs();
        }
    }

    /// <summary>
    /// Rendered <c>:focus</c> brush vs BOTH adjacent panes' own rendered pixel colors, sampled
    /// directly from a real render (never a guessed/hardcoded resource key — this splitter's two
    /// neighbors are a DataGrid, whose own Fluent-templated background is not necessarily the same
    /// named resource as a plain panel's, and a StackPanel with no explicit Background of its own).
    /// Mirrors <c>ReconstructorCompactTests.Splitter_FocusVisual_MeetsContrastAgainstBothPanes</c>'s
    /// own contrast MATH (WCAG relative luminance) exactly, sourced from ACTUAL rendered pixels
    /// instead of resource lookups.
    /// </summary>
    [AvaloniaFact]
    public void Splitter_FocusVisual_MeetsContrastAgainstBothPanes() =>
        AssertSplitterFocusContrastAgainstBothPanes(ExpandedInner);

    /// <summary>
    /// The default-theme contrast test above only ever
    /// exercised EXPANDED size — compact-mode focus visibility/contrast was untested despite that
    /// requirement being mode-independent, and the splitter's own reachability (proven by the
    /// tab-walk) says nothing about whether its focus indication is actually VISIBLE once reached
    /// at compact size. Same assertions, at <see cref="CompactInner"/>.
    /// </summary>
    [AvaloniaFact]
    public void Splitter_FocusVisual_MeetsContrastAgainstBothPanes_Compact() =>
        AssertSplitterFocusContrastAgainstBothPanes(CompactInner);

    private static void AssertSplitterFocusContrastAgainstBothPanes(double innerHeight)
    {
        CreatorViewModel vm = CreateVm();
        for (int i = 0; i < 3; i++)
        {
            vm.StoredFiles.Add(Item($@"C:\release\file{i:D2}.nfo", $"file{i:D2}.nfo"));
        }
        var view = new CreatorView { DataContext = vm };

        (Window window, _) = CompactViewRig.HostAt(view, innerHeight);
        try
        {
            GridSplitter splitter = window.GetVisualDescendants().OfType<GridSplitter>().Single();
            splitter.Focus();
            Dispatcher.UIThread.RunJobs();
            Assert.True(splitter.IsFocused, "test precondition: the splitter must genuinely take focus at this size");
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();

            (double contrastVsAbove, double contrastVsBelow) = MeasureSplitterFocusContrast(splitter, window);

            Assert.True(contrastVsAbove >= 3.0, $"focus brush vs the pane above (Stored Files grid): {contrastVsAbove:F2}:1 (need >= 3:1)");
            Assert.True(contrastVsBelow >= 3.0, $"focus brush vs the pane below (Output section): {contrastVsBelow:F2}:1 (need >= 3:1)");
        }
        finally { window.Close(); }
    }

    /// <summary>
    /// Extracted so both the default-theme and the complete-HC-fixture tests (expanded AND
    /// compact variants of each) share the exact same sampling/math, never duplicated by hand.
    /// </summary>
    /// <summary>
    /// Previously read <c>splitter.Background</c>'s own LOGICAL
    /// brush color directly — the exact same defect class as the "Transparent.Color is
    /// meaningless" bug documented below, just reachable a different way: MEASURED (a throwaway diagnostic) that
    /// setting <c>splitter.Opacity = 0</c> leaves BOTH <c>IsEffectivelyVisible</c> AND
    /// <c>splitter.Background</c>'s own logical color COMPLETELY UNCHANGED (still reporting the
    /// accent color), while the ACTUAL RENDERED PIXEL at that location silently reverts to
    /// whatever is behind it. A property-read (or a mere IsEffectivelyVisible check) would have
    /// let a visually-suppressed, unpainted, or covered focus indicator pass this contrast check
    /// undetected. Fixed to (a) an IN-BOUNDS check (<see cref="AssertFullyWithinWindow"/>, this
    /// file's own established clip-aware visibility helper — catches scrolled-away/clipped cases
    /// the opacity case does NOT, so both checks are needed, not either alone) and (b) sampling the
    /// REAL RENDERED PIXEL at the splitter's own center (the same technique already used for both
    /// neighboring panes) instead of trusting the logical property. Proven to genuinely
    /// discriminate by <see cref="Splitter_FocusVisual_ContrastMeasurement_UnpaintedSplitter_FailsTheCheck"/>.
    /// </summary>
    private static (double ContrastVsAbove, double ContrastVsBelow) MeasureSplitterFocusContrast(GridSplitter splitter, Window window)
    {
        AssertFullyWithinWindow(splitter, window);

        Point center = new(splitter.Bounds.Width / 2, splitter.Bounds.Height / 2);
        Point? centerInWindow = splitter.TranslatePoint(center, window);
        Assert.True(centerInWindow is not null, "test precondition: the splitter's own center must translate into window coordinates");
        Color focusColor = SamplePixelColor(window, centerInWindow.Value);

        Point? aboveInWindow = splitter.TranslatePoint(new Point(splitter.Bounds.Width / 2, -3), window);
        Point? belowInWindow = splitter.TranslatePoint(new Point(splitter.Bounds.Width / 2, splitter.Bounds.Height + 3), window);
        Assert.True(aboveInWindow is not null && belowInWindow is not null, "test precondition: both neighboring points must translate into window coordinates");

        Color abovePane = SamplePixelColor(window, aboveInWindow.Value);
        Color belowPane = SamplePixelColor(window, belowInWindow.Value);

        return (ContrastRatio(focusColor, abovePane), ContrastRatio(focusColor, belowPane));
    }

    /// <summary>
    /// The high-contrast smoke the default-theme
    /// contrast test above does not cover. This app has no actual shipped high-contrast SKIN —
    /// grepped the whole Resources tree and found no "HighContrast" resource dictionary, no
    /// <c>ThemeVariant</c> switching anywhere beyond the single hardcoded
    /// <c>RequestedThemeVariant="Dark"</c> in App.axaml, and no prior converted view's own test
    /// file has ever exercised one either — there is nothing resembling a real Windows
    /// high-contrast integration to toggle programmatically in a headless test, and this
    /// environment must not flip the HOST MACHINE's own real OS-level accessibility settings just
    /// to synthesize one (a disruptive, system-wide, outward-facing action far outside a unit
    /// test's blast radius). The achievable, honest equivalent: prove the splitter's focus
    /// indicator is genuinely LIVE-RESOURCE-DRIVEN (a <c>DynamicResource</c> binding to
    /// <c>AccentPrimary</c>, not a frozen/cached brush) by swapping the resource to an extreme,
    /// maximally-distinct color — the same technique a real high-contrast theme dictionary would
    /// use — and confirming the RENDERED pixel actually follows it, with contrast re-verified
    /// against both panes under the new color, then restoring the original value. This is the
    /// architectural property that makes a genuine high-contrast override safe if one is ever
    /// shipped: a hardcoded/cached brush would silently fail this exact check.
    /// <para>
    /// The override is scoped to THIS test only and restored in a <c>finally</c> block — MEASURED
    /// (a throwaway diagnostic) that <c>Application.Current.Resources["AccentPrimary"]</c> reads
    /// back <c>null</c> before any override (the real value lives in a MERGED dictionary,
    /// Resources/Tokens.axaml, not the top-level one), so restoration removes the directly-set key
    /// rather than reassigning a captured "original" value — confirmed to correctly fall back to
    /// the merged dictionary's own value afterward, not leave the key missing or wrong.
    /// </para>
    /// </summary>
    [AvaloniaFact]
    public void Splitter_FocusVisual_HighContrastSmoke_FollowsLiveResourceOverride()
    {
        CreatorViewModel vm = CreateVm();
        for (int i = 0; i < 3; i++)
        {
            vm.StoredFiles.Add(Item($@"C:\release\file{i:D2}.nfo", $"file{i:D2}.nfo"));
        }
        var view = new CreatorView { DataContext = vm };

        (Window window, _) = CompactViewRig.HostAt(view, ExpandedInner);
        try
        {
            GridSplitter splitter = window.GetVisualDescendants().OfType<GridSplitter>().Single();
            splitter.Focus();
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();

            var beforeBrush = Assert.IsAssignableFrom<ISolidColorBrush>(splitter.Background);
            Color beforeColor = beforeBrush.Color;

            // Only the TOP-LEVEL dictionary's own key matters here (not the whole merged-dictionary
            // resolution chain TryGetResource would search) — MEASURED (a throwaway diagnostic):
            // this key is null/absent at the top level before any override (the real value lives
            // in the MERGED Resources/Tokens.axaml), so restoration must REMOVE the key rather than
            // reassign a captured value, or the override would leak past this test.
            bool hadDirectOverride = Application.Current!.Resources.ContainsKey("AccentPrimary");
            object? capturedOriginal = hadDirectOverride ? Application.Current!.Resources["AccentPrimary"] : null;

            // Maximally distinct from AccentPrimary's own default (#FF0078D4, a blue) and from
            // both neighboring panes — mirrors the kind of extreme, saturated color a real
            // Windows high-contrast theme substitutes for focus/accent brushes.
            var highContrastColor = Color.FromRgb(0xFF, 0xFF, 0x00);
            try
            {
                Application.Current!.Resources["AccentPrimary"] = new SolidColorBrush(highContrastColor);
                Dispatcher.UIThread.RunJobs();
                AvaloniaHeadlessPlatform.ForceRenderTimerTick();
                Dispatcher.UIThread.RunJobs();

                var overriddenBrush = Assert.IsAssignableFrom<ISolidColorBrush>(splitter.Background);
                Assert.Equal(highContrastColor, overriddenBrush.Color); // WIRING claim: the property resolved correctly
                Assert.NotEqual(beforeColor, overriddenBrush.Color);

                // The CONTRAST claim must come from the ACTUAL
                // RENDERED PIXEL (mirrors MeasureSplitterFocusContrast's own identical fix), not
                // from the known override VALUE — a logically-correct-but-unpainted/clipped focus
                // indicator would otherwise pass this exact assertion undetected.
                (double contrastVsAbove, double contrastVsBelow) = MeasureSplitterFocusContrast(splitter, window);

                Assert.True(contrastVsAbove >= 3.0,
                    "the high-contrast override color must ALSO clear the 3:1 bar — a real high-contrast theme's own color choice would, and this proves the mechanism doesn't accidentally defeat itself");
                Assert.True(contrastVsBelow >= 3.0);
            }
            finally
            {
                if (hadDirectOverride)
                {
                    Application.Current!.Resources["AccentPrimary"] = capturedOriginal;
                }
                else
                {
                    Application.Current!.Resources.Remove("AccentPrimary");
                }
                Dispatcher.UIThread.RunJobs();
            }

            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();
            var restoredBrush = Assert.IsAssignableFrom<ISolidColorBrush>(splitter.Background);
            Assert.Equal(beforeColor, restoredBrush.Color);
        }
        finally { window.Close(); }
    }

    // ── A COMPLETE, scoped high-contrast theme fixture ──
    //
    // The single-key AccentPrimary override above proves resource LIVENESS only — it
    // never touches the rest of the app's own palette, so it cannot prove the splitter's focus
    // indication survives a genuine WHOLE-THEME swap the way a real Windows high-contrast
    // activation would produce (the two neighboring panes' own colors, whatever resources feed
    // them, would ALSO change under a real HC theme). This section builds the complete equivalent:
    // 46 token keys are overridden at once, modeled on an actual Windows "High Contrast Black" theme
    // (near-uniform black surfaces, white text/borders, a saturated yellow accent — Windows HC
    // themes still differentiate error/warning/success semantically by hue, not just lightness).
    //
    // DELIBERATELY FROZEN, and no longer a census. This list was written when the app had NO
    // high-contrast skin, and its 46 keys were then every brush Tokens.axaml defined. That is no
    // longer true: the app now owns 64 brushes across two merged dictionaries, and ships a real
    // HighContrast.axaml. The number here is pinned at 46 rather than chased, because this fixture's
    // job was always to prove the splitter's focus indication survives a WHOLE-THEME swap, and 46
    // simultaneous overrides prove that just as well as 64 would.
    //
    // What it must NOT be mistaken for is a check on the shipped palette — its colours are this
    // fixture's own, not HighContrast.axaml's. That question belongs to
    // HighContrastShippedDictionaryTests, which drives the real dictionary through the real
    // HighContrastThemeService; and the token inventory is guarded by HighContrastTokenTests. The
    // alternative was to re-point this at the real dictionary, which would have made it a duplicate
    // of the former with a worse mechanism, so it stays as the historical instrument it is.
    // Applied/restored via the SAME proven mechanism as the single-key test above (direct
    // top-level Resources[key] set/remove — MEASURED there that these keys are absent at the top
    // level, so restoration removes rather than reassigns), wrapped in an IDisposable so three
    // call sites (expanded, compact, and the discrimination covering test) share one exception-safe
    // apply/restore path instead of three hand-duplicated try/finally blocks.

    private static readonly IReadOnlyDictionary<string, Color> CompleteHighContrastFixtureColors = new Dictionary<string, Color>
    {
        // Surfaces -> black
        ["WindowBackground"] = Colors.Black,
        ["PanelBackground"] = Colors.Black,
        ["SurfaceBackground"] = Colors.Black,
        ["InputBackground"] = Colors.Black,
        ["HoverBackground"] = Colors.Black,
        ["ActiveBackground"] = Colors.Black,
        ["SelectedItemBackground"] = Colors.Black,
        ["HexHeaderBrush"] = Colors.Black,
        ["SystemControlBackgroundListLowBrush"] = Colors.Black,
        ["SystemControlBackgroundAltHighBrush"] = Colors.Black,
        ["SystemControlHighlightListLowBrush"] = Colors.Black,
        // Text -> white
        ["ForegroundPrimary"] = Colors.White,
        ["ForegroundSecondary"] = Colors.White,
        ["ForegroundDisabled"] = Colors.White,
        ["HeaderForeground"] = Colors.White,
        ["LogTerminalForeground"] = Colors.White,
        ["SystemControlForegroundBaseMediumHighBrush"] = Colors.White,
        ["SystemControlForegroundBaseMediumBrush"] = Colors.White,
        ["SystemControlForegroundBaseMediumLowBrush"] = Colors.White,
        ["SystemControlForegroundBaseLowBrush"] = Colors.White,
        ["SystemControlForegroundBaseHighBrush"] = Colors.White,
        // Borders -> white
        ["BorderSubtle"] = Colors.White,
        ["BorderMedium"] = Colors.White,
        ["BorderSeparator"] = Colors.White,
        ["PanelBorderBrush"] = Colors.White,
        ["StatusSeparatorBrush"] = Colors.White,
        ["PanelHeaderSeparatorBrush"] = Colors.White,
        // Accent -> saturated yellow (this is the key the splitter's own :focus style reads)
        ["AccentPrimary"] = Color.FromRgb(0xFF, 0xFF, 0x00),
        ["AccentHover"] = Color.FromRgb(0xFF, 0xFF, 0x00),
        ["AccentPressed"] = Color.FromRgb(0xFF, 0xFF, 0x00),
        ["BorderFocused"] = Color.FromRgb(0xFF, 0xFF, 0x00),
        ["SystemAccentBrush"] = Color.FromRgb(0xFF, 0xFF, 0x00),
        ["PropertyHighlightBrush"] = Color.FromRgb(0xFF, 0xFF, 0x00),
        // Semantic hues, kept distinguishable (real HC themes still differ error/warning/success by hue)
        ["AccentSuccess"] = Color.FromRgb(0x00, 0xFF, 0x00),
        ["AccentWarning"] = Color.FromRgb(0xFF, 0x80, 0x00),
        ["AccentError"] = Color.FromRgb(0xFF, 0x00, 0x00),
        ["WarningForeground"] = Color.FromRgb(0xFF, 0x80, 0x00),
        ["HexDiffHighlightBrush"] = Color.FromRgb(0xFF, 0x00, 0x00),
        ["DiffRowBackground"] = Color.FromArgb(0x33, 0xFF, 0x00, 0x00),
        // Hyperlinks -> cyan
        ["HyperlinkForeground"] = Color.FromRgb(0x00, 0xFF, 0xFF),
        ["HyperlinkHoverForeground"] = Color.FromRgb(0x00, 0xFF, 0xFF),
        // Hex-view retro colors -> white/yellow (not visually relevant to this view, included for completeness)
        ["HexOffsetForeground"] = Colors.White,
        ["HexBytesForeground"] = Colors.White,
        ["HexAsciiForeground"] = Colors.White,
        ["HexSelectionBrush"] = Color.FromArgb(0x44, 0xFF, 0xFF, 0x00),
        ["HexMatchHighlightBrush"] = Color.FromArgb(0x33, 0xFF, 0x80, 0x00),
    };

    /// <summary>
    /// Applies <see cref="CompleteHighContrastFixtureColors"/> as direct top-level overrides on
    /// <see cref="Application.Resources"/> (the same mechanism <see cref="Splitter_FocusVisual_HighContrastSmoke_FollowsLiveResourceOverride"/>
    /// already proved works and restores cleanly) and restores every one of them on
    /// <see cref="Dispose"/> — never leaks the fixture into any other test.
    /// </summary>
    private sealed class HighContrastFixtureScope : IDisposable
    {
        private readonly Dictionary<string, (bool HadDirectOverride, object? Original)> _captured = [];

        public HighContrastFixtureScope()
        {
            foreach ((string key, Color color) in CompleteHighContrastFixtureColors)
            {
                bool hadDirectOverride = Application.Current!.Resources.ContainsKey(key);
                _captured[key] = (hadDirectOverride, hadDirectOverride ? Application.Current!.Resources[key] : null);
                Application.Current!.Resources[key] = new SolidColorBrush(color);
            }
            Dispatcher.UIThread.RunJobs();
        }

        public void Dispose()
        {
            foreach ((string key, (bool hadDirectOverride, object? original)) in _captured)
            {
                if (hadDirectOverride)
                {
                    Application.Current!.Resources[key] = original;
                }
                else
                {
                    Application.Current!.Resources.Remove(key);
                }
            }
            Dispatcher.UIThread.RunJobs();
        }
    }

    /// <summary>
    /// The complete-theme equivalent of
    /// <see cref="Splitter_FocusVisual_HighContrastSmoke_FollowsLiveResourceOverride"/> — every
    /// resource the splitter's own template AND its two neighboring panes could plausibly consume
    /// is overridden at once (see <see cref="CompleteHighContrastFixtureColors"/>'s own remarks),
    /// so this exercises the REAL lookup chain rather than one already-known key. Asserts contrast
    /// against BOTH panes (now genuinely black under the fixture) AND against the splitter's OWN
    /// unfocused state (now also black at rest, per the GridSplitter base style's literal
    /// Transparent-over-black background) — i.e., the focus indication must be visually DISTINCT
    /// from what this same control looks like when nothing is focused, not merely "distinct from
    /// its neighbors" in the abstract.
    /// </summary>
    [AvaloniaFact]
    public void Splitter_FocusVisual_CompleteHighContrastFixture_RemainsDistinctFromPanesAndUnfocusedState() =>
        AssertSplitterFocusContrastUnderCompleteHighContrastFixture(ExpandedInner);

    /// <summary>The compact-size variant of the complete-fixture test above.</summary>
    [AvaloniaFact]
    public void Splitter_FocusVisual_CompleteHighContrastFixture_RemainsDistinctFromPanesAndUnfocusedState_Compact() =>
        AssertSplitterFocusContrastUnderCompleteHighContrastFixture(CompactInner);

    private static void AssertSplitterFocusContrastUnderCompleteHighContrastFixture(double innerHeight)
    {
        CreatorViewModel vm = CreateVm();
        for (int i = 0; i < 3; i++)
        {
            vm.StoredFiles.Add(Item($@"C:\release\file{i:D2}.nfo", $"file{i:D2}.nfo"));
        }
        var view = new CreatorView { DataContext = vm };

        (Window window, _) = CompactViewRig.HostAt(view, innerHeight);
        try
        {
            GridSplitter splitter = window.GetVisualDescendants().OfType<GridSplitter>().Single();
            Point splitterCenter = new(splitter.Bounds.Width / 2, splitter.Bounds.Height / 2);

            using (new HighContrastFixtureScope())
            {
                // Unfocused baseline, SAMPLED FROM THE REAL RENDER (not read off the Background
                // property directly) — a real bug this test's own first draft hit: the REST style
                // sets a literal "Transparent" Background, and reading a transparent SolidColorBrush's
                // own .Color property returns whatever RGB channels it happens to carry underneath
                // its zero alpha (MEASURED: Avalonia's Transparent carries WHITE channels at alpha
                // 0), which is not what a user actually SEES — nothing ever renders that value,
                // since alpha-zero paints nothing at all and the pane behind shows through instead.
                // Sampling the real rendered pixel (the same technique already used for the
                // neighboring panes) captures what is ACTUALLY visible when unfocused, under the
                // SAME HC theme, for a genuine apples-to-apples comparison.
                Point? centerInWindow = splitter.TranslatePoint(splitterCenter, window);
                Assert.True(centerInWindow is not null);
                AvaloniaHeadlessPlatform.ForceRenderTimerTick();
                Dispatcher.UIThread.RunJobs();
                Color unfocusedColor = SamplePixelColor(window, centerInWindow.Value);

                splitter.Focus();
                Dispatcher.UIThread.RunJobs();
                Assert.True(splitter.IsFocused, "test precondition: the splitter must genuinely take focus at this size");
                AvaloniaHeadlessPlatform.ForceRenderTimerTick();
                Dispatcher.UIThread.RunJobs();

                var focusBrush = Assert.IsAssignableFrom<ISolidColorBrush>(splitter.Background);
                Assert.Equal(Color.FromRgb(0xFF, 0xFF, 0x00), focusBrush.Color); // AccentPrimary under the fixture

                (double contrastVsAbove, double contrastVsBelow) = MeasureSplitterFocusContrast(splitter, window);
                Assert.True(contrastVsAbove >= 3.0, $"under the complete HC fixture, focus brush vs the pane above: {contrastVsAbove:F2}:1 (need >= 3:1)");
                Assert.True(contrastVsBelow >= 3.0, $"under the complete HC fixture, focus brush vs the pane below: {contrastVsBelow:F2}:1 (need >= 3:1)");

                double contrastVsOwnUnfocusedState = ContrastRatio(focusBrush.Color, unfocusedColor);
                Assert.True(contrastVsOwnUnfocusedState >= 3.0,
                    $"under the complete HC fixture, the focused splitter must remain visually distinct from its OWN unfocused rest state ({unfocusedColor}): {contrastVsOwnUnfocusedState:F2}:1 (need >= 3:1)");
            }

            // The fixture's own restoration is exercised too: after Dispose, the splitter (still
            // logically focused) must revert to the DEFAULT theme's own accent color.
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();
            var restoredBrush = Assert.IsAssignableFrom<ISolidColorBrush>(splitter.Background);
            Assert.Equal(Color.FromRgb(0x00, 0x78, 0xD4), restoredBrush.Color); // AccentPrimary's real default
        }
        finally { window.Close(); }
    }

    /// <summary>
    /// The discriminating-evidence case: "show it fails if the focus
    /// indication is removed/hardcoded: temporarily break, observe, revert." Under the complete HC
    /// fixture (both panes now black), a LOCAL (non-<c>DynamicResource</c>-following) Background
    /// value that happens to equal the fixture's own black — simulating exactly the real-world
    /// defect this whole mechanism exists to catch: a hardcoded focus color that a real Windows
    /// high-contrast activation would leave behind, unremapped, blending into the new background —
    /// must fail the SAME contrast check the passing tests above rely on. Reverted (the local value
    /// cleared) before the fixture itself is disposed, proving the untampered mechanism resumes.
    /// </summary>
    [AvaloniaFact]
    public void Splitter_FocusVisual_CompleteHighContrastFixture_HardcodedFocusColor_FailsContrastCheck()
    {
        CreatorViewModel vm = CreateVm();
        for (int i = 0; i < 3; i++)
        {
            vm.StoredFiles.Add(Item($@"C:\release\file{i:D2}.nfo", $"file{i:D2}.nfo"));
        }
        var view = new CreatorView { DataContext = vm };

        (Window window, _) = CompactViewRig.HostAt(view, ExpandedInner);
        try
        {
            GridSplitter splitter = window.GetVisualDescendants().OfType<GridSplitter>().Single();

            using (new HighContrastFixtureScope())
            {
                splitter.Focus();
                Dispatcher.UIThread.RunJobs();
                AvaloniaHeadlessPlatform.ForceRenderTimerTick();
                Dispatcher.UIThread.RunJobs();

                // Sanity: the REAL, untampered mechanism passes first (same claim as the test above).
                (double realAbove, double realBelow) = MeasureSplitterFocusContrast(splitter, window);
                Assert.True(realAbove >= 3.0 && realBelow >= 3.0, "test precondition: the untampered fixture must pass before it is deliberately broken");

                // BREAK: a LOCAL value shadows the :focus style's DynamicResource entirely —
                // exactly what a hardcoded Background="..." in the XAML would produce, frozen at
                // whatever color it was authored with regardless of any later theme swap. Chosen to
                // match the fixture's own black background exactly (near-zero contrast).
                splitter.Background = new SolidColorBrush(Colors.Black);
                Dispatcher.UIThread.RunJobs();
                AvaloniaHeadlessPlatform.ForceRenderTimerTick();
                Dispatcher.UIThread.RunJobs();

                (double brokenAbove, double brokenBelow) = MeasureSplitterFocusContrast(splitter, window);
                Assert.True(brokenAbove < 3.0 && brokenBelow < 3.0,
                    $"the hardcoded-black splitter should have FAILED the 3:1 bar against the (also black) HC fixture panes, but measured {brokenAbove:F2}:1 / {brokenBelow:F2}:1 — this covering test no longer discriminates.");

                // REVERT: clear the local value so the :focus style's DynamicResource binding
                // resumes, and confirm the untampered mechanism passes again.
                splitter.ClearValue(TemplatedControl.BackgroundProperty);
                Dispatcher.UIThread.RunJobs();
                AvaloniaHeadlessPlatform.ForceRenderTimerTick();
                Dispatcher.UIThread.RunJobs();

                (double revertedAbove, double revertedBelow) = MeasureSplitterFocusContrast(splitter, window);
                Assert.True(revertedAbove >= 3.0 && revertedBelow >= 3.0,
                    "reverting the local override should restore the passing, untampered mechanism");
            }
        }
        finally { window.Close(); }
    }

    /// <summary>
    /// The required discriminating-evidence case: proves
    /// <see cref="MeasureSplitterFocusContrast"/>'s rendered-pixel fix genuinely catches an
    /// "unpainted/clipped" focus indicator that BOTH a naive property-read AND a plain
    /// <c>IsEffectivelyVisible</c> check would miss. <c>Opacity = 0</c> is the sharpest available
    /// case, MEASURED directly (a throwaway diagnostic): it leaves <c>IsEffectivelyVisible</c>,
    /// <c>IsVisible</c>, AND the splitter's own logical <c>Background</c> color COMPLETELY
    /// UNCHANGED (still reporting the focused accent color) — only the ACTUAL RENDERED PIXEL
    /// silently reverts to whatever is behind it. This is precisely the class of real-world defect
    /// the fix exists to catch (a focus indicator that is logically "there" and "visible" by every
    /// property-based signal, yet invisible to an actual user).
    /// </summary>
    [AvaloniaFact]
    public void Splitter_FocusVisual_ContrastMeasurement_UnpaintedSplitter_FailsTheCheck()
    {
        CreatorViewModel vm = CreateVm();
        for (int i = 0; i < 3; i++)
        {
            vm.StoredFiles.Add(Item($@"C:\release\file{i:D2}.nfo", $"file{i:D2}.nfo"));
        }
        var view = new CreatorView { DataContext = vm };

        (Window window, _) = CompactViewRig.HostAt(view, ExpandedInner);
        try
        {
            GridSplitter splitter = window.GetVisualDescendants().OfType<GridSplitter>().Single();
            splitter.Focus();
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();

            // Sanity: the REAL, untampered mechanism passes first.
            (double realAbove, double realBelow) = MeasureSplitterFocusContrast(splitter, window);
            Assert.True(realAbove >= 3.0 && realBelow >= 3.0, "test precondition: the untampered splitter must pass before it is deliberately suppressed");

            Color loggedBackgroundBefore = Assert.IsAssignableFrom<ISolidColorBrush>(splitter.Background).Color;

            // BREAK: suppress rendering WITHOUT touching any property a naive check would read.
            splitter.Opacity = 0;
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();

            // The exact reason a property-read (the original, pre-fix shape of this check) or a plain
            // visibility flag would have MISSED this: none of them changed. This is WHY the
            // in-bounds check (AssertFullyWithinWindow, itself unable to see this case) is not
            // sufficient alone — MeasureSplitterFocusContrast passes straight through it and only
            // the rendered-pixel sample downstream actually reflects the suppression.
            Assert.True(splitter.IsEffectivelyVisible, "test precondition: Opacity=0 must NOT flip IsEffectivelyVisible — that is exactly what makes this case dangerous");
            Assert.True(splitter.IsVisible);
            Assert.True(splitter.Bounds.Width > 0 && splitter.Bounds.Height > 0, "test precondition: Opacity=0 must NOT collapse layout bounds either");
            Assert.Equal(loggedBackgroundBefore, Assert.IsAssignableFrom<ISolidColorBrush>(splitter.Background).Color);

            (double brokenAbove, double brokenBelow) = MeasureSplitterFocusContrast(splitter, window);
            Assert.True(brokenAbove < 3.0 && brokenBelow < 3.0,
                $"the unpainted (Opacity=0) splitter should have FAILED the 3:1 bar — its rendered pixel no longer shows the focus color at all — but measured {brokenAbove:F2}:1 / {brokenBelow:F2}:1: this covering test no longer discriminates.");

            // REVERT and confirm the untampered mechanism passes again.
            splitter.Opacity = 1;
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();

            (double revertedAbove, double revertedBelow) = MeasureSplitterFocusContrast(splitter, window);
            Assert.True(revertedAbove >= 3.0 && revertedBelow >= 3.0, "reverting Opacity should restore the passing, untampered mechanism");
        }
        finally { window.Close(); }
    }

    /// <summary>
    /// "Focused-splitter visibility after continued resizing" needed its own coverage — the staged
    /// focus-recovery contract (<c>CompactHeightBehavior</c>'s own
    /// capture/relocate machinery) is proven for OBSCURED-CAPTURED elements elsewhere in this
    /// suite, but never specifically for the splitter AS the focus-holder across a genuinely
    /// continuous shrink, including crossing the compact threshold and continuing to shrink
    /// AFTER that (a within-mode resize, which <c>CompactHeightBehavior.Evaluate</c>'s own
    /// early-return means does NOT re-run the staged capture/recovery sequence at all — so if
    /// anything were going to strand the splitter, it would be here).
    /// <para>
    /// This test found a REAL, REPRODUCIBLE PRODUCTION GAP in the
    /// SHARED <c>CompactHeightBehavior</c> (used by all five converted views), not in this view's
    /// own wiring, and was originally kept SKIPPED — with this comment as the evidence trail —
    /// until the shared fix landed (<c>CompactHeightBehavior</c> now re-checks a still-focused,
    /// in-scope element's clip-aware visibility on ANY bounds change, not only at transitions). The
    /// test is UN-SKIPPED here and HARDENED: the original
    /// form permitted focus theft — it only measured contrast <c>if</c> focus happened to still be
    /// on the splitter, so a run that MOVED focus away scored as a pass. Every assertion below is
    /// now UNCONDITIONAL: at each step the splitter must STILL be the focus-holder (focus moving is
    /// a theft failure, reported as such), must be clip-aware fully visible, and its focus
    /// indication must clear 3:1 against the RENDERED PIXELS the helper actually samples — its own
    /// centre versus the points 3 DIPs above and below it, which is a claim about the surfaces
    /// immediately adjacent along that centre line, not a survey of either pane as a whole.
    /// </para>
    /// <para>
    /// MEASURED (reproduced three times, isolating the exact trigger): the worst case
    /// (<see cref="ForceWorstCase"/> — 12 detected sets, both statuses, scanning, 8 stored files,
    /// creating+progress) is what exposes it; two narrower diagnostics (worst-case content minus
    /// the 8 stored files; then a direct 900→319 jump instead of a gradual sequence) each found NO
    /// gap, which is why the earliest drafts of this investigation concluded there wasn't one — the
    /// gradual, full-worst-case combination is what surfaces it. Sequence observed (at the heights
    /// this view switched at when it was measured — 720→719): crossing the
    /// threshold IS a genuine transition, so <c>CompactHeightBehavior</c>'s own staged
    /// recovery correctly fires ONCE — the config <c>ScrollViewer</c>'s Offset moves from
    /// <c>(0,0)</c> to <c>(0,22)</c>, just enough to bring the (by-then-obscured) splitter back
    /// into its OWN, then-321-DIP-tall viewport. But every SUBSEQUENT step (719→600→500→400→319,
    /// none of which are mode transitions) shrinks that SAME viewport further — 321→261→211→161→121
    /// DIPs — while the Offset stays FROZEN at 22 (nothing re-evaluates it, since
    /// <c>Evaluate()</c>'s own <c>if (!isTransition &amp;&amp; state.Established) return;</c> skips
    /// the entire staged sequence for a same-mode resize). The splitter's own position within the
    /// scrollable content never moves, so a viewport that keeps shrinking around a frozen offset
    /// eventually clips it again: at the floor (319), clip-aware visible region reported
    /// <c>(12, 114)..(672, 375)</c> against the splitter's own <c>(12, 429)..(672, 435)</c> — fully
    /// below it, obscured, while STILL logically focused. Root cause is <c>CompactHeightBehavior</c>
    /// treating "obscurement recheck" as transition-triggered only; a general fix would need it to
    /// also recheck the currently-focused element's visibility on ANY bounds change once compact
    /// (not just on entry), which touches the shared mechanism all six views depend on — outside
    /// this task's own scope to change unilaterally. The shared behavior was updated accordingly,
    /// with its own contract tests
    /// (<c>CompactHeightBehaviorTests.ContinuedShrinkPastTransition_*</c> /
    /// <c>ContinuedShrink_PartialClipOnly_*</c>).
    /// </para>
    /// </summary>
    [AvaloniaFact]
    public void Splitter_StaysFocusedAndVisibleWithRenderedIndication_AcrossContinuousShrinkPastThreshold()
    {
        CreatorViewModel vm = CreateVm();
        ForceWorstCase(vm);
        var view = new CreatorView { DataContext = vm };
        (Window window, Grid root) = CompactViewRig.HostAt(view, Threshold + 185);
        try
        {
            GridSplitter splitter = window.GetVisualDescendants().OfType<GridSplitter>().Single();
            splitter.Focus();
            Dispatcher.UIThread.RunJobs();
            Assert.True(splitter.IsFocused, "test precondition: the splitter must genuinely take focus before the shrink sequence begins");

            // Comfortably expanded, down through Threshold+1/Threshold/Threshold-1 (the transition
            // itself), then CONTINUING to shrink well past it — the within-compact regime
            // CompactHeightBehavior's own Evaluate() does NOT re-run staged recovery for.
            //
            // Every step is expressed relative to this view's own switch point and the compact
            // floor, so the ladder stays STRICTLY DECREASING wherever the derivation puts that
            // switch — which a mix of absolute heights and derived ones would not (the absolute
            // ones would sooner or later sort themselves into the middle of the derived ones and
            // turn a "continuous shrink" into a shrink with a growth step in it). The three
            // sub-threshold steps divide the room between the transition and the compact floor
            // into quarters, reproducing the original ladder's shape without naming its heights.
            double belowGap = (Threshold - 1) - CompactInner;
            double[] shrinkSteps =
            [
                Threshold + 135, Threshold + 85, Threshold + 35, Threshold + 1, Threshold, Threshold - 1,
                CompactInner + (belowGap * 0.75), CompactInner + (belowGap * 0.50), CompactInner + (belowGap * 0.25),
                CompactInner,
            ];

            Assert.True(shrinkSteps.Zip(shrinkSteps.Skip(1)).All(pair => pair.First > pair.Second),
                $"the ladder must shrink at every step: [{string.Join(", ", shrinkSteps.Select(h => h.ToString("F0")))}]");
            foreach (double targetInner in shrinkSteps)
            {
                double overhead = window.Height - root.Bounds.Height;
                window.Height = targetInner + overhead;
                Dispatcher.UIThread.RunJobs();
                // Drain the Loaded-priority staged-recovery post (CompactHeightBehavior defers its
                // own obscurement recheck one dispatcher-priority level below layout).
                for (int i = 0; i < 5; i++)
                {
                    Dispatcher.UIThread.RunJobs();
                }
                AvaloniaHeadlessPlatform.ForceRenderTimerTick();
                Dispatcher.UIThread.RunJobs();

                // UNCONDITIONAL: the earlier form guarded
                // the contrast measurement behind "if focus is still on the splitter", which let a
                // run that MOVED focus away score as a pass — permitting exactly the focus theft
                // the shared behavior is required never to commit. Every state in this sequence is
                // recoverable by scrolling the config band, so focus moving is a FAILURE.
                var focused = window.FocusManager?.GetFocusedElement() as Control;
                Assert.True(ReferenceEquals(focused, splitter),
                    $"at inner height {targetInner}, the splitter must STILL hold focus — this shrink is " +
                    "recoverable by scrolling, so moving focus away is theft, not a recovery (focus is now " +
                    $"{(focused is null ? "NOTHING" : focused.GetType().Name)})");
                AssertFullyWithinWindow(splitter, window);

                // Scope of this claim, stated exactly: the helper samples THREE
                // rendered pixels — the splitter's own centre, and the points 3 DIPs directly above
                // and below it. It therefore proves the focus indication is distinguishable from the
                // surfaces immediately adjacent to it along that centre line; it does not survey
                // either neighbouring pane as a whole.
                (double contrastVsAbove, double contrastVsBelow) = MeasureSplitterFocusContrast(splitter, window);
                Assert.True(contrastVsAbove >= 3.0 && contrastVsBelow >= 3.0,
                    $"at inner height {targetInner}, the splitter still holds focus but its rendered focus indication " +
                    $"no longer clears 3:1 against the pixels sampled 3 DIPs above and below its own centre " +
                    $"({contrastVsAbove:F2}:1 above / {contrastVsBelow:F2}:1 below)");
            }
        }
        finally { window.Close(); }
    }

    /// <summary>Renders the whole window and reads back one pixel's RGBA — used to sample a
    /// neighboring pane's TRUE rendered color rather than guessing which named resource applies.</summary>
    private static Color SamplePixelColor(Window window, Point pointInWindow)
    {
        var size = new PixelSize((int)Math.Ceiling(window.Bounds.Width), (int)Math.Ceiling(window.Bounds.Height));
        using var bitmap = new RenderTargetBitmap(size, new Vector(96, 96));
        bitmap.Render(window);

        byte[] buffer = new byte[size.Width * size.Height * 4];
        var handle = GCHandle.Alloc(buffer, GCHandleType.Pinned);
        try
        {
            bitmap.CopyPixels(new PixelRect(0, 0, size.Width, size.Height), handle.AddrOfPinnedObject(), buffer.Length, size.Width * 4);
        }
        finally
        {
            handle.Free();
        }

        int x = Math.Clamp((int)pointInWindow.X, 0, size.Width - 1);
        int y = Math.Clamp((int)pointInWindow.Y, 0, size.Height - 1);
        int offset = (y * size.Width * 4) + (x * 4);
        // Avalonia's RenderTargetBitmap default pixel format is BGRA8888.
        byte b = buffer[offset];
        byte g = buffer[offset + 1];
        byte r = buffer[offset + 2];
        byte a = buffer[offset + 3];
        return Color.FromArgb(a, r, g, b);
    }

    /// <summary>WCAG 2.x relative luminance + contrast ratio, computed from rendered brush colors — never a hardcoded number. Mirrors ReconstructorCompactTests' own identical helper.</summary>
    private static double ContrastRatio(Color a, Color b)
    {
        double la = RelativeLuminance(a);
        double lb = RelativeLuminance(b);
        double lighter = Math.Max(la, lb);
        double darker = Math.Min(la, lb);
        return (lighter + 0.05) / (darker + 0.05);
    }

    private static double RelativeLuminance(Color c)
    {
        double r = LinearizeChannel(c.R / 255.0);
        double g = LinearizeChannel(c.G / 255.0);
        double b = LinearizeChannel(c.B / 255.0);
        return 0.2126 * r + 0.7152 * g + 0.0722 * b;
    }

    private static double LinearizeChannel(double c) =>
        c <= 0.03928 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);

    /// <summary>
    /// CLIP-AWARE (mirrors every other converted view's own identical helper): a naive
    /// "translated point within the window's own outer rectangle" check can false-PASS a control
    /// genuinely obscured by an intermediate <c>ClipToBounds</c> ancestor. A degenerate
    /// (zero-width/zero-height) control translates to a single point, which trivially satisfies
    /// any containment check — effective visibility and a positive size are asserted FIRST.
    /// </summary>
    private static void AssertFullyWithinWindow(Control control, Window window)
    {
        Assert.True(control.IsEffectivelyVisible, $"{control.GetType().Name} is not effectively visible.");
        Assert.True(control.Bounds.Width > 0 && control.Bounds.Height > 0,
            $"{control.GetType().Name} has a non-positive size ({control.Bounds.Width:F1}x{control.Bounds.Height:F1}) — collapsed, not merely positioned badly.");

        if (TransformRect(control, new Rect(control.Bounds.Size), window) is not { } controlInWindow)
        {
            Assert.Fail($"{control.GetType().Name} could not be translated into window coordinates.");
            return;
        }

        Rect visible = new(window.Bounds.Size);
        foreach (Visual ancestor in control.GetVisualAncestors())
        {
            if (ancestor is not Control clipper || !clipper.ClipToBounds)
            {
                continue;
            }

            if (TransformRect(clipper, new Rect(clipper.Bounds.Size), window) is not { } clipperInWindow)
            {
                Assert.Fail($"{clipper.GetType().Name} (a clipping ancestor of {control.GetType().Name}) could not be translated into window coordinates.");
                return;
            }

            visible = visible.Intersect(clipperInWindow);
        }

        const double Slack = 0.5;
        Assert.True(
            controlInWindow.X >= visible.X - Slack && controlInWindow.Y >= visible.Y - Slack &&
            controlInWindow.Right <= visible.Right + Slack && controlInWindow.Bottom <= visible.Bottom + Slack,
            $"{control.GetType().Name} bounds ({controlInWindow}) exceed the visible (clip-aware) region {visible} — obscured by " +
            "an intermediate ClipToBounds ancestor (e.g. a ScrollViewer's own clipped viewport), not just positioned outside the window.");
    }

    private static Rect? TransformRect(Visual from, Rect localRect, Visual to)
    {
        Point? topLeft = from.TranslatePoint(new Point(localRect.X, localRect.Y), to);
        Point? bottomRight = from.TranslatePoint(new Point(localRect.Right, localRect.Bottom), to);
        return topLeft is { } tl && bottomRight is { } br ? new Rect(tl, br) : null;
    }

    // ── Fixtures (captured from real, green CompactViewRig.CaptureTabOrderControls runs against
    // this task's finished implementation). Each entry is
    // CompactViewRig.Describe's own format (real automation peer name plus x:Name, reported
    // separately), a human-readable regression net, NOT the discriminating check itself (that is
    // AssertTabWalk's own reference-based ResolveIndependentExpectedOrder + AssertSameControlSequence,
    // proven to genuinely discriminate by AssertSameControlSequence_SwappedPositions_FailsNamingTheMismatch).
    // The Input row leads, matching its visual and tree position — see
    // ResolveIndependentExpectedOrder's own note on why it used to trail the log instead. ──

    private static readonly IReadOnlyList<string> NormalModeTabOrderFixture =
    [
        "TextBox name=\"Input path\" id=\"InputTextBox\"",
        "Button name=\"Browse for input file\" id=\"\"",
        "Button name=\"Browse folder for release input\" id=\"\"",
        "Button name=\"Add...\" id=\"\"",
        "Button name=\"Remove\" id=\"\"",
        "Button name=\"Remove All\" id=\"\"",
        "Button name=\"Move Up\" id=\"\"",
        "Button name=\"Move Down\" id=\"\"",
        "DataGrid name=\"Stored Files\" id=\"StoredFilesGrid\"",
        "GridSplitter name=\"Resize stored files and output\" id=\"\"",
        "TextBox name=\"Output path\" id=\"OutputTextBox\"",
        "Button name=\"Browse for output path\" id=\"\"",
        "CheckBox name=\"Auto-include files — Scan release directory for .nfo, .sfv, proof images, .m3u, .cue, .log files.\" id=\"\"",
        "CheckBox name=\"Auto-create SRS — Create .srs files for samples found in Sample/ subdirectory.\" id=\"\"",
        "CheckBox name=\"Vobsub SRR — Create nested SRR files for subtitle archives found in Subs/ directories.\" id=\"\"",
        "CheckBox name=\"Store fix RAR — For fix/patch releases, store the main RAR file as proof.\" id=\"\"",
        "CheckBox name=\"Allow compressed — Accept RAR volumes that use compression (method != Store).\" id=\"\"",
        "CheckBox name=\"OSO hashes — Compute and store OpenSubtitles OSO hashes for archived files.\" id=\"\"",
        "CheckBox name=\"Languages.diz — Extract language metadata from VobSub .idx files and store in the SRR.\" id=\"\"",
        "TextBox name=\"App name:\" id=\"\"",
        "Button name=\"Create SRR\" id=\"\"",
        "Button name=\"Save log...\" id=\"\"",
    ];

    private static readonly IReadOnlyList<string> CompactModeTabOrderFixture =
    [
        "ToggleButton name=\"Help\" id=\"\"",
        "TextBox name=\"Input path\" id=\"InputTextBox\"",
        "Button name=\"Browse for input file\" id=\"\"",
        "Button name=\"Browse folder for release input\" id=\"\"",
        "Button name=\"Add...\" id=\"\"",
        "Button name=\"Remove\" id=\"\"",
        "Button name=\"Remove All\" id=\"\"",
        "Button name=\"Move Up\" id=\"\"",
        "Button name=\"Move Down\" id=\"\"",
        "DataGrid name=\"Stored Files\" id=\"StoredFilesGrid\"",
        "GridSplitter name=\"Resize stored files and output\" id=\"\"",
        "TextBox name=\"Output path\" id=\"OutputTextBox\"",
        "Button name=\"Browse for output path\" id=\"\"",
        "CheckBox name=\"Auto-include files — Scan release directory for .nfo, .sfv, proof images, .m3u, .cue, .log files.\" id=\"\"",
        "CheckBox name=\"Auto-create SRS — Create .srs files for samples found in Sample/ subdirectory.\" id=\"\"",
        "CheckBox name=\"Vobsub SRR — Create nested SRR files for subtitle archives found in Subs/ directories.\" id=\"\"",
        "CheckBox name=\"Store fix RAR — For fix/patch releases, store the main RAR file as proof.\" id=\"\"",
        "CheckBox name=\"Allow compressed — Accept RAR volumes that use compression (method != Store).\" id=\"\"",
        "CheckBox name=\"OSO hashes — Compute and store OpenSubtitles OSO hashes for archived files.\" id=\"\"",
        "CheckBox name=\"Languages.diz — Extract language metadata from VobSub .idx files and store in the SRR.\" id=\"\"",
        "TextBox name=\"App name:\" id=\"\"",
        "Button name=\"Create SRR\" id=\"\"",
        "Button name=\"Save log...\" id=\"\"",
    ];
}
