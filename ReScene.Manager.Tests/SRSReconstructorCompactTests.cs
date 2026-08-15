using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Automation.Peers;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
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
/// Small-window layout degradation tests for <see cref="SRSReconstructorView"/> (switch height
/// DERIVED from the view's own measured expanded floor — see <see cref="Threshold"/> —
/// config row AutoToStar 110 compact / 80 help-open, log 80, Help body MaxHeight 40, compact CI
/// bound <see cref="CompactInvariantRig.CiBound"/> == 307, pinned band ceiling 75). Adapts
/// <c>SRSCreatorCompactTests</c>' five-part shape to this view's even simpler structure: no ISO
/// file-selection row (the Reconstructor auto-detects the matching VOB set — no manual picker),
/// and NO progress controls at all (no Cancel button, no ProgressBar). Adds two view-specific
/// sections: #5 pinned band (the actual defect this change fixes — Rebuild Sample and the result
/// banner while band 1 is scrolled to both extremes) and #6 the result-cap/re-arm BINDING (the
/// VM half of the re-arm contract is unit-tested in SRSReconstructorViewModelTests; this file
/// only asserts the view-level binding survives the visibility race).
/// </summary>
public class SRSReconstructorCompactTests
{
    // ── Inert VM construction (mirrors SRSReconstructorViewTests.CreateViewModel) ──

    private sealed class InertReconstructionService : ISRSReconstructionService
    {
        public event EventHandler<SRSReconstructionProgressEventArgs>? Progress { add { } remove { } }
        public event EventHandler<SRSScanProgressEventArgs>? ScanProgress { add { } remove { } }

        public Task<SRSReconstructionResult> RebuildAsync(string srsFilePath, string mediaFilePath, string outputPath, CancellationToken ct)
            => Task.FromResult(new SRSReconstructionResult(true, true, 0, 0, 0, 0, null));
    }

    private sealed class InertTempDirectoryService : ITempDirectoryService
    {
        public string CreateTempDirectory() => Path.GetTempPath();
        public void Cleanup(string? tempDir) { }
    }

    private sealed class InlineUiDispatcher : IUiDispatcher
    {
        public void Invoke(Action action) => action();
        public void Post(Action action) => action();
        public void Post(Action action, UiDispatcherPriority priority) => action();
        public bool CheckAccess() => true;
    }

    private static SRSReconstructorViewModel CreateVm() =>
        new(
            new InertReconstructionService(),
            new AvaloniaFileDialogService(static () => null), // headless: dialogs no-op, never block
            new InertTempDirectoryService(),
            new InlineUiDispatcher());

