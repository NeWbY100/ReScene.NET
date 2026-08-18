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
using ReScene.SRS;

namespace ReScene.Manager.Tests;

/// <summary>
/// Small-window layout degradation tests for <see cref="SampleRestorerView"/> (switch height
/// DERIVED from the view's own measured expanded floor — see <see cref="Threshold"/> —
/// config row AutoToStar 110 compact / 80 help-open, log 80, Help body MaxHeight
/// 40, compact CI bound <see cref="CompactInvariantRig.CiBound"/> == 307, pinned band ceiling
/// 75). This is the view whose action row and log measured 0px at 700×450 BASE state under
/// today's DockPanel — the headline defect (section 5 below).
/// <para>
/// The one structural feature no other converted view has: a genuinely-editable, VIRTUALIZED
/// <c>DataGrid</c> (SRSEntriesGrid) inside the config band's own ScrollViewer. Two
/// consequences threaded through this file, both confirmed empirically (see
/// <c>ScrollHandoffBehaviorTests</c>'s own remarks for the decompiled evidence):
/// <list type="bullet">
///   <item>DataGrid virtualizes its rows — a row's <see cref="CheckBox"/> does not exist as a
///     realized <see cref="Control"/> at all until the grid's OWN internal scroll brings it into
///     ITS OWN viewport. <c>CompactViewRig.AssertReachableByWheel/Keyboard/Thumb</c> all require
///     an ALREADY-resolvable target reference, so they cannot be used verbatim for "the grid's
///     last row's checkbox" the way they are for plain, always-realized controls in every other
///     view — section 2's own reachability case for that target uses genuine arrow-key input
///     directly (which drives both the grid's own realization AND, via
///     <see cref="ScrollHandoffBehavior"/>, the outer's reveal) instead.</item>
///   <item>In NORMAL (non-edit) browsing, Tab does not move between DataGrid cells at all — only
///     each row's directly-interactive CheckBox (Focusable AND IsTabStop, unlike
///     <c>DataGridCell</c> itself, which is Focusable but NOT a tab stop) is a genuine Tab stop.
///     The tab-order tests below therefore seed a SMALL, fully-realized SRSEntries count (2) so
///     the grid's own contribution to tab order is deterministic — every row's checkbox is
///     already realized without any scrolling, avoiding a virtualization-dependent fixture.</item>
/// </list>
/// </para>
/// </summary>
public class SampleRestorerCompactTests
{
    // ── Inert VM construction (mirrors SampleRestorerViewTests.CreateViewModel) ──

    private sealed class InertSampleRestorerService : ISampleRestorerService
    {
        public event EventHandler<SRSReconstructionProgressEventArgs>? Progress { add { } remove { } }

        public List<SRSEntryInfo> GetSRSEntries(string srrFilePath) => [];

        public Task<SRSReconstructionResult> RestoreSampleAsync(
            string srrFilePath, string srsFileName, string mediaFilePath, string outputPath, CancellationToken ct)
            => Task.FromResult(new SRSReconstructionResult(true, true, 0, 0, 0, 0, null));
    }

    private sealed class InlineUiDispatcher : IUiDispatcher
    {
        public void Invoke(Action action) => action();
        public void Post(Action action) => action();
        public void Post(Action action, UiDispatcherPriority priority) => action();
        public bool CheckAccess() => true;
    }

    private static SampleRestorerViewModel CreateVm() =>
        new(
            new InertSampleRestorerService(),
            new AvaloniaFileDialogService(static () => null), // headless: dialogs no-op, never block
            new InlineUiDispatcher());

    private static SampleRestorerViewModel.SRSFileEntry Entry(string srsFileName, string sampleFileName, bool isSelected = true) =>
        new() { SRSFileName = srsFileName, SampleFileName = sampleFileName, MediaFilePath = string.Empty, Status = "Pending", IsSelected = isSelected };