    private static SRSReconstructorView BuildWorstCase()
    {
        SRSReconstructorViewModel vm = CreateVm();
        ForceWorstCase(vm);
        return new SRSReconstructorView { DataContext = vm };
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
    /// The worst-case layout, forced together: all three FieldStatusLines non-None (with
    /// realistic, wrapping-length messages — FieldStatusLine's message TextBlock wraps, so a
    /// short message would understate the floor), and ShowResult with a genuinely two-line
    /// ResultSummary (long enough to wrap at the 676-DIP inner width — exercises the MaxLines=2
    /// cap rather than a one-line best case). Used by every invariant/no-clip check so "worst
    /// case" means the same thing everywhere it is asserted.
    /// </summary>
    private static void ForceWorstCase(SRSReconstructorViewModel vm)
    {
        vm.SRSStatus = FieldStatus.Warning("This SRS contains no sample file data — check it was created correctly.");
        vm.MediaStatus = FieldStatus.Warning("This media file's size doesn't match what the SRS expects.");
        vm.OutputStatus = FieldStatus.Info("Auto-filled from the SRS sample name. Change it if needed.");
        vm.ShowResult = true;
        vm.ResultSuccess = true;
        vm.ResultSummary = "CRC32 match: 0ABCDEF1 (123,456,789 bytes) — reconstructed successfully from the matching VOB title set found on the ISO image.";
    }

    // ── 1. Invariant (the four one-sum checks; CompactInvariantRig) ────

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
        CompactInvariantRig.AssertActiveModeFitsAroundSwitchPoint("SRSReconstructor", BuildWorstCase);

    [AvaloniaFact]
    public void Invariant_CompactFloor_HelpClosed_WithinCiBound()
    {
        SRSReconstructorViewModel vm = CreateVm();
        ForceWorstCase(vm);
        var view = new SRSReconstructorView { DataContext = vm };

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
        SRSReconstructorViewModel vm = CreateVm();
        ForceWorstCase(vm);
        var view = new SRSReconstructorView { DataContext = vm };

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

            // Pinned band (row 2) is never the budget donor — its natural height stays small
            // and positive regardless of mode, and within CompactInvariantRig.PinnedBandCeiling even with the
            // result banner forced visible with a two-line summary (ForceWorstCase).
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
        AssertReachabilityNoClipAndTabWalk(Threshold + 1, expectCompact: false);

    private static void AssertReachabilityNoClipAndTabWalk(double innerHeight, bool expectCompact)
    {
        AssertNoClip(innerHeight, expectCompact);
        AssertConfigAndActionReachable(innerHeight);
        AssertTabWalk(innerHeight);
    }

    private static void AssertNoClip(double innerHeight, bool expectCompact)
    {
        SRSReconstructorViewModel vm = CreateVm();
        ForceWorstCase(vm); // criterion B worst case: every conditional forced
        var view = new SRSReconstructorView { DataContext = vm };

        (Window window, Grid root) = CompactViewRig.HostAt(view, innerHeight);
        try
        {
            Assert.Equal(expectCompact, root.Classes.Contains("compactHeight"));
            CompactInvariantRig.AssertArrangesWithin(root, root.Bounds.Height);
        }
        finally { window.Close(); }
    }

    /// <summary>
    /// Criterion A for the last config control (Output's own Browse button)
    /// and the primary action (Rebuild Sample). Both routed through the config band's own
    /// ScrollViewer, identified by Grid.Row (the Help body is ALSO a bare, non-templated
    /// ScrollViewer, so Grid.Row is the only unambiguous handle).
    /// <para>
    /// SRS/Media/Output paths are set so <c>CanRebuild()</c> is true and the button is
    /// genuinely enabled — for the DEFAULT inert VM (all paths empty) Rebuild Sample is
    /// disabled and Avalonia correctly excludes it from Tab order entirely (same precedent as
    /// the Reconstructor's own "Start" button and SRSCreator's own "Create SRS" button);
    /// "reachable by keyboard" is only a meaningful check once the button can actually take
    /// focus.
    /// </para>
    /// </summary>
    private static void AssertConfigAndActionReachable(double innerHeight)
    {
        SRSReconstructorViewModel vm = CreateVm();
        vm.SRSFilePath = @"C:\release\sample.srs";
        vm.MediaFilePath = @"C:\release\sample.mkv";
        vm.OutputPath = @"C:\release\sample.rebuilt.mkv";
        var view = new SRSReconstructorView { DataContext = vm };
        (Window window, Grid root) = CompactViewRig.HostAt(view, innerHeight);
        try
        {
            ScrollViewer configScroller = window.GetVisualDescendants().OfType<ScrollViewer>().Single(sv => Grid.GetRow(sv) == 1);

            Button outputBrowse = window.GetVisualDescendants().OfType<Button>().Single(b => ReferenceEquals(b.Command, vm.BrowseOutputCommand));
            AssertReachableByAllThreeRoutes(window, configScroller, outputBrowse);

            Button rebuildButton = window.GetVisualDescendants().OfType<Button>().Single(b => b.Content is "Rebuild Sample");
            Assert.True(rebuildButton.IsEffectivelyEnabled, "test precondition: Rebuild Sample must be enabled to be a meaningful keyboard-reachability target");
            AssertReachableByAllThreeRoutes(window, configScroller, rebuildButton);
        }
        finally { window.Close(); }
    }

    /// <summary>
    /// Each route is exercised from a genuine "not yet visible" start (offset reset between
    /// routes) — otherwise the first route scrolling the target into view would make the other
    /// two trivially no-op without ever exercising their own mechanism. Harmless no-op for the
    /// pinned Rebuild Sample button (never inside <paramref name="scroller"/>'s clipped-out
    /// region, so every route's own early "already visible" check returns immediately) — still
    /// a real assertion that it stays fully visible regardless.
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
    /// Buttons, x:Name for TextBoxes) — never derived from a walk's own observed output. This
    /// view has THREE identically-described "Browse" buttons (SRS/Media/Output); if any two
    /// were genuinely swapped in the tree, a description-only fixture (or a forward-derived
    /// reverse expectation) could not tell — proven directly by
    /// <see cref="AssertSameControlSequence_IdenticallyDescribedControls_AreDistinguishedByReference"/>
    /// below. Unlike SRSCreatorView's own equivalent, every TextBox here already carries a
    /// distinct x:Name (SRSFileTextBox/MediaFileTextBox/OutputTextBox) — no Width-based
    /// disambiguation hack needed.
    /// <para>
    /// Adopts the hardened <see cref="CompactViewRig"/> idioms directly: a forward walk with a
    /// completeness check (an unreached control fails loudly rather than being silently
    /// absorbed), plus a REVERSE walk anchored at the forward walk's own LAST stop (the
    /// unambiguous boundary — the log's Save button) that must retrace the ENTIRE forward order
    /// and land back on the forward walk's FIRST stop — the actual, empirical proof that the
    /// presumed-first control really is first. SRSReconstructorView is a single keyboard-
    /// navigation scope (no nested TabControl, no splitter) — one forward walk plus one
    /// reverse walk, no per-scope machinery.
    /// </para>
    /// </summary>
    private static void AssertTabWalk(double innerHeight)
    {
        SRSReconstructorViewModel vm = CreateVm();
        vm.SRSFilePath = @"C:\release\sample.srs";
        vm.MediaFilePath = @"C:\release\sample.mkv";
        vm.OutputPath = @"C:\release\sample.rebuilt.mkv"; // Rebuild Sample enabled: its own position is pinned, not left unverified
        var view = new SRSReconstructorView { DataContext = vm };
        (Window window, Grid root) = CompactViewRig.HostAt(view, innerHeight);
        try
        {
            bool compact = root.Classes.Contains("compactHeight");

            // Independent ground truth, resolved BEFORE any walk runs — never derived from a
            // walk's own output. In compact mode Help starts collapsed (condition 5): the
            // body's own prose is not a tab stop while collapsed, so the header toggle is the
            // walk's genuine entry point. In expanded/flat mode the disclosure contributes
            // NOTHING to tab order at all (header hidden by style, body plain non-focusable
            // prose) — SRS File's own Browse button is the first stop there, PROVEN (not
            // merely presumed) by the reverse walk's own boundary-landing assertion below.
            List<Control> independentOrder = ResolveIndependentExpectedOrder(window, vm, compact);
            Control sentinel = independentOrder[0];

            IReadOnlyList<string> fixture = compact ? CompactModeTabOrderFixture : NormalModeTabOrderFixture;

            sentinel.Focus();
            Dispatcher.UIThread.RunJobs();
            CompactViewRig.TabOrderCapture forwardCapture = CompactViewRig.CaptureTabOrderControls(window, root, independentOrder);
            IReadOnlyList<Control> forwardOrder = forwardCapture.Order;
            Assert.Equal(fixture, forwardOrder.Select(CompactViewRig.Describe)); // human-readable regression net (renames, additions, removals)
            AssertSameControlSequence(independentOrder, forwardOrder, "forward"); // the actual discriminating check (same-described-sibling swaps)

            // The forward walk's terminal external target must be the SPECIFIC, expected
            // shell-chrome boundary — the rig's own fake shell (CompactViewRig's BuildShell)
            // puts a "_File" MenuItem right after the TabControl in Z-order (same finding as
            // Reconstructor's and SRSCreator's own, against the identical shared shell).
            MenuItem expectedExternalBoundary = window.GetVisualDescendants().OfType<MenuItem>()
                .Single(m => m.Header as string == "_File");
            Assert.True(forwardCapture.FirstExternalTarget is not null,
                "forward capture should have left root's scope onto an external control, not ended via a stable loop within root");
            Assert.True(ReferenceEquals(expectedExternalBoundary, forwardCapture.FirstExternalTarget),
                $"forward capture's terminal external target should be {CompactViewRig.Describe(expectedExternalBoundary)}, " +
                $"not {CompactViewRig.Describe(forwardCapture.FirstExternalTarget)} — same description does not mean same control instance.");

            // REVERSE: anchored at the forward walk's own LAST stop (the unambiguous boundary),
            // never a presumed starting point. Checked against the INDEPENDENT order's own
            // reversal — NOT forwardOrder.Reverse() — so a genuine same-described-sibling swap
            // cannot hide behind a self-referential oracle.
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
    /// identifier (bound <c>RelayCommand</c> reference for Buttons, x:Name for TextBoxes),
    /// NEVER by re-deriving from a walk's own observed output.
    /// </summary>
    private static List<Control> ResolveIndependentExpectedOrder(Window window, SRSReconstructorViewModel vm, bool compact)
    {
        Button srsBrowse = window.GetVisualDescendants().OfType<Button>().Single(b => ReferenceEquals(b.Command, vm.BrowseSRSCommand));
        TextBox srsTextBox = window.GetVisualDescendants().OfType<TextBox>().Single(t => t.Name == "SRSFileTextBox");
        Button mediaBrowse = window.GetVisualDescendants().OfType<Button>().Single(b => ReferenceEquals(b.Command, vm.BrowseMediaCommand));
        TextBox mediaTextBox = window.GetVisualDescendants().OfType<TextBox>().Single(t => t.Name == "MediaFileTextBox");
        Button outputBrowse = window.GetVisualDescendants().OfType<Button>().Single(b => ReferenceEquals(b.Command, vm.BrowseOutputCommand));
        TextBox outputTextBox = window.GetVisualDescendants().OfType<TextBox>().Single(t => t.Name == "OutputTextBox");
        Button rebuildButton = window.GetVisualDescendants().OfType<Button>().Single(b => ReferenceEquals(b.Command, vm.RebuildCommand));
        Button saveLog = window.GetVisualDescendants().OfType<Button>().Single(b => ReferenceEquals(b.Command, vm.SaveLogCommand));

        // Field THEN button, per row — see SampleRestorerCompactTests' own note for the reversed
        // markup order these pins correct. Before that fix this list read button-then-field.
        List<Control> order = [srsTextBox, srsBrowse, mediaTextBox, mediaBrowse, outputTextBox, outputBrowse, rebuildButton, saveLog];

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
    /// own forward/reverse checks — is genuinely sensitive to a PERMUTATION, not just to controls
    /// going missing. Captures the REAL forward walk against the real, independent expected order,
    /// swaps two adjacent positions WITHIN THAT INDEPENDENT EXPECTATION (never within the observed
    /// walk), asserts the mismatch fails naming the specific position, then confirms the UNTAMPERED
    /// expectation still passes against the same real walk.
    /// <para>
    /// REDESIGNED for the same reason, and in the same shape, as <c>SRSCreatorCompactTests</c>'
    /// test of this name — see its doc for the full account. Short version: this used to swap two
    /// of the three identically-described "Browse" buttons; naming all three removed the last
    /// identically-described pair from this view, so the reference-versus-description half moved to
    /// <see cref="AssertSameControlSequence_IdenticallyDescribedControls_AreDistinguishedByReference"/>
    /// against a constructed pair rather than being re-pointed at a pair that is not genuinely
    /// indistinguishable.
    /// </para>
    /// </summary>
    [AvaloniaFact]
    public void AssertSameControlSequence_SwappedPositions_FailsNamingTheMismatch()
    {
        SRSReconstructorViewModel vm = CreateVm();
        vm.SRSFilePath = @"C:\release\sample.srs";
        vm.MediaFilePath = @"C:\release\sample.mkv";
        vm.OutputPath = @"C:\release\sample.rebuilt.mkv";
        var view = new SRSReconstructorView { DataContext = vm };
        (Window window, Grid root) = CompactViewRig.HostAt(view, ExpandedInner);
        try
        {
            List<Control> independentOrder = ResolveIndependentExpectedOrder(window, vm, compact: false);
            Control sentinel = independentOrder[0];
            sentinel.Focus();
            Dispatcher.UIThread.RunJobs();

            IReadOnlyList<Control> forwardOrder = CompactViewRig.CaptureTabOrderControls(window, root, independentOrder).Order;

            Assert.True(independentOrder.Count >= 2, "this covering test needs at least 2 stops to swap");
            List<Control> tampered = [.. independentOrder];
            (tampered[0], tampered[1]) = (tampered[1], tampered[0]);

            Xunit.Sdk.FailException ex = Assert.Throws<Xunit.Sdk.FailException>(
                () => AssertSameControlSequence(tampered, forwardOrder, "forward"));

            Assert.Contains("position 0", ex.Message, StringComparison.Ordinal);
            Assert.Contains("same description does not mean same control instance", ex.Message, StringComparison.Ordinal);

            // The untampered, genuinely independent expectation still passes against the SAME
            // real walk — the failure above was the tampering, not an actual defect.
            AssertSameControlSequence(independentOrder, forwardOrder, "forward (untampered, sanity check)");
        }
        finally { window.Close(); }
    }

    /// <summary>
    /// The half of the old covering test the naming pass would otherwise have silently dropped:
    /// that <see cref="AssertSameControlSequence"/> catches a permutation a DESCRIPTION-based
    /// comparison cannot see at all. Asserted against a constructed pair, because this view no
    /// longer contains one. Kept PER-SUITE for the reason
    /// <c>SRSCreatorCompactTests</c>' own copy documents: the helper is a private, per-suite
    /// method, so one shared test would prove the property for one copy and let the others drift.
    /// </summary>
    [AvaloniaFact]
    public void AssertSameControlSequence_IdenticallyDescribedControls_AreDistinguishedByReference()
    {
        var first = new Button { Content = "Browse" };
        var second = new Button { Content = "Browse" };
        var neighbour = new TextBox { Name = "Anchor" };

        Assert.Equal(CompactViewRig.Describe(first), CompactViewRig.Describe(second));
        Assert.False(ReferenceEquals(first, second));

        List<Control> actual = [first, neighbour, second];
        List<Control> swapped = [second, neighbour, first];

        // A description-based oracle is blind to the swap — the specific gap this helper closes.
        Assert.Equal(actual.Select(CompactViewRig.Describe), swapped.Select(CompactViewRig.Describe));

        Xunit.Sdk.FailException ex = Assert.Throws<Xunit.Sdk.FailException>(
            () => AssertSameControlSequence(swapped, actual, "constructed identical-description pair"));

        Assert.Contains("position 0", ex.Message, StringComparison.Ordinal);
        Assert.Contains("same description does not mean same control instance", ex.Message, StringComparison.Ordinal);

        AssertSameControlSequence(actual, actual, "constructed pair (untampered, sanity check)");
    }

    /// <summary>
    /// OBJECT-REFERENCE-exact sequence comparison — asserts <paramref name="actual"/> is,
    /// position for position, the SAME control REFERENCES as <paramref name="expected"/>, not
    /// merely the same DESCRIPTIONS. Mirrors <c>SRSCreatorCompactTests</c>' own helper of the
    /// same shape.
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
    // criterion-C helper, now ALSO the exact-order/completeness/reverse-boundary authority) at
    // the exact heights RenderedMatrix_CompactAt700x450_... and
    // RenderedMatrix_FreshAtThresholdPlusOne_... already exercise. Kept as separate, named entry
    // points so "the tab order is exactly this" reads as its own explicit, discoverable
    // assertion — not merely a side effect of a criterion-C reachability test.

    [AvaloniaFact]
    public void TabOrderSnapshot_Normal_MatchesPreChangeFixture() => AssertTabWalk(ExpandedInner);

    [AvaloniaFact]
    public void TabOrderSnapshot_Compact_MatchesSpecSection2Order() => AssertTabWalk(CompactInner);

    // ── 4. Chrome ─────────────────────────────────────────────────────

    [AvaloniaFact]
    public void SingleIntroInstance_ExistsInBothModes()
    {
        SRSReconstructorViewModel vm = CreateVm();
        var normalView = new SRSReconstructorView { DataContext = vm };
        (Window normalWindow, Grid normalRoot) = CompactViewRig.HostAt(normalView, ExpandedInner);
        try
        {
            Assert.DoesNotContain("compactHeight", normalRoot.Classes);
            Assert.Equal(1, CountIntroInstances(normalWindow));
        }
        finally { normalWindow.Close(); }

        SRSReconstructorViewModel vm2 = CreateVm();
        var compactView = new SRSReconstructorView { DataContext = vm2 };
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
            .Count(t => t.Text is not null && t.Text.StartsWith("Reconstruct a sample file from an SRS file", StringComparison.Ordinal));

    [AvaloniaFact]
    public void CompactEntry_HelpStartsCollapsed_BodyReachable_ExpanderResetsOnReentry()
    {
        SRSReconstructorViewModel vm = CreateVm();
        var view = new SRSReconstructorView { DataContext = vm };
        (Window window, Grid root) = CompactViewRig.HostAt(view, CompactInner);
        try
        {
            Expander helpDisclosure = root.GetVisualDescendants().OfType<Expander>().Single(e => e.Name == "HelpDisclosure");
            Assert.False(helpDisclosure.IsExpanded); // condition 5: compact entry starts collapsed

            helpDisclosure.IsExpanded = true;
            Dispatcher.UIThread.RunJobs();

            // "Invocable" is proven by REACHABILITY/usability (focusable, enabled, unobscured, a
            // real Tab lands on it) — this body has no interactive children (plain prose), so
            // its own compact-only-focusable ScrollViewer IS the route.
            ScrollViewer body = helpDisclosure.GetVisualDescendants().OfType<ScrollViewer>().Single();
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
            Assert.True(helpDisclosure.IsExpanded); // flat mode: force-expanded

            // The staged-focus guard's actual point: restoring from a focus captured on the
            // body (which just went non-focusable — flat mode's base style, not the
            // compact-only override) must relocate focus, not strand it. RestoreFocusTarget was
            // wired to SRSFileTextBox in the view's ctor, so that is where it must land.
            TextBox srsFileTextBox = window.GetVisualDescendants().OfType<TextBox>().Single(t => t.Name == "SRSFileTextBox");
            Assert.True(srsFileTextBox.IsFocused,
                "restoring from a focused compact body must relocate focus to the wired RestoreFocusTarget (SRSFileTextBox), not strand it");

            // The resize-driven focus-recovery target must have an accessible name (WCAG 4.1.2)
            // — same resolution technique as SampleRestorer's SRRFileTextBox and Creator's
            // OutputTextBox (the real AutomationPeer, not the raw attached property), so this
            // proves what a screen reader actually announces on landing here.
            Assert.Equal("SRS file path", ControlAutomationPeer.CreatePeerForElement(srsFileTextBox).GetName());

            window.Height -= restoreDelta;
            Dispatcher.UIThread.RunJobs();
            Assert.Contains("compactHeight", root.Classes);
            Assert.False(helpDisclosure.IsExpanded, "re-entering compact must reset Help to collapsed, not resume the prior session's open state");
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void HelpOpenDonation_ConfigRowMin80_BodyMaxHeight40_OutputTextBoxKeyboardReachable()
    {
        SRSReconstructorViewModel vm = CreateVm();
        var view = new SRSReconstructorView { DataContext = vm };
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

            TextBox outputTextBox = window.GetVisualDescendants().OfType<TextBox>().Single(t => t.Name == "OutputTextBox");
            CompactViewRig.AssertReachableByKeyboard(window, outputTextBox);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void CompactBodyScroller_NotFocusableNormally_FocusableAndNamedInCompact()
    {
        SRSReconstructorViewModel vm = CreateVm();
        var normalView = new SRSReconstructorView { DataContext = vm };
        (Window normalWindow, Grid normalRoot) = CompactViewRig.HostAt(normalView, ExpandedInner);
        try
        {
            // Flat mode force-expands the body (so this scroller IS realized/attached even
            // though the header stays hidden) — criterion F requires it NOT be a new Tab stop.
            ScrollViewer body = normalRoot.GetVisualDescendants().OfType<Expander>().Single(e => e.Name == "HelpDisclosure")
                .GetVisualDescendants().OfType<ScrollViewer>().Single();
            Assert.False(body.Focusable);
        }
        finally { normalWindow.Close(); }

        SRSReconstructorViewModel vm2 = CreateVm();
        var compactView = new SRSReconstructorView { DataContext = vm2 };
        (Window compactWindow, Grid compactRoot) = CompactViewRig.HostAt(compactView, CompactInner);
        try
        {
            Expander helpDisclosure = compactRoot.GetVisualDescendants().OfType<Expander>().Single(e => e.Name == "HelpDisclosure");
            helpDisclosure.IsExpanded = true;
            Dispatcher.UIThread.RunJobs();

            ScrollViewer body = helpDisclosure.GetVisualDescendants().OfType<ScrollViewer>().Single();
            Assert.True(body.Focusable);
            Assert.Equal("Help content", AutomationProperties.GetName(body));
        }
        finally { compactWindow.Close(); }
    }

    /// <summary>
    /// All four built-ins exercised with genuine key input against a REAL, attached
    /// ScrollViewer — never a synthetic Offset-setter poke. This view's own
    /// intro prose is short enough that it never genuinely overflows the 40-DIP donation cap at
    /// the app's own enforced minimum width, so — mirroring SRSCreatorCompactTests' own,
    /// identical finding — the body's Text is temporarily lengthened (synthetic content, this
    /// test only) so the four keys can be proven against REAL overflow; the scroller/keys are a
    /// generic mechanism (this class of ScrollViewer, not this specific prose) and
    /// <see cref="CompactBodyScroller_NotFocusableNormally_FocusableAndNamedInCompact"/> already
    /// covers the production text's own focusability/naming.
    /// </summary>
    [AvaloniaFact]
    public void CompactBodyScroller_AllFourPageKeys_MoveOffsetBothWaysAndToExtents()
    {
        SRSReconstructorViewModel vm = CreateVm();
        var view = new SRSReconstructorView { DataContext = vm };
        (Window window, Grid root) = CompactViewRig.HostAt(view, CompactInner);
        try
        {
            Expander helpDisclosure = root.GetVisualDescendants().OfType<Expander>().Single(e => e.Name == "HelpDisclosure");
            helpDisclosure.IsExpanded = true;
            Dispatcher.UIThread.RunJobs();

            ScrollViewer body = helpDisclosure.GetVisualDescendants().OfType<ScrollViewer>().Single();
            TextBlock introText = body.GetVisualDescendants().OfType<TextBlock>().Single();
            introText.Text = string.Concat(Enumerable.Repeat("Reconstruct a sample from an SRS file and the original media. ", 20));
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

    // ── 5. Pinned band (the defect this task exists to fix) ───────────

    /// <summary>
    /// Directly asserts the defect this change exists to fix: with band 1 (config)
    /// independently scrolled to its top AND its bottom extreme, BOTH the pinned Rebuild
    /// Sample button's bounds AND the result banner's bounds — translated into window
    /// coordinates — stay fully inside the window the entire time, with the result banner
    /// forced visible (the worst case for the pinned band's own height). Pre-change (today's
    /// DockPanel), the equivalent result Border collapsed to a zero-height sliver at the very
    /// bottom edge under these exact conditions — this test is verified to fail against that
    /// pre-change layout (measured: <c>resultBanner.Bounds=0, 293, 676, 0</c> at 700×319).
    /// </summary>
    [AvaloniaFact]
    public void PinnedActionBand_RebuildSampleAndResultBannerStayWithinWindow_BandOneScrolledToTopAndBottom()
    {
        SRSReconstructorViewModel vm = CreateVm();
        vm.SRSFilePath = @"C:\release\sample.srs";
        vm.MediaFilePath = @"C:\release\sample.mkv";
        vm.OutputPath = @"C:\release\sample.rebuilt.mkv";
        ForceWorstCase(vm); // forces ShowResult with a two-line summary — the pinned band's worst case
        var view = new SRSReconstructorView { DataContext = vm };
        (Window window, Grid root) = CompactViewRig.HostAt(view, CompactInner);
        try
        {
            Assert.Contains("compactHeight", root.Classes);
            Button rebuildButton = window.GetVisualDescendants().OfType<Button>().Single(b => b.Content is "Rebuild Sample");
            Border resultBanner = window.GetVisualDescendants().OfType<Border>().Single(b => b.Name == "ResultBanner");
            Assert.True(resultBanner.IsVisible);

            ScrollViewer configScroller = window.GetVisualDescendants().OfType<ScrollViewer>().Single(sv => Grid.GetRow(sv) == 1);
            Assert.True(configScroller.Extent.Height > configScroller.Viewport.Height,
                "test precondition: band 1 must genuinely overflow so top/bottom are distinct positions");

            configScroller.Offset = new Vector(0, 0);
            Dispatcher.UIThread.RunJobs();
            AssertFullyWithinWindow(rebuildButton, window);
            AssertFullyWithinWindow(resultBanner, window);

            configScroller.Offset = new Vector(0, configScroller.Extent.Height - configScroller.Viewport.Height);
            Dispatcher.UIThread.RunJobs();
            AssertFullyWithinWindow(rebuildButton, window);
            AssertFullyWithinWindow(resultBanner, window);
        }
        finally { window.Close(); }
    }

    /// <summary>
    /// A degenerate (zero-width or zero-height) control translates to a single point, which
    /// trivially satisfies any containment check — exactly the pre-change defect (the result
    /// Border collapsing to <c>Height=0</c>) would have slipped past a containment-only check.
    /// Effective visibility and a positive size are asserted FIRST, unconditionally, so a
    /// collapsed/invisible control fails outright instead of being reported as "contained".
    /// <para>
    /// CLIP-AWARE since gate finding NEW-4. This used to compare the control's two translated
    /// corners against the WINDOW'S OUTER RECTANGLE alone, which false-PASSES a control that is
    /// genuinely hidden by an intermediate <c>ClipToBounds</c> ancestor: something scrolled out of
    /// the config band's own <see cref="ScrollViewer"/> still translates to coordinates that fall
    /// numerically inside the window, so the old check reported "contained" for something the user
    /// cannot see. Criterion A/B are about what is VISIBLE, so the window rectangle was never the
    /// right region.
    /// </para>
    /// <para>
    /// The geometry is delegated to <see cref="CompactViewRig.IsFullyVisibleWithinWindow"/> rather
    /// than hand-copied. That method already owns this exact cumulative-clip walk (progressively
    /// intersect the window's bounds with every <c>ClipToBounds</c> ancestor's translated bounds,
    /// then require the control's own full rect to fit inside the result), it is already
    /// <c>internal</c>, and it is the very algorithm the other suites' local copies say they
    /// "mirror". Copying it a third and fourth time to satisfy the no-promotion rule would be
    /// duplicating a subtle geometry walk for the sake of a rule aimed at NEW abstractions — there
    /// is nothing to promote here, the shared implementation already exists. What stays local is
    /// the diagnostics: the two pre-checks above carry this view's own degenerate-control lesson
    /// and name the specific failure, which a bare bool cannot.
    /// </para>
    /// </summary>
    private static void AssertFullyWithinWindow(Control control, Window window)
    {
        Assert.True(control.IsEffectivelyVisible, $"{control.GetType().Name} is not effectively visible.");
        Assert.True(control.Bounds.Width > 0 && control.Bounds.Height > 0,
            $"{control.GetType().Name} has a non-positive size ({control.Bounds.Width:F1}x{control.Bounds.Height:F1}) — collapsed, not merely positioned badly.");

        Assert.True(CompactViewRig.IsFullyVisibleWithinWindow(control, window),
            $"{control.GetType().Name} (bounds {control.Bounds}) is not fully within the CLIP-AWARE visible region of " +
            $"the window (bounds {window.Bounds}) — it may be positioned outside the window, or hidden by an " +
            "intermediate ClipToBounds ancestor such as a ScrollViewer's own clipped viewport.");
    }

    /// <summary>
    /// The discriminating evidence for gate finding NEW-4 — see
    /// <c>SRSCreatorCompactTests</c>'s test of the same name for the full reasoning. Kept per-suite
    /// rather than shared because the false-passing control and the exact geometry are this view's
    /// own: MEASURED at 700x450 with the config band at the top, the Output row sits at y≈134 in a
    /// viewport ≈132 DIPs tall, so <c>OutputTextBox</c> is hidden behind the band's clip while
    /// every corner remains inside the window. 20 controls in this view are false-passed in that
    /// one state.
    /// </summary>
    [AvaloniaFact]
    public void ClipAwareContainment_CatchesAControlScrolledBehindTheBandClip_WhichTheWindowRectCheckMisses()
    {
        SRSReconstructorViewModel vm = CreateVm();
        var view = new SRSReconstructorView { DataContext = vm };
        (Window window, Grid root) = CompactViewRig.HostAt(view, CompactInner);
        try
        {
            Assert.Contains("compactHeight", root.Classes);
            ScrollViewer band = window.GetVisualDescendants().OfType<ScrollViewer>().Single(sv => Grid.GetRow(sv) == 1);
            Assert.True(band.Extent.Height > band.Viewport.Height,
                "test precondition: the config band must genuinely overflow, or nothing can be scrolled out of its clip");

            band.Offset = new Vector(0, 0);
            Dispatcher.UIThread.RunJobs();

            TextBox hidden = window.GetVisualDescendants().OfType<TextBox>().Single(t => t.Name == "OutputTextBox");
            Assert.True(hidden.IsEffectivelyVisible,
                "test precondition: the target must be REALIZED and effectively visible — a control hidden by IsVisible would be caught by either check and prove nothing");
            Assert.True(hidden.Bounds is { Width: > 0, Height: > 0 },
                "test precondition: the target must have a real size, so this is a clipping case and not a degenerate one");

            Assert.True(NaiveWithinWindowRectOnly(hidden, window),
                "test precondition: the OLD window-rect-only check must PASS here — if it already failed, this scenario would not " +
                "demonstrate a false pass and this covering test would be proving nothing");

            Assert.False(CompactViewRig.IsFullyVisibleWithinWindow(hidden, window),
                "the clip-aware check must REJECT a control hidden behind the config band's own clip — this is the whole of NEW-4");

            Assert.ThrowsAny<Xunit.Sdk.XunitException>(() => AssertFullyWithinWindow(hidden, window));
        }
        finally { window.Close(); }
    }

    /// <summary>
    /// The pre-NEW-4 containment check, verbatim, kept ONLY so
    /// <see cref="ClipAwareContainment_CatchesAControlScrolledBehindTheBandClip_WhichTheWindowRectCheckMisses"/>
    /// can demonstrate what it missed. Never used as an assertion by any real test.
    /// </summary>
    private static bool NaiveWithinWindowRectOnly(Control control, Window window)
    {
        Point? topLeft = control.TranslatePoint(new Point(0, 0), window);
        Point? bottomRight = control.TranslatePoint(new Point(control.Bounds.Width, control.Bounds.Height), window);
        if (topLeft is not { } tl || bottomRight is not { } br)
        {
            return false;
        }

        const double Slack = 0.5;
        Rect windowBounds = new(window.Bounds.Size);
        return tl.X >= windowBounds.X - Slack && tl.Y >= windowBounds.Y - Slack
            && br.X <= windowBounds.Right + Slack && br.Y <= windowBounds.Bottom + Slack;
    }

    // ── 6. Result cap + re-arm BINDING ──────────────────────────────────

    /// <summary>
    /// The VM half of the re-arm contract (clear-at-start, identical-repeat, cancel) is
    /// unit-tested in <c>SRSReconstructorViewModelTests</c> (run/cancel actually live there —
    /// this rig's own house rule is CompactViewRig members + VM setters only, no VM commands).
    /// This view case asserts everything that lives at the VIEW level:
    /// <list type="number">
    ///   <item>a long (300-char) ResultSummary keeps the Border's height at its normal 2-line
    ///     cap — MaxLines=2 bounds it regardless of text length, not just for short text;</item>
    ///   <item>the FULL, untrimmed text is exposed via the real automation peer's Name, the
    ///     ToolTip, and AutomationProperties.HelpText — trimming is visual-only, the same rule
    ///     as the compact tip (ReconstructorView's own <c>TextBlock.tipLine</c>);</item>
    ///   <item>the sighted-keyboard route: the Log list carries the complete, untruncated
    ///     result line (LogEntries), so a keyboard user can always read the full text even
    ///     though the visual banner clips it to 2 lines;</item>
    ///   <item>the BINDING: with the VM's ResultSummary genuinely transitioning empty->text,
    ///     the realized, ALWAYS-IN-TREE ResultStatus TextBlock (log header, LiveSetting=
    ///     Polite) follows — the announcement path that survives the visual banner's own
    ///     IsVisible race. The visual banner itself carries NO LiveSetting.</item>
    /// </list>
    /// </summary>
    [AvaloniaFact]
    public void ResultCap_LongSummary_HeightBounded_FullTextExposed_LogAndAnnouncementCarryFullText()
    {
        SRSReconstructorViewModel vm = CreateVm();
        var view = new SRSReconstructorView { DataContext = vm };
        (Window window, Grid root) = CompactViewRig.HostAt(view, CompactInner);
        try
        {
            Border resultBanner = window.GetVisualDescendants().OfType<Border>().Single(b => b.Name == "ResultBanner");
            TextBlock resultStatus = window.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Name == "ResultStatus");

            // Baseline: ResultStatus is realized (always in the tree, never IsVisible-gated)
            // and starts empty (no announcement yet) — the visual banner itself is hidden.
            Assert.False(resultBanner.IsVisible);
            Assert.True(resultStatus.IsVisible);
            Assert.Equal(string.Empty, resultStatus.Text);
            Assert.Equal(AutomationLiveSetting.Polite, AutomationProperties.GetLiveSetting(resultStatus));
            Assert.Equal(AutomationLiveSetting.Off, AutomationProperties.GetLiveSetting(resultBanner)); // visual banner carries NO LiveSetting

            // Reference cap: a short summary's OWN Border height, one line.
            vm.ResultSuccess = true;
            vm.ShowResult = true;
            vm.ResultSummary = "CRC32 match: 0ABCDEF1 (42 bytes)";
            Dispatcher.UIThread.RunJobs();
            double shortHeight = resultBanner.Bounds.Height;
            Assert.True(shortHeight > 0);

            // Re-arm to empty (VM contract already unit-tested) so the transition below is a
            // genuine empty->text one — matching the realized announcement path exactly.
            vm.ResultSummary = string.Empty;
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(string.Empty, resultStatus.Text);

            string longSummary = string.Concat(Enumerable.Repeat("Reconstruction failed verification. ", 9))[..300];
            Assert.Equal(300, longSummary.Length);
            vm.LogEntries.Add($"  Error: {longSummary}"); // the sighted-keyboard route
            vm.ResultSuccess = false;
            vm.ResultSummary = longSummary; // empty -> text transition
            Dispatcher.UIThread.RunJobs();

            // 1. Border height stays at the reference 2-line cap regardless of text length.
            Assert.True(resultBanner.Bounds.Height <= shortHeight + 20,
                $"Border height {resultBanner.Bounds.Height:F1} exceeded the ~2-line cap (short-text reference {shortHeight:F1}) with a 300-char summary");

            // 2. Full text exposed via UIA Name / ToolTip / HelpText on the Border's own
            //    TextBlock — trimming is visual-only.
            var resultText = (TextBlock)resultBanner.Child!;
            Assert.Equal(longSummary, resultText.Text);
            Assert.Equal(longSummary, ControlAutomationPeer.CreatePeerForElement(resultText).GetName());
            Assert.Equal(longSummary, ToolTip.GetTip(resultText) as string);
            Assert.Equal(longSummary, AutomationProperties.GetHelpText(resultText));
            Assert.Equal(TextTrimming.CharacterEllipsis, resultText.TextTrimming);
            Assert.Equal(2, resultText.MaxLines);

            // 3. Sighted-keyboard route: the log contains the complete result line.
            Assert.Contains(vm.LogEntries, e => e.Contains(longSummary, StringComparison.Ordinal));

            // 4. The BINDING: with ResultSummary's empty->text transition, the realized,
            //    always-in-tree ResultStatus text follows.
            Assert.Equal(longSummary, resultStatus.Text);
        }
        finally { window.Close(); }
    }

    /// <summary>
    /// ResultStatus and SaveLogStatus share the SAME log-header row via a Grid with
    /// FIXED-PROPORTION Star columns (1*,2*) rather than each being its own
    /// DockPanel.Dock="Right" item, and rather than an Auto+MaxWidth column (an earlier attempt
    /// at this same fix). Both presenters must render non-zero width, with the CHOSEN allocation
    /// mechanism (the fixed 1:2 ratio) and each one's own trim behavior asserted directly, not
    /// implied — visual-only, since each keeps its FULL text as its accessible name regardless
    /// of rendered width (screen readers read the automation peer's Name from the underlying
    /// Text property, never the trimmed glyphs — the same rule the Border's own result-cap
    /// TextBlock relies on).
    /// <para>
    /// Sequence used here — verified directly against <c>SRSReconstructorViewModel.RebuildAsync</c>'s
    /// own source (all three completion branches: success, failure, exception all agree):
    /// <c>ResultSuccess</c>, THEN <c>ResultSummary</c>, THEN <c>ShowResult</c>, with
    /// <c>SaveLogAnnouncement</c> set and settled EARLIER (a stale value from an earlier "Save
    /// log..." click — nothing in <c>RebuildAsync</c> clears it, so this is a genuinely reachable
    /// real sequence, not a contrived one). An earlier version of this test used
    /// <c>ResultSuccess -> ShowResult -> ResultSummary</c>, which does not match production;
    /// corrected here.
    /// </para>
    /// <para>
    /// IMPORTANT: this exact, real production order does NOT reproduce the original
    /// Auto+MaxWidth defect — confirmed by re-testing three separate realistic reconstructions
    /// against a restored copy of the Auto+MaxWidth layout (single synchronous tick with today's
    /// real order; the same tick split into two, rebuild-then-separately-save-log; and this
    /// method's own stale-save-log-then-rebuild order) — all three rendered BOTH presenters
    /// correctly even on the OLD layout. The original defect is real and reproducible but
    /// requires <c>ResultSummary</c> changing strictly AFTER <c>ShowResult</c> in the same tick —
    /// an ordering that does not correspond to any code path in this codebase
    /// today. This test therefore does NOT discriminate the old layout from the new one; it is a
    /// correctness/regression check against the CURRENT, real production sequence.
    /// <see cref="LogHeaderStatusLines_ResultSummaryAfterShowResult_OrderSensitiveAutoColumnFragility_StarColumnsAreImmune"/>
    /// below is the test that still provides genuine RED/GREEN discriminating coverage.
    /// </para>
    /// </summary>
    [AvaloniaFact]
    public void LogHeaderStatusLines_BothLongAndNonEmpty_BothRenderNonZeroWidth_AtCompactSize()
    {
        SRSReconstructorViewModel vm = CreateVm();
        var view = new SRSReconstructorView { DataContext = vm };
        (Window window, Grid root) = CompactViewRig.HostAt(view, CompactInner);
        try
        {
            Assert.Contains("compactHeight", root.Classes);

            TextBlock resultStatus = window.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Name == "ResultStatus");
            TextBlock saveLogStatus = window.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Name == "SaveLogStatus");

            string longResultText = string.Concat(Enumerable.Repeat("Reconstruction failed verification. ", 5));
            string longSaveLogText = string.Concat(Enumerable.Repeat("Could not save the log to the selected path. ", 5));

            // Stale SaveLogAnnouncement from an EARLIER save action, settled first -- nothing in
            // RebuildAsync clears SaveLogAnnouncement, so this genuinely can already be showing
            // when a LATER rebuild completes.
            vm.SaveLogAnnouncement = longSaveLogText;
            Dispatcher.UIThread.RunJobs();

            // The real RebuildAsync completion order.
            vm.ResultSuccess = false;
            vm.ResultSummary = longResultText;
            vm.ShowResult = true;
            Dispatcher.UIThread.RunJobs();

            Assert.Equal(longResultText, resultStatus.Text);
            Assert.Equal(longSaveLogText, saveLogStatus.Text);

            // Both presenters must render at a non-zero width when both are long at once.
            Assert.True(resultStatus.Bounds.Width > 0,
                $"ResultStatus rendered at zero width ({resultStatus.Bounds.Width:F1}) when both status lines are long");
            Assert.True(saveLogStatus.Bounds.Width > 0,
                $"SaveLogStatus rendered at zero width ({saveLogStatus.Bounds.Width:F1}) when both status lines are long");

            // The CHOSEN allocation mechanism asserted directly, not implied: fixed 1:2 Star
            // columns. Compared via the Grid's OWN column widths (raw proportions), not the two
            // TextBlocks' rendered Bounds -- each carries the SAME fixed Margin="8,0" (16 DIPs),
            // and subtracting an equal constant from two DIFFERENT-sized shares does not
            // preserve their ratio, so the RENDERED widths alone would not cleanly assert 1:2.
            var grid = (Grid)resultStatus.Parent!;
            Assert.Equal(2, grid.ColumnDefinitions.Count);
            double col0Width = grid.ColumnDefinitions[0].ActualWidth;
            double col1Width = grid.ColumnDefinitions[1].ActualWidth;
            Assert.True(Math.Abs(col1Width - col0Width * 2) <= 1.5,
                $"expected column 1 ({col1Width:F1}) to be ~2x column 0 ({col0Width:F1}) for the fixed 1*,2* split");

            // Trim behavior asserted, not implied: both must genuinely overflow their own
            // column (proving CharacterEllipsis is an exercised claim, not a dead property),
            // yet BOTH keep their full, untrimmed text as their accessible name -- visual-only
            // trimming, the same rule the Border's own result-cap TextBlock relies on.
            Assert.Equal(TextTrimming.CharacterEllipsis, resultStatus.TextTrimming);
            Assert.Equal(TextTrimming.CharacterEllipsis, saveLogStatus.TextTrimming);
            Assert.True(resultStatus.Bounds.Width < resultStatus.DesiredSize.Width,
                "test precondition: ResultStatus's long text must genuinely overflow its column for CharacterEllipsis to be a meaningful, exercised claim");
            Assert.True(saveLogStatus.Bounds.Width < saveLogStatus.DesiredSize.Width,
                "test precondition: SaveLogStatus's long text must genuinely overflow its column for CharacterEllipsis to be a meaningful, exercised claim");
            Assert.Equal(longResultText, ControlAutomationPeer.CreatePeerForElement(resultStatus).GetName());
            Assert.Equal(longSaveLogText, ControlAutomationPeer.CreatePeerForElement(saveLogStatus).GetName());
        }
        finally { window.Close(); }
    }

    /// <summary>
    /// The genuinely DISCRIMINATING covering test — reproduces the original Auto+MaxWidth defect
    /// exactly (RED against a restored copy of that layout, GREEN against the current Star-Star
    /// columns), using the SPECIFIC order that triggers it: <c>ResultSummary</c> changing
    /// strictly AFTER <c>ShowResult</c> in the same synchronous tick. Narrowed to the claim this
    /// test actually needs — this exact order is not a real <c>RebuildAsync</c>
    /// COMPLETION/SHOWING transition. Verified by grep
    /// against every assignment site in <c>SRSReconstructorViewModel.cs</c>: all three completion
    /// branches (success, failure, exception — the paths that grow ResultStatus's content while
    /// making the banner VISIBLE) always set <c>ResultSummary</c> before <c>ShowResult</c>. The
    /// constructor's and <c>RebuildAsync</c>'s own run-start RESET paths do set
    /// <c>ShowResult = false</c> before clearing <c>ResultSummary</c> — the opposite order — but
    /// that is a HIDING transition (collapsing row 2, not growing it), not the showing/growing
    /// scenario this fragility and this test concern themselves with. This test exists as
    /// deliberate hardening: it proves the Star-Star mechanism is immune to a real, reproducible
    /// Avalonia Grid Auto-column order-sensitivity regardless of property-set order, guarding
    /// against a plausible FUTURE refactor of
    /// <c>RebuildAsync</c> (or a similar view) reintroducing it.
    /// </summary>
    [AvaloniaFact]
    public void LogHeaderStatusLines_ResultSummaryAfterShowResult_OrderSensitiveAutoColumnFragility_StarColumnsAreImmune()
    {
        SRSReconstructorViewModel vm = CreateVm();
        var view = new SRSReconstructorView { DataContext = vm };
        (Window window, Grid root) = CompactViewRig.HostAt(view, CompactInner);
        try
        {
            Assert.Contains("compactHeight", root.Classes);

            TextBlock resultStatus = window.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Name == "ResultStatus");
            TextBlock saveLogStatus = window.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Name == "SaveLogStatus");

            string longResultText = string.Concat(Enumerable.Repeat("Reconstruction failed verification. ", 5));
            string longSaveLogText = string.Concat(Enumerable.Repeat("Could not save the log to the selected path. ", 5));

            // Deliberately NOT production's order (see this test's own doc) -- ResultSummary is
            // set LAST, after ShowResult, in the same synchronous tick as SaveLogAnnouncement.
            vm.ResultSuccess = false;
            vm.ShowResult = true;
            vm.SaveLogAnnouncement = longSaveLogText;
            vm.ResultSummary = longResultText;
            Dispatcher.UIThread.RunJobs();

            Assert.Equal(longResultText, resultStatus.Text);
            Assert.Equal(longSaveLogText, saveLogStatus.Text);

            Assert.True(resultStatus.Bounds.Width > 0,
                $"ResultStatus rendered at zero width ({resultStatus.Bounds.Width:F1}) under the order-sensitive trigger");
            Assert.True(saveLogStatus.Bounds.Width > 0,
                $"SaveLogStatus rendered at zero width ({saveLogStatus.Bounds.Width:F1}) under the order-sensitive trigger");
        }
        finally { window.Close(); }
    }

    // ── 7. Frame-rig parity (criterion F: normal-mode pixels unchanged) ──

    /// <summary>
    /// Same technique as SRSCreatorCompactTests' own hardened version (RenderTargetBitmap +
    /// CopyPixels, exact integer pixel size gate BEFORE any byte is read, full-buffer compare —
    /// no mask/crop/intersection). LOCAL copy of <c>AssertFullRasterPixelIdentity</c> /
    /// <c>RenderToPixelBuffer</c> (not promoted into the shared rig — promotion is an open
    /// controller decision).
    /// </summary>
    [AvaloniaFact]
    public void FrameRig_NormalMode_HeaderRegionMatchesPreDisclosureShape()
    {
        SRSReconstructorViewModel vm = CreateVm();
        var view = new SRSReconstructorView { DataContext = vm };
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

                // The intro TextBlock's own documented, intentional inset (Margin="0,0,4,0",
                // per house rule) — MEASURED, not assumed.
                double widthNarrowing = oldSize.Width - newCaptionSize.Width;
                Assert.Equal(4.0, widthNarrowing, precision: 0);

                // The hosted ROW itself (the Expander, hug-bug-fixed) is the SAME width as
                // old's own natural width — MEASURED.
                Assert.Equal(oldSize.Width, newRowSize.Width, precision: 0);

                AssertFullRasterPixelIdentity(oldRow0, oldSize, newRow0, newRowSize);
            }
            finally { oldWindow.Close(); }
        }
        finally { newWindow.Close(); }
    }

    /// <summary>
    /// Proves <see cref="AssertFullRasterPixelIdentity"/>'s size gate genuinely DISCRIMINATES —
    /// a capture-size disagreement fails loudly instead of silently shrinking to the
    /// intersection. Mirrors SRSCreatorCompactTests' own identical covering test.
    /// </summary>
    [AvaloniaFact]
    public void AssertFullRasterPixelIdentity_SubDipDriftAcrossARasterLine_FailsInsteadOfShrinkingToTheIntersection()
    {
        SRSReconstructorViewModel vm = CreateVm();
        var view = new SRSReconstructorView { DataContext = vm };
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

    /// <summary>Verbatim reconstruction of SRSReconstructorView.axaml's row-0 TextBlock before this task (git history).</summary>
    private static Window BuildPreDisclosureRow0Window()
    {
        var textBlock = new TextBlock
        {
            Text = "Reconstruct a sample file from an SRS file and the original full media file. The reconstructed sample will be CRC-verified against the expected checksum.",
            Foreground = (IBrush?)Application.Current!.FindResource("ForegroundSecondary"),
            FontSize = (double)Application.Current!.FindResource("FontSizeCaption")!,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 6),
        };

        return new Window { Width = CompactInvariantRig.InnerWidth, SizeToContent = SizeToContent.Height, Content = textBlock };
    }

    /// <summary>
    /// Renders both controls to a <see cref="RenderTargetBitmap"/> at their OWN true geometry
    /// and requires true byte-for-byte identity of the ENTIRE buffer on BOTH sides — no mask,
    /// no crop, no intersection, no offset. Local copy of SRSCreatorCompactTests' own hardened
    /// helper (raster agreement is asserted EXACTLY in integer pixels, in
    /// BOTH dimensions, BEFORE a single byte is read — a mismatch names both sizes, never a
    /// clamp — because <c>RenderTargetBitmap.Render</c> lays the visual out TO the bitmap's
    /// size, so a raster-size disagreement means two DIFFERENT layouts, not the same picture
    /// with one extra line; clamping to the intersection would compare two different
    /// renderings and report parity).
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

    // ── Fixtures (captured from real, green CompactViewRig.CaptureTabOrderControls runs
    // against the finished view, WITH Rebuild Sample enabled). Each entry is
    // CompactViewRig.Describe's own format (real
    // automation peer name plus x:Name, reported separately) — a human-readable regression net
    // (catches renames, additions, removals), NOT the discriminating check itself. The ordering
    // check itself is AssertTabWalk's OWN independent, reference-based one
    // (ResolveIndependentExpectedOrder + AssertSameControlSequence, both forward and reverse),
    // proven to discriminate by AssertSameControlSequence_SwappedPositions_FailsNamingTheMismatch
    // and AssertSameControlSequence_IdenticallyDescribedControls_AreDistinguishedByReference.
    // MediaFileTextBox and OutputTextBox previously read name="" — real a11y debt, paid in the
    // naming pass with "Media file path"/"Output path", the same "<subject> path" convention
    // SRSFileTextBox already used, each subject taken from that row's own visible caption. The
    // three "Browse" buttons were left bare in that pass and are now named as well
    // ("Browse for SRS file"/"…media file"/"…output path"), so NO entry in these fixtures
    // describes identically to another any more — which is why the covering test's
    // identically-described half moved to a constructed pair. ──

    /// <summary>
    /// Normal mode, starting at SRS File's own Browse button — PROVEN first (not presumed): the
    /// reverse walk anchored at the tail end (Save log) retraces this exact sequence backwards
    /// and lands back on this same Browse button, empirically confirming nothing precedes it.
    /// From there: SRS File's Browse + its TextBox, Media File's Browse + its TextBox, Output's
    /// Browse + its TextBox, Rebuild Sample (SRS/Media/Output set so it is genuinely enabled and
    /// its own position is pinned — CanExecute false for the default inert VM would otherwise
    /// leave it absent and unverified), then Save log.
    /// </summary>
    private static readonly IReadOnlyList<string> NormalModeTabOrderFixture =
    [
        "TextBox name=\"SRS file path\" id=\"SRSFileTextBox\"",
        "Button name=\"Browse for SRS file\" id=\"\"",
        "TextBox name=\"Media file path\" id=\"MediaFileTextBox\"",
        "Button name=\"Browse for media file\" id=\"\"",
        "TextBox name=\"Output path\" id=\"OutputTextBox\"",
        "Button name=\"Browse for output path\" id=\"\"",
        "Button name=\"Rebuild Sample\" id=\"\"",
        "Button name=\"Save log...\" id=\"\"",
    ];

    /// <summary>
    /// Compact order: disclosure header toggle → (body skipped: Help starts collapsed
    /// per condition 5, so the plain-prose body is IsVisible=false and correctly excluded from
    /// Tab order) → identical tail to normal mode (this walk starts one stop earlier, at the
    /// header toggle, rather than at SRS File's Browse button — likewise PROVEN first here by
    /// its own reverse walk landing back on the toggle).
    /// </summary>
    private static readonly IReadOnlyList<string> CompactModeTabOrderFixture =
    [
        "ToggleButton name=\"Help\" id=\"\"",
        "TextBox name=\"SRS file path\" id=\"SRSFileTextBox\"",
        "Button name=\"Browse for SRS file\" id=\"\"",
        "TextBox name=\"Media file path\" id=\"MediaFileTextBox\"",
        "Button name=\"Browse for media file\" id=\"\"",
        "TextBox name=\"Output path\" id=\"OutputTextBox\"",
        "Button name=\"Browse for output path\" id=\"\"",
        "Button name=\"Rebuild Sample\" id=\"\"",
        "Button name=\"Save log...\" id=\"\"",
    ];
}