    private static SampleRestorerView BuildWorstCase()
    {
        SampleRestorerViewModel vm = CreateVm();
        ForceWorstCase(vm);
        return new SampleRestorerView { DataContext = vm };
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
    /// Sets the three paths <c>CanRestore()</c> needs for Restore All to be genuinely enabled.
    /// <para>
    /// FINDING: setting <c>SRRFilePath</c> synchronously triggers
    /// <c>SampleRestorerViewModel.OnSRRFilePathChanged</c> → fire-and-forget
    /// <c>LoadSRSEntriesAsync</c>, which clears <c>SRSEntries</c> synchronously (before its first
    /// <c>await</c> — harmless here since nothing has been seeded onto the VM yet) but leaves a
    /// QUEUED continuation that later — once anything calls <c>Dispatcher.UIThread.RunJobs()</c>,
    /// which <see cref="CompactViewRig.HostAt"/> always does — logs "Found 0 SRS file(s) in SRR"
    /// into <c>vm.LogEntries</c> (the inert service's <c>GetSRSEntries</c> always returns an empty
    /// list) and sets <c>SRRStatus</c> to a "no samples found" warning. Draining that continuation
    /// HERE (an explicit <c>RunJobs</c>) and clearing its two side effects BEFORE the view is ever
    /// constructed keeps the Log ListBox empty and SRRStatus at a clean baseline for callers that
    /// seed their own SRSEntries/FieldStatus afterward — otherwise a REAL <c>ListBoxItem</c>
    /// becomes an extra, unaccounted-for Tab stop (found via a red tab-order-snapshot run: this
    /// view's log ListBox items are genuine Tab stops, unlike every other converted view's own log
    /// test, none of which react to a path property with an async reload).
    /// </para>
    /// </summary>
    private static void SetPathsForCanRestore(SampleRestorerViewModel vm)
    {
        vm.SRRFilePath = @"C:\release\sample.srr";
        vm.MediaDirectoryPath = @"C:\release\media";
        vm.OutputDirectoryPath = @"C:\release\output";
        Dispatcher.UIThread.RunJobs();
        vm.LogEntries.Clear();
        vm.SRRStatus = FieldStatus.None;
        vm.MatchStatus = FieldStatus.None;
    }

    /// <summary>
    /// The worst case (case 1): all conditionals forced together. FieldStatusLines
    /// (SRR/Media — Output carries none today) set with realistic wrapping-length messages;
    /// IsRestoring + ShowProgress true (forces Cancel/ProgressMessage/ProgressBar visible);
    /// SRSEntries populated to 12 rows (overflows the grid's own 250-DIP MaxHeight, so it renders
    /// AT that cap) — a real row count, not a token one: an earlier version of this method used a
    /// modest 2-row population instead, which weakened the fixture enough to hide a genuine
    /// defect rather than exercising it: MEASURED, with 12 rows and NO production fix, that inner
    /// heights from 536 (the smallest expanded height when that was measured) up to ~640 leave the
    /// ENTIRE log band (row 3) — not merely
    /// clipped, but translated fully below the window's own bottom edge (e.g. at 536: log at
    /// window-Y [693,773] against a 675-tall window). <see cref="SampleRestorerView"/>'s own
    /// constructor now fixes this with a
    /// dynamic, window-height-aware cap on the config ScrollViewer (see its own remarks) — proven
    /// safe across that exact previously-unsafe range by
    /// <see cref="Invariant_ExpandedMode_NeverClipsAcrossUnsafeHeightRange"/> below, using REAL
    /// arranged rendering + the clip-aware <see cref="AssertFullyWithinWindow"/>, not
    /// <see cref="CompactInvariantRig.MeasureFloor"/>.
    /// </summary>
    private static void ForceWorstCase(SampleRestorerViewModel vm)
    {
        vm.SRRStatus = FieldStatus.Warning("This SRR contains no embedded SRS sample data — check it was created correctly.");
        vm.MatchStatus = FieldStatus.Warning("Only some samples matched a file in this media folder; the rest need manual assignment.");
        vm.IsRestoring = true;
        vm.ShowProgress = true;
        vm.OverallProgressText = "Restoring 8 of 12...";
        vm.ProgressMessage = "Reconstructing sample 8: verifying CRC against the expected checksum...";
        for (int i = 0; i < 12; i++)
        {
            vm.SRSEntries.Add(Entry($"sample{i:D2}.srs", $"sample{i:D2}.mkv"));
        }
    }

    // ── 1. Invariant (the four one-sum checks; CompactInvariantRig) — RED-FIRST against today's DockPanel ──

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
    /// THE invariant: at every height around this view's own switch point, whichever mode is
    /// active actually fits. See
    /// <see cref="CompactInvariantRig.AssertActiveModeFitsAroundSwitchPoint"/> — no height and no
    /// verdict in it is a platform-calibrated number.
    /// </summary>
    [AvaloniaFact]
    public void Invariant_ActiveModeFits_AtEveryHeightAroundTheSwitchPoint() =>
        CompactInvariantRig.AssertActiveModeFitsAroundSwitchPoint("SampleRestorer", BuildWorstCase);

    /// <summary>
    /// The REAL, user-facing guarantee <see cref="Invariant_ExpandedModeFloor_UnderDerivedThreshold"/>'s
    /// own <c>MeasureFloor</c> methodology cannot directly observe:
    /// MEASURED that <c>MeasureFloor</c>'s bare, unconstrained <c>Measure(Infinity)</c> call
    /// reports each Auto row's own UNCONSTRAINED desired height, while a REAL Grid arrange pass
    /// additionally SHRINKS Auto rows when the total genuinely exceeds available space — a
    /// mechanism the static measure-only check cannot see (this is also why
    /// <see cref="SampleRestorerView"/>'s own safety cap reserves an
    /// <c>ArrangeRoundingSlack</c> margin beyond the minimum its own arithmetic strictly needs —
    /// see that constant's own remarks for the exact measured gap). This test instead uses REAL
    /// arranged rendering (<see cref="CompactViewRig.HostAt"/>) and the clip-aware
    /// <see cref="AssertFullyWithinWindow"/> across the range measured unsafe before the
    /// production fix, expressed as offsets ABOVE this view's own switch point so it follows the
    /// derivation rather than pinning heights that meant something on one font stack (1 through
    /// ~105 DIPs above it — the entire log band, row 3, previously
    /// translated fully below the window's own bottom edge in that range with a 12-row grid and no
    /// fix), plus a comfortably-larger height, to prove the actual defect this change exists to fix
    /// is gone — not merely that one abstract number moved.
    /// </summary>
    [AvaloniaTheory]
    [InlineData(1.0)]     // the smallest possible expanded height
    [InlineData(25.0)]
    [InlineData(65.0)]    // mid-way through the measured-unsafe range
    [InlineData(105.0)]   // the measured-unsafe range's own upper edge
    [InlineData(365.0)]   // comfortably larger -- the cap must not OVER-constrain when there's room to spare
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
            Button restoreAll = window.GetVisualDescendants().OfType<Button>().Single(b => b.Content is "Restore All");
            Button cancel = window.GetVisualDescendants().OfType<Button>().Single(b => b.Content is "Cancel");
            ListBox log = window.GetVisualDescendants().OfType<ListBox>().Single();
            Assert.True(cancel.IsVisible);

            AssertFullyWithinWindow(restoreAll, window);
            AssertFullyWithinWindow(cancel, window);
            AssertFullyWithinWindow(log, window);
        }
        finally { window.Close(); }
    }


    [AvaloniaFact]
    public void Invariant_CompactFloor_WithinCiBound_AndPinnedBandRowSane()
    {
        SampleRestorerViewModel vm = CreateVm();
        ForceWorstCase(vm);
        var view = new SampleRestorerView { DataContext = vm };

        (Window window, Grid root) = CompactViewRig.HostAt(view, CompactInner);
        try
        {
            Assert.Contains("compactHeight", root.Classes);

            // One sum: the compact minimum (config row min -> 80) AND the body's own MaxHeight
            // (40) both spend the same 307-DIP budget — never checked independently.
            double floor = CompactInvariantRig.MeasureFloor(root);
            Assert.True(floor <= CompactInvariantRig.CiBound,
                $"compact floor {floor:F1} must be <= {CompactInvariantRig.CiBound}");

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

    // ── 2. Rendered matrix: compact @700x450, fresh @Threshold, fresh @Threshold+1 ──

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
        AssertGridLastRowAndActionReachable(innerHeight);
        AssertTabWalk(innerHeight);
    }

    private static void AssertNoClip(double innerHeight, bool expectCompact)
    {
        SampleRestorerViewModel vm = CreateVm();
        ForceWorstCase(vm); // criterion B worst case: every conditional forced, grid populated
        var view = new SampleRestorerView { DataContext = vm };

        (Window window, Grid root) = CompactViewRig.HostAt(view, innerHeight);
        try
        {
            Assert.Equal(expectCompact, root.Classes.Contains("compactHeight"));
            CompactInvariantRig.AssertArrangesWithin(root, root.Bounds.Height);
        }
        finally { window.Close(); }
    }

    /// <summary>
    /// Criterion A for this view's two hardest-to-reach targets: the GRID's OWN last-row
    /// checkbox (a virtualized target — see this class's own remarks: no realized Control exists
    /// for it until the grid's own internal scroll creates one, so
    /// <c>CompactViewRig.AssertReachableByWheel/Keyboard/Thumb</c> cannot be used verbatim; genuine
    /// arrow-key input is used directly instead, since it is the one input route that drives BOTH
    /// the grid's own realization and, via <see cref="ScrollHandoffBehavior"/>, the outer band's
    /// reveal in the same gesture) and "Restore All" (a plain pinned button — the STANDARD
    /// three-route check applies unchanged, matching every other view's own primary-action case).
    /// </summary>
    private static void AssertGridLastRowAndActionReachable(double innerHeight)
    {
        SampleRestorerViewModel vm = CreateVm();
        SetPathsForCanRestore(vm);
        for (int i = 0; i < 12; i++)
        {
            vm.SRSEntries.Add(Entry($"sample{i:D2}.srs", $"sample{i:D2}.mkv"));
        }
        var view = new SampleRestorerView { DataContext = vm };
        (Window window, Grid root) = CompactViewRig.HostAt(view, innerHeight);
        try
        {
            ScrollViewer configScroller = window.GetVisualDescendants().OfType<ScrollViewer>().Single(sv => Grid.GetRow(sv) == 1);
            configScroller.Offset = default;
            Dispatcher.UIThread.RunJobs();

            SampleRestorerViewModel.SRSFileEntry lastEntry = vm.SRSEntries[^1];
            CheckBox firstCheckbox = window.GetVisualDescendants().OfType<CheckBox>()
                .Single(cb => ReferenceEquals(cb.DataContext, vm.SRSEntries[0]));
            firstCheckbox.Focus();
            Dispatcher.UIThread.RunJobs();

            for (int i = 0; i < vm.SRSEntries.Count - 1; i++)
            {
                window.KeyPressQwerty(PhysicalKey.ArrowDown, RawInputModifiers.None);
                window.KeyReleaseQwerty(PhysicalKey.ArrowDown, RawInputModifiers.None);
                Dispatcher.UIThread.RunJobs();
            }

            CheckBox lastCheckbox = window.GetVisualDescendants().OfType<CheckBox>()
                .Single(cb => ReferenceEquals(cb.DataContext, lastEntry));
            AssertFullyWithinWindow(lastCheckbox, window);

            Button restoreAll = window.GetVisualDescendants().OfType<Button>().Single(b => b.Content is "Restore All");
            Assert.True(restoreAll.IsEffectivelyEnabled, "test precondition: Restore All must be enabled to be a meaningful keyboard-reachability target");
            AssertReachableByAllThreeRoutes(window, configScroller, restoreAll);
        }
        finally { window.Close(); }
    }

    /// <summary>
    /// Each route is exercised from a genuine "not yet visible" start (offset reset between
    /// routes) — otherwise the first route scrolling the target into view would make the other
    /// two trivially no-op without ever exercising their own mechanism. Harmless no-op for the
    /// pinned Restore All button (never inside <paramref name="scroller"/>'s clipped-out region,
    /// so every route's own early "already visible" check returns immediately) — still a real
    /// assertion that it stays fully visible regardless.
    /// </summary>
    private static void AssertReachableByAllThreeRoutes(Window window, ScrollViewer scroller, Control target)
    {
        scroller.Offset = default;
        Dispatcher.UIThread.RunJobs();
        CompactViewRig.AssertReachableByWheel(window, target);

        scroller.Offset = default;
        Dispatcher.UIThread.RunJobs();
        CompactViewRig.AssertReachableByKeyboard(window, target);

        scroller.Offset = default;
        Dispatcher.UIThread.RunJobs();
        CompactViewRig.AssertReachableByThumb(window, target);
    }

    /// <summary>
    /// ORDER-ORACLE standard (project house rule, blocking if violated): the expected stop
    /// sequence is resolved INDEPENDENTLY, up front, by unique identity (bound command for
    /// Buttons, x:Name for TextBoxes, DataContext reference for the grid's own CheckBoxes) —
    /// never derived from a walk's own observed output. This view's three "Browse" buttons used to
    /// describe identically as well, and no longer do — they carry distinct names now. Every row
    /// checkbox still does (same AutomationProperties.Name, no x:Name), which is what
    /// <see cref="AssertSameControlSequence_SwappedIdenticallyDescribedRowCheckboxes_FailsNamingTheMismatch"/>
    /// selects on to prove the discrimination directly. The SAME reference-equality mechanism (not
    /// a separate test) covers every other position too, since it checks all of them.
    /// <para>
    /// Uses a SMALL, deliberately fully-realized SRSEntries count (2) — see this class's own
    /// remarks on why a virtualization-dependent count would make the grid's own tab-stop
    /// contribution non-deterministic. Adopts the hardened <see cref="CompactViewRig"/> idioms
    /// directly: a forward walk with a completeness check, plus a REVERSE walk anchored at the
    /// forward walk's own LAST stop that must retrace the ENTIRE forward order and land back on
    /// the forward walk's FIRST stop. SampleRestorerView is a single keyboard-navigation scope
    /// (no nested TabControl, no splitter) — one forward walk plus one reverse walk.
    /// </para>
    /// </summary>
    private static void AssertTabWalk(double innerHeight)
    {
        SampleRestorerViewModel vm = CreateVm();
        SetPathsForCanRestore(vm);
        SampleRestorerViewModel.SRSFileEntry entryA = Entry("sampleA.srs", "sampleA.mkv");
        SampleRestorerViewModel.SRSFileEntry entryB = Entry("sampleB.srs", "sampleB.mkv");
        vm.SRSEntries.Add(entryA);
        vm.SRSEntries.Add(entryB); // Restore All enabled: >=1 selected entry (default IsSelected=true) + all three paths set
        var view = new SampleRestorerView { DataContext = vm };
        (Window window, Grid root) = CompactViewRig.HostAt(view, innerHeight);
        try
        {
            // A SECOND settle-and-clear: SetPathsForCanRestore's own RunJobs() can race a GENUINE
            // background-thread hop (LoadSRSEntriesAsync's Task.Run), occasionally running before
            // the continuation has actually posted back — HostAt's OWN window.Show()+RunJobs() has
            // since given it ample additional real time, so this second clear reliably removes any
            // stray "Found 0 SRS file(s)..." log entry regardless of that race's outcome.
            Dispatcher.UIThread.RunJobs();
            vm.LogEntries.Clear();

            bool compact = root.Classes.Contains("compactHeight");

            // Independent ground truth, resolved BEFORE any walk runs — never derived from a
            // walk's own output. In compact mode Help starts collapsed (condition 5): the body's
            // own prose is not a tab stop while collapsed, so the header toggle is the walk's
            // genuine entry point. In expanded/flat mode the disclosure contributes NOTHING to
            // tab order at all — SRR File's own Browse button is the first stop there, PROVEN
            // (not merely presumed) by the reverse walk's own boundary-landing assertion below.
            List<Control> independentOrder = ResolveIndependentExpectedOrder(window, vm, entryA, entryB, compact);
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

            // REVERSE: anchored at the forward walk's own LAST stop, checked against the
            // INDEPENDENT order's own reversal — NOT forwardOrder.Reverse() — so a genuine
            // same-described-sibling swap cannot hide behind a self-referential oracle.
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
    /// identifier (bound <c>RelayCommand</c> reference for Buttons, x:Name for TextBoxes/the
    /// DataGrid, DataContext reference for the grid's own CheckBoxes), NEVER by re-deriving from a
    /// walk's own observed output.
    /// <para>
    /// FINDING (corrects an assumption from the isolated <c>ScrollHandoffBehaviorTests</c> rig,
    /// where focus started already inside a realized cell, skipping past this): the
    /// <c>DataGrid</c> CONTROL ITSELF is Focusable AND IsTabStop (the framework default for
    /// any plain Control, unlike <c>DataGridCell</c> which is Focusable but NOT a tab stop) — Tab
    /// arriving from the PRECEDING external control (OutputDirTextBox) lands on the grid itself
    /// FIRST, confirmed empirically (a real Tab walk against this view), before a SECOND Tab
    /// descends into its first realized row's checkbox.
    /// </para>
    /// </summary>
    private static List<Control> ResolveIndependentExpectedOrder(
        Window window, SampleRestorerViewModel vm,
        SampleRestorerViewModel.SRSFileEntry entryA, SampleRestorerViewModel.SRSFileEntry entryB, bool compact)
    {
        Button srrBrowse = window.GetVisualDescendants().OfType<Button>().Single(b => ReferenceEquals(b.Command, vm.BrowseSRRCommand));
        TextBox srrTextBox = window.GetVisualDescendants().OfType<TextBox>().Single(t => t.Name == "SRRFileTextBox");
        Button mediaBrowse = window.GetVisualDescendants().OfType<Button>().Single(b => ReferenceEquals(b.Command, vm.BrowseMediaDirectoryCommand));
        TextBox mediaTextBox = window.GetVisualDescendants().OfType<TextBox>().Single(t => t.Name == "MediaDirTextBox");
        Button outputBrowse = window.GetVisualDescendants().OfType<Button>().Single(b => ReferenceEquals(b.Command, vm.BrowseOutputDirectoryCommand));
        TextBox outputTextBox = window.GetVisualDescendants().OfType<TextBox>().Single(t => t.Name == "OutputDirTextBox");
        DataGrid srsEntriesGrid = window.GetVisualDescendants().OfType<DataGrid>().Single(g => g.Name == "SRSEntriesGrid");
        CheckBox checkboxA = window.GetVisualDescendants().OfType<CheckBox>().Single(cb => ReferenceEquals(cb.DataContext, entryA));
        CheckBox checkboxB = window.GetVisualDescendants().OfType<CheckBox>().Single(cb => ReferenceEquals(cb.DataContext, entryB));
        Button restoreAll = window.GetVisualDescendants().OfType<Button>().Single(b => ReferenceEquals(b.Command, vm.RestoreCommand));
        Button saveLog = window.GetVisualDescendants().OfType<Button>().Single(b => ReferenceEquals(b.Command, vm.SaveLogCommand));

        // Field THEN button, per row: each row is a right-docked DockPanel whose button is declared
        // first, so its markup order is the reverse of what the user sees. TabIndex pins plus
        // KeyboardNavigation.TabNavigation="Local" now put the walk back into the rendered order.
        // Before that fix this list read button-then-field, which is what the rows actually did.
        List<Control> order = [srrTextBox, srrBrowse, mediaTextBox, mediaBrowse, outputTextBox, outputBrowse, srsEntriesGrid, checkboxA, checkboxB, restoreAll, saveLog];

        if (compact)
        {
            ScrollViewer helpBody = window.GetVisualDescendants().OfType<ScrollViewer>()
                .Single(sv => sv.Name == "HelpBody");
            order.Insert(0, helpBody);
        }

        return order;
    }

    /// <summary>
    /// Proves <see cref="AssertSameControlSequence"/> — and therefore <see cref="AssertTabWalk"/>'s
    /// own forward/reverse checks — is genuinely sensitive to a PERMUTATION of identically-
    /// described controls, not just to controls going missing.
    /// <para>
    /// RE-POINTED, not redesigned. This used to swap two of this view's three "Browse" buttons,
    /// which described identically because none carried a name. Naming all nine bare Browse
    /// buttons across the three sibling views removed that pair. Unlike the Reconstructor and the
    /// two SRS views — which were left with no identically-described pair at all, and whose
    /// equivalents had to be split into a positional test plus a constructed-pair test — THIS view
    /// still has a real one: the SRS grid's per-row checkboxes all carry the same
    /// <c>AutomationProperties.Name</c> ("Restore this sample") and no x:Name, so two rows are
    /// genuinely indistinguishable to the description channel while being different objects.
    /// </para>
    /// <para>
    /// The pair's indistinguishability is ASSERTED here rather than assumed, which is the whole
    /// point of re-pointing carefully: if the grid ever gained a per-row distinguisher this test
    /// would be proving nothing, and it says so instead of passing quietly.
    /// </para>
    /// </summary>
    [AvaloniaFact]
    public void AssertSameControlSequence_SwappedIdenticallyDescribedRowCheckboxes_FailsNamingTheMismatch()
    {
        SampleRestorerViewModel vm = CreateVm();
        SetPathsForCanRestore(vm);
        SampleRestorerViewModel.SRSFileEntry entryA = Entry("sampleA.srs", "sampleA.mkv");
        SampleRestorerViewModel.SRSFileEntry entryB = Entry("sampleB.srs", "sampleB.mkv");
        vm.SRSEntries.Add(entryA);
        vm.SRSEntries.Add(entryB);
        var view = new SampleRestorerView { DataContext = vm };
        (Window window, Grid root) = CompactViewRig.HostAt(view, ExpandedInner);
        try
        {
            // See AssertTabWalk's own identical comment: a second settle-and-clear closes a real
            // background-thread race in SetPathsForCanRestore's own first attempt.
            Dispatcher.UIThread.RunJobs();
            vm.LogEntries.Clear();

            List<Control> independentOrder = ResolveIndependentExpectedOrder(window, vm, entryA, entryB, compact: false);
            Control sentinel = independentOrder[0];
            sentinel.Focus();
            Dispatcher.UIThread.RunJobs();

            IReadOnlyList<Control> forwardOrder = CompactViewRig.CaptureTabOrderControls(window, root, independentOrder).Order;

            List<int> rowCheckboxIndexes = [.. Enumerable.Range(0, independentOrder.Count)
                .Where(i => CompactViewRig.Describe(independentOrder[i]) == "CheckBox name=\"Restore this sample\" id=\"\"")];
            Assert.True(rowCheckboxIndexes.Count >= 2,
                "this covering test requires at least 2 identically-described row checkboxes to swap — if the grid gained a " +
                "per-row distinguisher, this test proves nothing about description-blind comparison and must be redesigned, " +
                "not silently re-pointed");

            // The premise, asserted: the two really are indistinguishable to the description
            // channel, and really are different objects. Both halves matter — identical
            // descriptions make the swap invisible to a string comparison, and distinct references
            // are what AssertSameControlSequence is supposed to notice.
            Assert.Equal(
                CompactViewRig.Describe(independentOrder[rowCheckboxIndexes[0]]),
                CompactViewRig.Describe(independentOrder[rowCheckboxIndexes[1]]));
            Assert.False(ReferenceEquals(independentOrder[rowCheckboxIndexes[0]], independentOrder[rowCheckboxIndexes[1]]));

            List<Control> tampered = [.. independentOrder];
            (tampered[rowCheckboxIndexes[0]], tampered[rowCheckboxIndexes[1]]) = (tampered[rowCheckboxIndexes[1]], tampered[rowCheckboxIndexes[0]]);

            Xunit.Sdk.FailException ex = Assert.Throws<Xunit.Sdk.FailException>(
                () => AssertSameControlSequence(tampered, forwardOrder, "forward"));

            Assert.Contains($"position {rowCheckboxIndexes[0]}", ex.Message, StringComparison.Ordinal);
            Assert.Contains("same description does not mean same control instance", ex.Message, StringComparison.Ordinal);

            // The untampered, genuinely independent expectation still passes against the SAME
            // real walk — the failure above was the tampering, not an actual defect.
            AssertSameControlSequence(independentOrder, forwardOrder, "forward (untampered, sanity check)");
        }
        finally { window.Close(); }
    }

    /// <summary>
    /// OBJECT-REFERENCE-exact sequence comparison — asserts <paramref name="actual"/> is,
    /// position for position, the SAME control REFERENCES as <paramref name="expected"/>, not
    /// merely the same DESCRIPTIONS. Mirrors every other view's own helper of the same shape.
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
    // Both entry points simply invoke the SAME hardened AssertTabWalk (section 2's own
    // criterion-C helper, now ALSO the exact-order/completeness/reverse-boundary authority) at
    // the exact heights RenderedMatrix_CompactAt700x450_... and
    // RenderedMatrix_FreshAtThresholdPlusOne_... already exercise.

    [AvaloniaFact]
    public void TabOrderSnapshot_Normal_MatchesPreChangeFixture() => AssertTabWalk(ExpandedInner);

    [AvaloniaFact]
    public void TabOrderSnapshot_Compact_MatchesSpecSection2Order() => AssertTabWalk(CompactInner);

    // ── 4. Chrome ─────────────────────────────────────────────────────

    [AvaloniaFact]
    public void SingleIntroInstance_ExistsInBothModes()
    {
        SampleRestorerViewModel vm = CreateVm();
        var normalView = new SampleRestorerView { DataContext = vm };
        (Window normalWindow, Grid normalRoot) = CompactViewRig.HostAt(normalView, ExpandedInner);
        try
        {
            Assert.DoesNotContain("compactHeight", normalRoot.Classes);
            Assert.Equal(1, CountIntroInstances(normalWindow));
        }
        finally { normalWindow.Close(); }

        SampleRestorerViewModel vm2 = CreateVm();
        var compactView = new SampleRestorerView { DataContext = vm2 };
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
            .Count(t => t.Text is not null && t.Text.StartsWith("Restore media files using SRS data", StringComparison.Ordinal));

    [AvaloniaFact]
    public void CompactEntry_HelpBodyIsReachable_AndRestoringRelocatesFocus()
    {
        SampleRestorerViewModel vm = CreateVm();
        var view = new SampleRestorerView { DataContext = vm };
        (Window window, Grid root) = CompactViewRig.HostAt(view, CompactInner);
        try
        {
            // "Invocable" is proven by REACHABILITY/usability (focusable, enabled, unobscured, a
            // real Tab lands on it) — this body has no interactive children (plain prose), so its
            // own compact-only-focusable ScrollViewer IS the route.
            ScrollViewer body = root.GetVisualDescendants().OfType<ScrollViewer>().Single(s => s.Name == "HelpBody");
            Assert.True(body.Focusable);
            Assert.True(body.IsEffectivelyEnabled);
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
            Assert.False(body.Focusable, "flat mode drops the compact-only Tab stop on the Help body");

            // The staged-focus guard's actual point: restoring from a focus captured on the body
            // (which just went non-focusable — flat mode's base style, not the compact-only
            // override) must relocate focus, not strand it. RestoreFocusTarget was wired to
            // SRRFileTextBox in the view's ctor, so that is where it must land.
            TextBox srrFileTextBox = window.GetVisualDescendants().OfType<TextBox>().Single(t => t.Name == "SRRFileTextBox");
            Assert.True(srrFileTextBox.IsFocused,
                "restoring from a focused compact body must relocate focus to the wired RestoreFocusTarget (SRRFileTextBox), not strand it");

            // The resize-driven focus-recovery target must have an accessible name — before this
            // fix it had none at all. Same resolution technique as
            // SRSEntriesGrid_UIAName_ResolvesToEmbeddedSRSFilesHeader above (the real
            // AutomationPeer, not the raw attached property) so this proves what a screen reader
            // actually announces on landing here, not merely that a XAML attribute exists.
            Assert.Equal("SRR file path", ControlAutomationPeer.CreatePeerForElement(srrFileTextBox).GetName());

            window.Height -= restoreDelta;
            Dispatcher.UIThread.RunJobs();
            Assert.Contains("compactHeight", root.Classes);
            Assert.True(body.Focusable, "re-entering compact restores the Help body's keyboard route");
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void CompactMinimums_ConfigRowMin80_BodyMaxHeight40_OutputTextBoxKeyboardReachable()
    {
        SampleRestorerViewModel vm = CreateVm();
        var view = new SampleRestorerView { DataContext = vm };
        (Window window, Grid root) = CompactViewRig.HostAt(view, CompactInner);
        try
        {

            int configRow = Grid.GetRow(window.GetVisualDescendants().OfType<ScrollViewer>().Single(sv => Grid.GetRow(sv) == 1));
            Assert.Equal(80, root.RowDefinitions[configRow].MinHeight);

            ScrollViewer body = root.GetVisualDescendants().OfType<ScrollViewer>().Single(s => s.Name == "HelpBody");
            Assert.Equal(40, body.MaxHeight);

            TextBox outputTextBox = window.GetVisualDescendants().OfType<TextBox>().Single(t => t.Name == "OutputDirTextBox");
            CompactViewRig.AssertReachableByKeyboard(window, outputTextBox);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void CompactBodyScroller_NotFocusableNormally_FocusableAndNamedInCompact()
    {
        SampleRestorerViewModel vm = CreateVm();
        var normalView = new SampleRestorerView { DataContext = vm };
        (Window normalWindow, Grid normalRoot) = CompactViewRig.HostAt(normalView, ExpandedInner);
        try
        {
            // Flat mode force-expands the body (so this scroller IS realized/attached even though
            // the header stays hidden) — criterion F requires it NOT be a new Tab stop.
            ScrollViewer body = normalRoot.GetVisualDescendants().OfType<ScrollViewer>()
                .Single(sv => sv.Name == "HelpBody");
            Assert.False(body.Focusable);
        }
        finally { normalWindow.Close(); }

        SampleRestorerViewModel vm2 = CreateVm();
        var compactView = new SampleRestorerView { DataContext = vm2 };
        (Window compactWindow, Grid compactRoot) = CompactViewRig.HostAt(compactView, CompactInner);
        try
        {
            Dispatcher.UIThread.RunJobs();

            ScrollViewer body = compactRoot.GetVisualDescendants().OfType<ScrollViewer>().Single(s => s.Name == "HelpBody");
            Assert.True(body.Focusable);
            Assert.Equal("Help content", AutomationProperties.GetName(body));
        }
        finally { compactWindow.Close(); }
    }

    /// <summary>
    /// All four built-ins exercised with genuine key input against a REAL, attached ScrollViewer
    /// — never a synthetic Offset-setter poke. This view's own intro prose is short enough that
    /// it never genuinely overflows the 40-DIP Help body cap at the app's own enforced minimum
    /// width, so — mirroring every other converted view's own identical finding — the body's Text
    /// is temporarily lengthened (synthetic content, this test only) so the four keys can be
    /// proven against REAL overflow.
    /// </summary>
    [AvaloniaFact]
    public void CompactBodyScroller_AllFourPageKeys_MoveOffsetBothWaysAndToExtents()
    {
        SampleRestorerViewModel vm = CreateVm();
        var view = new SampleRestorerView { DataContext = vm };
        (Window window, Grid root) = CompactViewRig.HostAt(view, CompactInner);
        try
        {
            Dispatcher.UIThread.RunJobs();

            ScrollViewer body = root.GetVisualDescendants().OfType<ScrollViewer>().Single(s => s.Name == "HelpBody");
            TextBlock introText = body.GetVisualDescendants().OfType<TextBlock>().Single();
            introText.Text = string.Concat(Enumerable.Repeat("Restore media files using SRS data embedded in an SRR file. ", 20));
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
    /// Directly asserts the defect this change exists to fix: with band 1 (config, holding the
    /// SRR/Media/Output sections AND the SRSEntriesGrid) independently scrolled to its top AND its
    /// bottom extreme, the pinned Restore All button, the Cancel button, and the ProgressBar —
    /// translated into window coordinates — stay fully inside the window the entire time, with
    /// IsRestoring + ShowProgress forced (ForceWorstCase — the worst case for the pinned band's
    /// own height, and the exact conditions that render the Cancel button and ProgressBar at
    /// all). RED-FIRST: this is the strongest red in the feature — pre-change (today's DockPanel,
    /// no Grid rows / scroll clipping at all), the action row and log measured 0px at 700×450
    /// BASE state with ZERO conditionals even forced.
    /// </summary>
    [AvaloniaFact]
    public void PinnedActionBand_RestoreAllCancelAndProgressBarStayWithinWindow_BandOneScrolledToTopAndBottom()
    {
        SampleRestorerViewModel vm = CreateVm();
        SetPathsForCanRestore(vm);
        ForceWorstCase(vm); // forces IsRestoring (Cancel visible) + ShowProgress (ProgressBar visible) + a populated grid
        var view = new SampleRestorerView { DataContext = vm };
        (Window window, Grid root) = CompactViewRig.HostAt(view, CompactInner);
        try
        {
            Assert.Contains("compactHeight", root.Classes);
            Button restoreAll = window.GetVisualDescendants().OfType<Button>().Single(b => b.Content is "Restore All");
            Button cancel = window.GetVisualDescendants().OfType<Button>().Single(b => b.Content is "Cancel");
            ProgressBar progressBar = window.GetVisualDescendants().OfType<ProgressBar>().Single();
            Assert.True(cancel.IsVisible);
            Assert.True(progressBar.IsVisible);

            ScrollViewer configScroller = window.GetVisualDescendants().OfType<ScrollViewer>().Single(sv => Grid.GetRow(sv) == 1);
            Assert.True(configScroller.Extent.Height > configScroller.Viewport.Height,
                "test precondition: band 1 must genuinely overflow so top/bottom are distinct positions");

            configScroller.Offset = new Vector(0, 0);
            Dispatcher.UIThread.RunJobs();
            AssertFullyWithinWindow(restoreAll, window);
            AssertFullyWithinWindow(cancel, window);
            AssertFullyWithinWindow(progressBar, window);

            configScroller.Offset = new Vector(0, configScroller.Extent.Height - configScroller.Viewport.Height);
            Dispatcher.UIThread.RunJobs();
            AssertFullyWithinWindow(restoreAll, window);
            AssertFullyWithinWindow(cancel, window);
            AssertFullyWithinWindow(progressBar, window);
        }
        finally { window.Close(); }
    }

    /// <summary>
    /// CLIP-AWARE: a naive "translated point within the window's
    /// own outer rectangle" check can false-PASS a control that is genuinely obscured by an
    /// INTERMEDIATE <c>ClipToBounds</c> ancestor — e.g. a target nested inside the config band's
    /// own <see cref="ScrollViewer"/> can translate to a point that numerically falls inside the
    /// window's rectangle while the ScrollViewer's own clipped viewport hides it entirely. Mirrors
    /// <c>CompactViewRig.IsFullyVisibleWithinWindow</c>'s / <c>CompactHeightBehavior.IsObscured</c>'s
    /// own cumulative-intersection algorithm exactly: progressively intersect the window's own
    /// bounds with every <c>ClipToBounds</c> ancestor's own translated bounds, then require the
    /// control's OWN full translated rect to fit within that combined visible region — not merely
    /// its top-left/bottom-right corners against the window alone.
    /// <para>
    /// A degenerate (zero-width or zero-height) control translates to a single point, which
    /// trivially satisfies any containment check — exactly the pre-change defect (the action row
    /// and log measuring zero height) would have
    /// slipped past a containment-only check. Effective visibility and a positive size are
    /// asserted FIRST, unconditionally.
    /// </para>
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

    // ── 6. Handoff (ScrollHandoffBehavior exercised through the real view) ──

    /// <summary>
    /// Wheel at the grid's own extent moves the config band's OWN scroller — a spec-level user
    /// expectation (small-window config band scrolls its DataGrid host; a wheel gesture must not
    /// dead-end at the grid's own edge) that must keep a regression guard regardless of which code
    /// provides it.
    /// <para>
    /// NOT a <see cref="ScrollHandoffBehavior"/> test (renamed and re-documented):
    /// this behavior's own wheel mechanism was removed — investigation found it not merely
    /// redundant with Avalonia's native <c>ScrollViewer.IsScrollChainingEnabled</c> default (true,
    /// never overridden in this app) but incapable of ever providing the "future insurance" it was
    /// kept for (see <see cref="ScrollHandoffBehavior"/>'s own remarks). This test still passes
    /// unchanged with that mechanism gone — proven directly during the investigation that led to
    /// its removal — because the platform default alone already produces this exact result. It is
    /// kept, renamed, as the real view's own platform-level regression guard for that expectation.
    /// </para>
    /// </summary>
    [AvaloniaFact]
    public void WheelHandoffAtGridExtent_PlatformDefaultMovesConfigBandScroller()
    {
        SampleRestorerViewModel vm = CreateVm();
        for (int i = 0; i < 12; i++)
        {
            vm.SRSEntries.Add(Entry($"sample{i:D2}.srs", $"sample{i:D2}.mkv"));
        }
        var view = new SampleRestorerView { DataContext = vm };
        (Window window, Grid root) = CompactViewRig.HostAt(view, CompactInner);
        try
        {
            DataGrid grid = window.GetVisualDescendants().OfType<DataGrid>().Single(g => g.Name == "SRSEntriesGrid");
            ScrollBar gridBar = grid.GetVisualDescendants().OfType<ScrollBar>().Single(b => b.Orientation == Orientation.Vertical);
            ScrollViewer configScroller = window.GetVisualDescendants().OfType<ScrollViewer>().Single(sv => Grid.GetRow(sv) == 1);

            // Stage 1: the grid is NOT visible at all at band 1's own default (offset 0) scroll
            // position in compact mode — the config row's own viewport (MinHeight 110, help
            // closed) is far shorter than the three sections preceding the grid in the StackPanel.
            // A real user reaches it by first wheeling over whatever OF band 1 IS already visible
            // (its own currently-visible center — always non-empty, since SOME of band 1 is
            // always on-screen) until the grid gains ANY visible sliver.
            const int MaxRevealTicks = 200;
            for (int tick = 0; tick < MaxRevealTicks && TryVisibleCenterInWindow(grid, window) is null; tick++)
            {
                window.MouseWheel(VisibleCenterInWindow(configScroller, window), new Vector(0, -1));
                Dispatcher.UIThread.RunJobs();
            }
            Assert.NotNull(TryVisibleCenterInWindow(grid, window)); // test precondition: the grid must have SOME visible sliver to wheel "at" it at all

            // Stage 2: NOW wheel directly at the grid's own (partially) visible position, driving
            // its own internal virtualization to its bottom extent.
            const int MaxDriveTicks = 200;
            for (int tick = 0; tick < MaxDriveTicks && gridBar.Value < gridBar.Maximum; tick++)
            {
                window.MouseWheel(VisibleCenterInWindow(grid, window), new Vector(0, -1));
                Dispatcher.UIThread.RunJobs();
            }
            Assert.True(gridBar.Value >= gridBar.Maximum, "test precondition: the grid must genuinely reach its own bottom extent");

            double gridBefore = gridBar.Value;
            double outerBefore = configScroller.Offset.Y;
            Assert.True(configScroller.Extent.Height > configScroller.Viewport.Height,
                "test precondition: the config band must have genuine room to scroll for the hand-off to be observable");

            // Stage 3: the actual assertion — one more wheel DOWN at the (still visible) grid,
            // now genuinely exhausted internally, must hand off to the config band's own scroller.
            window.MouseWheel(VisibleCenterInWindow(grid, window), new Vector(0, -1));
            Dispatcher.UIThread.RunJobs();

            Assert.Equal(gridBefore, gridBar.Value); // the grid itself did not move further
            Assert.True(configScroller.Offset.Y > outerBefore,
                $"the config band's own scroller should have moved from {outerBefore}, was {configScroller.Offset.Y}");
        }
        finally { window.Close(); }
    }

    /// <summary>
    /// The center of <paramref name="element"/>'s TRUE visible region — the intersection of its
    /// own translated bounds against EVERY <c>ClipToBounds</c> ancestor's own translated bounds,
    /// progressively intersected (mirrors <c>CompactHeightBehavior.IsObscured</c> /
    /// <c>CompactViewRig.IsFullyVisibleWithinWindow</c>'s own cumulative-clip algorithm) — NOT
    /// merely the raw window's own outer rectangle. FINDING (a real bug this fixes): a naive
    /// "intersect against window bounds only" check reports a false-positive "visible" point for
    /// a control that is genuinely scrolled out of an intermediate ScrollViewer's own clipped
    /// viewport (e.g. the config band's own ScrollViewer, itself smaller than the full window) —
    /// the raw translated rect can still overlap the window's outer rectangle even though an
    /// ancestor's own clip has scrolled it fully out of view. Returns null (never throws) when the
    /// element has no visible region at all, so callers can loop "wheel elsewhere until this
    /// becomes visible" without exceptions as control flow.
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

    private static Rect? TransformRect(Visual from, Rect localRect, Visual to)
    {
        Point? topLeft = from.TranslatePoint(new Point(localRect.X, localRect.Y), to);
        Point? bottomRight = from.TranslatePoint(new Point(localRect.Right, localRect.Bottom), to);
        return topLeft is { } tl && bottomRight is { } br ? new Rect(tl, br) : null;
    }

    /// <summary>
    /// Focuses a bottom-row cell while the grid is half-clipped by the
    /// band — real Down-arrow presses walk cell "currency" deeper into the (virtualized) grid;
    /// <see cref="ScrollHandoffBehavior"/> chains <c>BringIntoView</c> to the config band's own
    /// ScrollViewer each time the current row changes, so the row ends fully visible even though
    /// literal keyboard focus itself settles on the DataGrid control, not a specific cell/row
    /// (see this class's and <c>ScrollHandoffBehaviorTests</c>'s own remarks on why).
    /// </summary>
    [AvaloniaFact]
    public void Handoff_KeyboardNavigation_ChainsBringIntoViewToOuterViewer_BottomRowEndsFullyVisible()
    {
        SampleRestorerViewModel vm = CreateVm();
        for (int i = 0; i < 12; i++)
        {
            vm.SRSEntries.Add(Entry($"sample{i:D2}.srs", $"sample{i:D2}.mkv"));
        }
        var view = new SampleRestorerView { DataContext = vm };
        (Window window, Grid root) = CompactViewRig.HostAt(view, CompactInner);
        try
        {
            ScrollViewer configScroller = window.GetVisualDescendants().OfType<ScrollViewer>().Single(sv => Grid.GetRow(sv) == 1);
            configScroller.Offset = default;
            Dispatcher.UIThread.RunJobs();

            CheckBox firstCheckbox = window.GetVisualDescendants().OfType<CheckBox>()
                .Single(cb => ReferenceEquals(cb.DataContext, vm.SRSEntries[0]));
            firstCheckbox.Focus();
            Dispatcher.UIThread.RunJobs();

            SampleRestorerViewModel.SRSFileEntry lastEntry = vm.SRSEntries[^1];
            for (int i = 0; i < vm.SRSEntries.Count - 1; i++)
            {
                window.KeyPressQwerty(PhysicalKey.ArrowDown, RawInputModifiers.None);
                window.KeyReleaseQwerty(PhysicalKey.ArrowDown, RawInputModifiers.None);
                Dispatcher.UIThread.RunJobs();
            }

            CheckBox lastCheckbox = window.GetVisualDescendants().OfType<CheckBox>()
                .Single(cb => ReferenceEquals(cb.DataContext, lastEntry));
            AssertFullyWithinWindow(lastCheckbox, window);
        }
        finally { window.Close(); }
    }

    /// <summary>
    /// "Inner arrow-key navigation stays inside the grid": with only a few SRSEntries (comfortably realized within the grid's own 250-DIP box without
    /// ever needing the grid's OWN internal virtualization scroll to kick in), navigating between
    /// them must never move the grid's OWN internal offset off zero — the grid's virtualization
    /// mechanism must not activate for a movement that never needed it.
    /// <para>
    /// Deliberately NOT asserted here: "the config band's own (outer) scroller never moves at
    /// all". MEASURED: even reaching row 0's own checkbox in compact mode (MinHeight 110, help
    /// closed) already requires the outer to scroll some — the config band's own tight budget
    /// means the outer is a live participant throughout compact-mode navigation, not a bystander;
    /// <see cref="Handoff_KeyboardNavigation_ChainsBringIntoViewToOuterViewer_BottomRowEndsFullyVisible"/>
    /// is the dedicated, positive test for that chaining. This test's own, narrower claim is about
    /// the GRID's internal virtualization specifically, which a small, fully-realized entry count
    /// can prove cleanly regardless of how much the outer itself needs to move.
    /// </para>
    /// </summary>
    [AvaloniaFact]
    public void Handoff_InnerArrowKeyNavigation_WithinGridsOwnViewport_NeverActivatesGridsOwnVirtualizationScroll()
    {
        SampleRestorerViewModel vm = CreateVm();
        for (int i = 0; i < 3; i++)
        {
            vm.SRSEntries.Add(Entry($"sample{i:D2}.srs", $"sample{i:D2}.mkv"));
        }
        var view = new SampleRestorerView { DataContext = vm };
        (Window window, Grid root) = CompactViewRig.HostAt(view, CompactInner);
        try
        {
            DataGrid grid = window.GetVisualDescendants().OfType<DataGrid>().Single(g => g.Name == "SRSEntriesGrid");
            ScrollBar gridBar = grid.GetVisualDescendants().OfType<ScrollBar>().Single(b => b.Orientation == Orientation.Vertical);

            CheckBox firstCheckbox = window.GetVisualDescendants().OfType<CheckBox>()
                .Single(cb => ReferenceEquals(cb.DataContext, vm.SRSEntries[0]));
            firstCheckbox.Focus();
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(0, gridBar.Value);

            for (int i = 0; i < vm.SRSEntries.Count - 1; i++)
            {
                window.KeyPressQwerty(PhysicalKey.ArrowDown, RawInputModifiers.None);
                window.KeyReleaseQwerty(PhysicalKey.ArrowDown, RawInputModifiers.None);
                Dispatcher.UIThread.RunJobs();

                Assert.Equal(0, gridBar.Value); // 3 rows comfortably fit the grid's own 250-DIP box — its own virtualization scroll never needs to move
            }
        }
        finally { window.Close(); }
    }

    // ── 7. LabeledBy: the grid's UIA name resolves to its own header ──

    [AvaloniaFact]
    public void SRSEntriesGrid_UIAName_ResolvesToEmbeddedSRSFilesHeader()
    {
        SampleRestorerViewModel vm = CreateVm();
        var view = new SampleRestorerView { DataContext = vm };
        (Window window, Grid root) = CompactViewRig.HostAt(view, ExpandedInner);
        try
        {
            DataGrid grid = window.GetVisualDescendants().OfType<DataGrid>().Single(g => g.Name == "SRSEntriesGrid");
            Assert.Equal("Embedded SRS Files", ControlAutomationPeer.CreatePeerForElement(grid).GetName());
        }
        finally { window.Close(); }
    }

    // ── 8. Frame-rig parity (criterion F: normal-mode pixels unchanged) ──

    /// <summary>
    /// Same technique as every other converted view's own hardened version (RenderTargetBitmap +
    /// CopyPixels, exact integer pixel size gate BEFORE any byte is read, full-buffer compare — no
    /// mask/crop/intersection). LOCAL copy of <c>AssertFullRasterPixelIdentity</c> /
    /// <c>RenderToPixelBuffer</c> (not promoted into the shared rig — promotion is an open
    /// controller decision).
    /// </summary>
    [AvaloniaFact]
    public void FrameRig_NormalMode_HeaderRegionMatchesPreDisclosureShape()
    {
        SampleRestorerViewModel vm = CreateVm();
        var view = new SampleRestorerView { DataContext = vm };
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

                // UNLIKE every other converted view, this intro TextBlock carries NO right inset
                // (see the XAML's own comment: the usual house-rule 4px margin measurably shifted
                // this view's own longer sentence's word-wrap, a genuine pixel difference, not
                // just a narrower margin) — so the caption's own width matches old's exactly, zero
                // narrowing. MEASURED, not assumed.
                double widthNarrowing = oldSize.Width - newCaptionSize.Width;
                Assert.Equal(0.0, widthNarrowing, precision: 0);

                // The hosted ROW itself (the Expander, hug-bug-fixed) is the SAME width as old's
                // own natural width — MEASURED.
                Assert.Equal(oldSize.Width, newRowSize.Width, precision: 0);

                AssertFullRasterPixelIdentity(oldRow0, oldSize, newRow0, newRowSize);
            }
            finally { oldWindow.Close(); }
        }
        finally { newWindow.Close(); }
    }

    /// <summary>
    /// Extends the raster comparison beyond row 0: row 0 alone
    /// never exercised the config band's own <c>StackPanel</c>, whose local, unconditional
    /// <c>Margin="0,0,4,0"</c> (an earlier version of this markup) DID change expanded-mode
    /// rendering — a real defect the row-0-only comparison structurally could not see. The SRR
    /// File caption is the config band's own FIRST child: its available width depends entirely on
    /// the enclosing StackPanel's own margin, so it directly exercises the fix (the
    /// <c>compactScrollInset</c> style class, zero at normal size).
    /// </summary>
    [AvaloniaFact]
    public void FrameRig_NormalMode_ConfigBandCaptionMatchesPreChangeShape()
    {
        SampleRestorerViewModel vm = CreateVm();
        var view = new SampleRestorerView { DataContext = vm };
        (Window newWindow, Grid newRoot) = CompactViewRig.HostAt(view, ExpandedInner);
        try
        {
            Assert.DoesNotContain("compactHeight", newRoot.Classes);
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();

            ScrollViewer configScroller = newRoot.Children.OfType<ScrollViewer>().Single(sv => Grid.GetRow(sv) == 1);
            TextBlock newCaption = configScroller.GetVisualDescendants().OfType<TextBlock>()
                .Single(tb => tb.Inlines is [Run { Text: "SRR File " }, ..]);
            Size newCaptionSize = newCaption.Bounds.Size;

            Window oldWindow = BuildPreConversionSrrCaptionWindow();
            try
            {
                oldWindow.Show();
                AvaloniaHeadlessPlatform.ForceRenderTimerTick();
                Dispatcher.UIThread.RunJobs();
                var oldCaption = (Control)oldWindow.Content!;
                Size oldSize = oldCaption.Bounds.Size;

                Assert.Equal(oldSize.Height, newCaptionSize.Height, precision: 0);

                // Zero narrowing at normal size -- the fix's own point (a local, unconditional
                // inset on the enclosing StackPanel would have shown up here as a 4-DIP gap).
                Assert.Equal(oldSize.Width, newCaptionSize.Width, precision: 0);

                AssertFullRasterPixelIdentity(oldCaption, oldSize, newCaption, newCaptionSize);
            }
            finally { oldWindow.Close(); }
        }
        finally { newWindow.Close(); }
    }

    /// <summary>
    /// Verbatim reconstruction of SampleRestorerView.axaml's SRR File caption TextBlock before this
    /// task (git history). DIAGNOSED: the two &lt;Run&gt; elements sit on separate
    /// source lines in the XAML, and Avalonia's XAML parser collapses the inter-tag newline +
    /// indentation into a THIRD, implicit whitespace-only <see cref="Run"/> (plain, default-styled
    /// — it does not inherit the preceding Run's own local FontWeight) in the real
    /// <c>Inlines</c> collection — CONFIRMED directly (a throwaway diagnostic dump of the real,
    /// live-hosted TextBlock's own <c>Inlines</c> showed exactly 3 entries: "SRR File ", a bare
    /// " ", then the caption text — not the 2 this reconstruction originally assumed). Omitting it
    /// silently drops one space before the em dash, shifting every pixel from the em dash onward
    /// and producing a false raster mismatch unrelated to this task's own compactScrollInset fix.
    /// No equivalent gap exists after the second Run (whitespace immediately before the closing
    /// &lt;/TextBlock&gt; tag is trimmed, not collapsed to content) — confirmed by the same dump
    /// reporting exactly 3 inlines, not 4.
    /// </summary>
    private static Window BuildPreConversionSrrCaptionWindow()
    {
        var textBlock = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 2) };
        textBlock.Inlines!.Add(new Run { Text = "SRR File ", FontWeight = FontWeight.SemiBold });
        textBlock.Inlines!.Add(new Run { Text = " " });
        textBlock.Inlines!.Add(new Run
        {
            Text = "— The .srr file containing embedded .srs sample data.",
            Foreground = (IBrush?)Application.Current!.FindResource("ForegroundSecondary"),
            FontSize = (double)Application.Current!.FindResource("FontSizeCaption")!,
        });

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
        SampleRestorerViewModel vm = CreateVm();
        var view = new SampleRestorerView { DataContext = vm };
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

    /// <summary>Verbatim reconstruction of SampleRestorerView.axaml's row-0 TextBlock before this task (git history).</summary>
    private static Window BuildPreDisclosureRow0Window()
    {
        var textBlock = new TextBlock
        {
            Text = "Restore media files using SRS data embedded in an SRR file. Select a directory containing the media files to restore — metadata and structure will be reconstructed and CRC-verified.",
            Foreground = (IBrush?)Application.Current!.FindResource("ForegroundSecondary"),
            FontSize = (double)Application.Current!.FindResource("FontSizeCaption")!,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 6),
        };

        return new Window { Width = CompactInvariantRig.InnerWidth, SizeToContent = SizeToContent.Height, Content = textBlock };
    }

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

    // ── Fixtures (captured from real, green CompactViewRig.CaptureTabOrderControls runs against
    // the finished view). Each entry
    // is CompactViewRig.Describe's own format (real automation peer name plus x:Name, reported
    // separately) — a human-readable regression net, NOT the discriminating check itself.
    // Same-typed siblings that describe identically (both grid row checkboxes) are disambiguated
    // by AssertTabWalk's OWN independent, reference-based checks.
    // All three picker TextBoxes carry an explicit AutomationProperties.Name: SRRFileTextBox
    // gained "SRR file path" during the compact-layout work, and MediaDirTextBox/OutputDirTextBox
    // — recorded here at the time as untouched pre-existing a11y debt — gained "Media directory
    // path"/"Output directory path" in the naming pass, following the same "<subject> path"
    // convention with each subject taken from that row's own visible caption.
    // The three "Browse" buttons WERE deliberately left bare in that pass, and are now named too
    // ("Browse for SRR file"/"…media directory"/"…output directory"), closing the WCAG 3.2.4
    // asymmetry that pass left behind — the same function announced "Browse for <target>" in the
    // Reconstructor and the Creator and a bare "Browse" here. They are therefore no longer the
    // identically-described siblings the covering test selects on; it now selects on the grid's
    // per-row checkboxes, which genuinely still describe identically. ──

    /// <summary>
    /// Normal mode, starting at SRR File's own Browse button — PROVEN first (not presumed): the
    /// reverse walk anchored at the tail end (Save log) retraces this exact sequence backwards and
    /// lands back on this same Browse button, empirically confirming nothing precedes it.
    /// </summary>
    private static readonly IReadOnlyList<string> NormalModeTabOrderFixture =
    [
        "TextBox name=\"SRR file path\" id=\"SRRFileTextBox\"",
        "Button name=\"Browse for SRR file\" id=\"\"",
        "TextBox name=\"Media directory path\" id=\"MediaDirTextBox\"",
        "Button name=\"Browse for media directory\" id=\"\"",
        "TextBox name=\"Output directory path\" id=\"OutputDirTextBox\"",
        "Button name=\"Browse for output directory\" id=\"\"",
        "DataGrid name=\"Embedded SRS Files\" id=\"SRSEntriesGrid\"",
        "CheckBox name=\"Restore this sample\" id=\"\"",
        "CheckBox name=\"Restore this sample\" id=\"\"",
        "Button name=\"Restore All\" id=\"\"",
        "Button name=\"Save log...\" id=\"\"",
    ];

    /// <summary>
    /// Compact order: disclosure header toggle → (body skipped: Help starts collapsed
    /// per condition 5, so the plain-prose body is IsVisible=false and correctly excluded from Tab
    /// order) → identical tail to normal mode.
    /// </summary>
    private static readonly IReadOnlyList<string> CompactModeTabOrderFixture =
    [
        "ScrollViewer name=\"Help content\" id=\"HelpBody\"",
        "TextBox name=\"SRR file path\" id=\"SRRFileTextBox\"",
        "Button name=\"Browse for SRR file\" id=\"\"",
        "TextBox name=\"Media directory path\" id=\"MediaDirTextBox\"",
        "Button name=\"Browse for media directory\" id=\"\"",
        "TextBox name=\"Output directory path\" id=\"OutputDirTextBox\"",
        "Button name=\"Browse for output directory\" id=\"\"",
        "DataGrid name=\"Embedded SRS Files\" id=\"SRSEntriesGrid\"",
        "CheckBox name=\"Restore this sample\" id=\"\"",
        "CheckBox name=\"Restore this sample\" id=\"\"",
        "Button name=\"Restore All\" id=\"\"",
        "Button name=\"Save log...\" id=\"\"",
    ];
}
