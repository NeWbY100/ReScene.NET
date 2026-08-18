using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Automation.Peers;
using Avalonia.Controls;
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
/// Small-window layout degradation tests for <see cref="SRSCreatorView"/> (switch height DERIVED
/// from the view's own measured expanded floor — see <see cref="Threshold"/> — config
/// row AutoToStar 110 compact / 80 help-open, log 80, Help body MaxHeight 40, compact CI bound
/// <see cref="CompactInvariantRig.CiBound"/> == 307, pinned band ceiling 75). Adapts
/// <c>ReconstructorCompactTests</c>' five-part shape to this view's simpler, sub-tab-free
/// three-band structure: no splitter section (this view has none), and a NEW pinned-band section
/// (#5) asserting the actual defect this change fixes directly — the Create SRS button's bounds
/// while band 1 is scrolled to both extremes.
/// </summary>
public class SRSCreatorCompactTests
{
    // ── Inert VM construction (mirrors SRSCreatorViewTests.CreateViewModel) ──

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

    private static SRSCreatorViewModel CreateVm() =>
        new(
            new InertSrsCreationService(),
            new AvaloniaFileDialogService(static () => null), // headless: dialogs no-op, never block
            new InertTempDirectoryService(),
            new DefaultAppSettingsService(),
            new InlineUiDispatcher());

    private static SRSCreatorView BuildWorstCase()
    {
        SRSCreatorViewModel vm = CreateVm();
        ForceWorstCase(vm);
        return new SRSCreatorView { DataContext = vm };
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
    /// The worst-case layout, forced together: ISO selection visible, all three FieldStatusLines
    /// non-None (with realistic, wrapping-length messages — FieldStatusLine's message TextBlock
    /// wraps, so a short message would understate the floor), and Cancel + ProgressMessage +
    /// ProgressBar all visible. Used by every invariant/no-clip check so "worst case" means the
    /// same thing everywhere it is asserted.
    /// </summary>
    private static void ForceWorstCase(SRSCreatorViewModel vm)
    {
        vm.IsISOSource = true;
        vm.SampleStatus = FieldStatus.Warning("This looks like a very small sample — check it is not truncated before continuing.");
        vm.MainFileStatus = FieldStatus.Warning("This file doesn't exist — match offsets will stay 0.");
        vm.OutputStatus = FieldStatus.Info("Auto-filled from the sample name. Change it if needed.");
        vm.IsCreating = true;
        vm.ShowProgress = true;
        vm.ProgressMessage = "Profiling sample...";
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
        CompactInvariantRig.AssertActiveModeFitsAroundSwitchPoint("SRSCreator", BuildWorstCase);


    [AvaloniaFact]
    public void Invariant_CompactFloor_WithinCiBound_AndPinnedBandRowSane()
    {
        SRSCreatorViewModel vm = CreateVm();
        ForceWorstCase(vm);
        var view = new SRSCreatorView { DataContext = vm };

        (Window window, Grid root) = CompactViewRig.HostAt(view, CompactInner);
        try
        {
            Assert.Contains("compactHeight", root.Classes);

            // One sum: donation row applied (config row min -> 80) AND the body's own MaxHeight
            // (40) both spend the same 307-DIP budget — never checked independently.
            double floor = CompactInvariantRig.MeasureFloor(root);
            Assert.True(floor <= CompactInvariantRig.CiBound,
                $"compact floor {floor:F1} must be <= {CompactInvariantRig.CiBound}");

            // 4. Pinned band (row 2) is never the budget donor — its natural height stays small
            // and positive regardless of mode, and within CompactInvariantRig.PinnedBandCeiling even with
            // Cancel + ProgressMessage + ProgressBar all forced visible (ForceWorstCase).
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
        SRSCreatorViewModel vm = CreateVm();
        ForceWorstCase(vm); // criterion B worst case: every conditional forced
        var view = new SRSCreatorView { DataContext = vm };

        (Window window, Grid root) = CompactViewRig.HostAt(view, innerHeight);
        try
        {
            Assert.Equal(expectCompact, root.Classes.Contains("compactHeight"));
            CompactInvariantRig.AssertArrangesWithin(root, root.Bounds.Height);
        }
        finally { window.Close(); }
    }

    /// <summary>
    /// Criterion A for the LAST config control (App name TextBox — no x:Name of its own, so
    /// distinguished by its Width="400", the only TextBox in the view with that width) and the
    /// primary action (Create SRS button, content-matched like the existing
    /// <c>SRSCreatorViewTests</c> suite already does for Cancel). Both routed through the config
    /// band's own ScrollViewer, identified by Grid.Row rather than by uniqueness-among-
    /// ScrollViewers — the Help body is ALSO a bare, non-templated ScrollViewer, so Grid.Row is
    /// the only unambiguous handle.
    /// <para>
    /// Input/Output paths are set so <c>CanCreateSRS()</c> is true and the button is genuinely
    /// enabled — for the DEFAULT inert VM (both paths empty) Create SRS is disabled and Avalonia
    /// correctly excludes it from Tab order entirely (same precedent as the Reconstructor's own
    /// "Start" button, which its own fixture comment documents as absent for the same reason);
    /// "reachable by keyboard" is only a meaningful check once the button can actually take
    /// focus.
    /// </para>
    /// </summary>
    private static void AssertConfigAndActionReachable(double innerHeight)
    {
        SRSCreatorViewModel vm = CreateVm();
        vm.InputPath = @"C:\release\sample.mkv";
        vm.OutputPath = @"C:\release\sample.srs";
        var view = new SRSCreatorView { DataContext = vm };
        (Window window, Grid root) = CompactViewRig.HostAt(view, innerHeight);
        try
        {
            ScrollViewer configScroller = window.GetVisualDescendants().OfType<ScrollViewer>().Single(sv => Grid.GetRow(sv) == 1);

            TextBox appName = window.GetVisualDescendants().OfType<TextBox>().Single(t => t.Width == 400);
            AssertReachableByAllThreeRoutes(window, configScroller, appName);

            Button createButton = window.GetVisualDescendants().OfType<Button>().Single(b => b.Content is "Create SRS");
            Assert.True(createButton.IsEffectivelyEnabled, "test precondition: Create SRS must be enabled to be a meaningful keyboard-reachability target");
            AssertReachableByAllThreeRoutes(window, configScroller, createButton);
        }
        finally { window.Close(); }
    }

    /// <summary>
    /// Each route is exercised from a genuine "not yet visible" start (offset reset between
    /// routes) — otherwise the first route scrolling the target into view would make the other
    /// two trivially no-op without ever exercising their own mechanism. Harmless no-op for the
    /// pinned Create SRS button (never inside <paramref name="scroller"/>'s clipped-out region,
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
    /// A previous version of this reverse check derived its expectation FROM the forward walk's
    /// own observation (<c>forwardOrder.Reverse()</c>) — a self-referential, non-discriminating
    /// oracle. This view has THREE identically-described
    /// "Browse" buttons and TWO identically-described unnamed TextBoxes (MainFilePath, AppName);
    /// if any two same-described siblings were genuinely swapped in the tree, the forward walk
    /// would observe them in the swapped order, the derived reverse expectation would inherit
    /// that SAME swap, and the reverse walk (which also observes the same swapped tree) would
    /// match it — a real regression would pass. Fixed by resolving an INDEPENDENT ground-truth
    /// order up front (<see cref="ResolveIndependentExpectedOrder"/>, one unique identifier per
    /// stop — bound command for Buttons, x:Name or a distinguishing attribute for TextBoxes —
    /// never the walk's own output) and checking BOTH the forward walk and the reverse walk
    /// against THAT SAME list (forward as-is; reverse as its independent reversal) — proven to
    /// genuinely discriminate by
    /// <see cref="AssertSameControlSequence_IdenticallyDescribedControls_AreDistinguishedByReference"/>
    /// below.
    /// <para>
    /// Also adopts the now-hardened <see cref="CompactViewRig"/> idioms directly: a forward walk
    /// with a completeness check (so an unreached control, including one that would only be
    /// reachable BEFORE the presumed sentinel, fails loudly rather than being silently absorbed),
    /// plus a REVERSE walk anchored at the forward walk's own LAST stop (the unambiguous
    /// "boundary" — the log's Save button, not a presumed starting point) that must retrace the
    /// ENTIRE forward order and land back on the forward walk's FIRST stop — the actual,
    /// empirical proof that the presumed-first control really is first. SRSCreatorView is a
    /// single keyboard-navigation scope (no nested TabControl like Reconstructor's), so this is
    /// one forward walk plus one reverse walk — no per-scope machinery.
    /// </para>
    /// </summary>
    private static void AssertTabWalk(double innerHeight)
    {
        SRSCreatorViewModel vm = CreateVm();
        vm.InputPath = @"C:\release\sample.mkv";
        vm.OutputPath = @"C:\release\sample.srs"; // Create SRS enabled: its own position is pinned, not left unverified
        var view = new SRSCreatorView { DataContext = vm };
        (Window window, Grid root) = CompactViewRig.HostAt(view, innerHeight);
        try
        {
            bool compact = root.Classes.Contains("compactHeight");

            // Independent ground truth, resolved BEFORE any walk runs — never derived from a
            // walk's own output. In compact mode Help starts collapsed (condition 5): the body's
            // own prose is not a tab stop while collapsed, so the header toggle is the walk's
            // genuine entry point. In expanded/flat mode the disclosure contributes NOTHING to
            // tab order at all (header hidden by style, body plain non-focusable prose) — Sample
            // File's own Browse button is the first stop there, PROVEN (not merely presumed) by
            // the reverse walk's own boundary-landing assertion below.
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
            // puts a "_File" MenuItem right after the TabControl in Z-order, matching
            // Reconstructor's own identical finding against the same shared shell. Confirmed by
            // a real run: FirstExternalTarget's own Describe is `MenuItem name="File" id=""`
            // (the accessible name strips the access-key underscore; matched here against the
            // raw Header property, "_File", which is what BuildShell actually declares).
            MenuItem expectedExternalBoundary = window.GetVisualDescendants().OfType<MenuItem>()
                .Single(m => m.Header as string == "_File");
            Assert.True(forwardCapture.FirstExternalTarget is not null,
                "forward capture should have left root's scope onto an external control, not ended via a stable loop within root");
            Assert.True(ReferenceEquals(expectedExternalBoundary, forwardCapture.FirstExternalTarget),
                $"forward capture's terminal external target should be {CompactViewRig.Describe(expectedExternalBoundary)}, " +
                $"not {CompactViewRig.Describe(forwardCapture.FirstExternalTarget)} — same description does not mean same control instance.");

            // REVERSE: anchored at the forward walk's own LAST stop (the unambiguous boundary),
            // never a presumed starting point. Checked against the INDEPENDENT order's own
            // reversal — NOT forwardOrder.Reverse() — so a genuine
            // same-described-sibling swap cannot hide behind a self-referential oracle. Confirmed
            // by a real run: a single scope means the reverse walk genuinely retraces the whole
            // independent order and lands back on its first stop — the actual, empirical proof
            // that the presumed forward sentinel is genuinely first, not an assumption.
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
    /// attribute for TextBoxes), NEVER by re-deriving from a walk's own observed output. This is
    /// what makes <see cref="AssertTabWalk"/>'s forward/reverse checks genuinely discriminating
    /// against a same-described-sibling swap — proven directly by
    /// <see cref="AssertSameControlSequence_SwappedPositions_FailsNamingTheMismatch"/> and
    /// <see cref="AssertSameControlSequence_IdenticallyDescribedControls_AreDistinguishedByReference"/>.
    /// (This view once supplied the motivating collision itself — three bare "Browse" buttons and
    /// two unnamed TextBoxes — and no longer does: every one of them is named. Resolving by
    /// identity here is the house rule applied uniformly, not a workaround for a collision that
    /// still exists.)
    /// </summary>
    private static List<Control> ResolveIndependentExpectedOrder(Window window, SRSCreatorViewModel vm, bool compact)
    {
        Button sampleBrowse = window.GetVisualDescendants().OfType<Button>().Single(b => ReferenceEquals(b.Command, vm.BrowseInputCommand));
        TextBox inputTextBox = window.GetVisualDescendants().OfType<TextBox>().Single(t => t.Name == "InputTextBox");
        Button mainBrowse = window.GetVisualDescendants().OfType<Button>().Single(b => ReferenceEquals(b.Command, vm.BrowseMainFileCommand));
        Button mainClear = window.GetVisualDescendants().OfType<Button>().Single(b => ReferenceEquals(b.Command, vm.ClearMainFileCommand));
        TextBox appName = window.GetVisualDescendants().OfType<TextBox>().Single(t => t.Width == 400);
        TextBox mainFilePath = window.GetVisualDescendants().OfType<TextBox>().Single(t => t.Name is null && !ReferenceEquals(t, appName));
        Button outputBrowse = window.GetVisualDescendants().OfType<Button>().Single(b => ReferenceEquals(b.Command, vm.BrowseOutputCommand));
        TextBox outputTextBox = window.GetVisualDescendants().OfType<TextBox>().Single(t => t.Name == "OutputTextBox");
        Button createSrs = window.GetVisualDescendants().OfType<Button>().Single(b => ReferenceEquals(b.Command, vm.CreateSRSCommand));
        Button saveLog = window.GetVisualDescendants().OfType<Button>().Single(b => ReferenceEquals(b.Command, vm.SaveLogCommand));

        // Field THEN button(s), per row — see SampleRestorerCompactTests' own note for the reversed
        // markup order these pins correct. The main-file row has TWO right-docked buttons, so it
        // renders field, Clear, Browse and its markup declares them in exactly the opposite order.
        // Before that fix this list read button-then-field.
        List<Control> order = [inputTextBox, sampleBrowse, mainFilePath, mainClear, mainBrowse, outputTextBox, outputBrowse, appName, createSrs, saveLog];

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
    /// own forward/reverse checks, which rely on it — is genuinely sensitive to a PERMUTATION, not
    /// just to controls going missing. Captures the REAL forward walk against the real, independent
    /// expected order, then deliberately swaps two adjacent positions WITHIN THAT INDEPENDENT
    /// EXPECTATION (never within the observed walk) — simulating a regression that reordered them
    /// in the tree, which a forward-derived reverse oracle could never catch. Asserts
    /// the mismatch fails, naming the specific position, then confirms the UNTAMPERED
    /// expectation still passes against the same real walk (the failure above was caused by the
    /// tampering, not a real defect).
    /// <para>
    /// REDESIGNED — same split, and the same reasoning, as
    /// <c>ReconstructorCompactTests</c>' §C5 precedent. This used to swap two of the three
    /// identically-described "Browse" buttons, which is what made it a proof about REFERENCE
    /// versus DESCRIPTION comparison rather than merely about ordering. Naming those three removed
    /// the last identically-described pair from this view — checked, not assumed: every stop in the
    /// measured walk now carries a distinct accessible name or x:Name. Rather than re-point the
    /// swap at a pair that is not genuinely indistinguishable (which would hollow the test out
    /// while it kept passing), the two claims are split: this test keeps the real-walk grounding
    /// and asserts positional sensitivity, and
    /// <see cref="AssertSameControlSequence_IdenticallyDescribedControls_AreDistinguishedByReference"/>
    /// carries the reference-versus-description claim against a constructed pair.
    /// </para>
    /// <para>
    /// SampleRestorer went the other way and kept a real pair, because it still has one (its grid's
    /// per-row checkboxes). The difference is deliberate: a real pair is better evidence when one
    /// exists, and a constructed one is honest when it does not.
    /// </para>
    /// </summary>
    [AvaloniaFact]
    public void AssertSameControlSequence_SwappedPositions_FailsNamingTheMismatch()
    {
        SRSCreatorViewModel vm = CreateVm();
        vm.InputPath = @"C:\release\sample.mkv";
        vm.OutputPath = @"C:\release\sample.srs";
        var view = new SRSCreatorView { DataContext = vm };
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
    /// longer contains one.
    /// <para>
    /// Kept PER-SUITE rather than shared with the Reconstructor's identical test, and the reason is
    /// not symmetry: <see cref="AssertSameControlSequence"/> is a private helper duplicated in each
    /// suite, so the claim "this suite's ordering check compares by reference" is a per-suite claim
    /// about a per-suite method. One shared test would prove it for whichever copy it happened to
    /// call, leaving the others free to drift to a description comparison undetected. Promoting the
    /// helper into the shared rig would make one test correct, and is a five-suite refactor that
    /// belongs in its own change.
    /// </para>
    /// <para>
    /// Both halves are stated rather than assumed: first that a description comparison genuinely
    /// PASSES on the swapped sequence, then that the reference comparison FAILS on the same swap.
    /// </para>
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
    /// merely the same DESCRIPTIONS. A description-based <c>Assert.Equal</c> cannot distinguish
    /// a permutation of controls that all describe identically; this can, since it never converts
    /// either side to a string until it already knows a mismatch exists and needs to report it.
    /// This view's three "Browse" buttons were the motivating example until they were named;
    /// the property is now proven against a constructed pair by
    /// <see cref="AssertSameControlSequence_IdenticallyDescribedControls_AreDistinguishedByReference"/>.
    /// Mirrors <c>ReconstructorCompactTests</c>' own helper of the same shape.
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
    // points (rather than deleted as pure duplicates) so "the tab order is exactly this" reads
    // as its own explicit, discoverable assertion — not merely a side effect of a criterion-C
    // reachability test.

    [AvaloniaFact]
    public void TabOrderSnapshot_Normal_MatchesPreChangeFixture() => AssertTabWalk(ExpandedInner);

    [AvaloniaFact]
    public void TabOrderSnapshot_Compact_MatchesSpecSection2Order() => AssertTabWalk(CompactInner);

    // ── 4. Chrome ─────────────────────────────────────────────────────

    [AvaloniaFact]
    public void SingleIntroInstance_ExistsInBothModes()
    {
        SRSCreatorViewModel vm = CreateVm();
        var normalView = new SRSCreatorView { DataContext = vm };
        (Window normalWindow, Grid normalRoot) = CompactViewRig.HostAt(normalView, ExpandedInner);
        try
        {
            Assert.DoesNotContain("compactHeight", normalRoot.Classes);
            Assert.Equal(1, CountIntroInstances(normalWindow));
        }
        finally { normalWindow.Close(); }

        SRSCreatorViewModel vm2 = CreateVm();
        var compactView = new SRSCreatorView { DataContext = vm2 };
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
            .Count(t => t.Text is not null && t.Text.StartsWith("Create an SRS (Sample Rescue Storage)", StringComparison.Ordinal));

    [AvaloniaFact]
    public void CompactEntry_HelpBodyIsReachable_AndRestoringRelocatesFocus()
    {
        SRSCreatorViewModel vm = CreateVm();
        var view = new SRSCreatorView { DataContext = vm };
        (Window window, Grid root) = CompactViewRig.HostAt(view, CompactInner);
        try
        {
            // "Invocable" is proven by REACHABILITY/usability (focusable, enabled, unobscured, a
            // real Tab lands on it) — this body has no interactive children (plain prose), so
            // its own compact-only-focusable ScrollViewer IS the route.
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

            // The staged-focus guard's actual point: restoring from a focus captured on the
            // body (which just went non-focusable — flat mode's base style, not the
            // compact-only override) must relocate focus, not strand it. RestoreFocusTarget was
            // wired to InputTextBox in the view's ctor, so that is where it must land.
            TextBox inputTextBox = window.GetVisualDescendants().OfType<TextBox>().Single(t => t.Name == "InputTextBox");
            Assert.True(inputTextBox.IsFocused,
                "restoring from a focused compact body must relocate focus to the wired RestoreFocusTarget (InputTextBox), not strand it");

            // The resize-driven focus-recovery target must have an accessible name (WCAG 4.1.2)
            // — same resolution technique as SampleRestorer's SRRFileTextBox and Creator's
            // OutputTextBox (the real AutomationPeer, not the raw attached property), so this
            // proves what a screen reader actually announces on landing here.
            Assert.Equal("Sample file path", ControlAutomationPeer.CreatePeerForElement(inputTextBox).GetName());

            window.Height -= restoreDelta;
            Dispatcher.UIThread.RunJobs();
            Assert.Contains("compactHeight", root.Classes);
            Assert.True(body.Focusable, "re-entering compact restores the Help body's keyboard route");
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void HelpOpenDonation_ConfigRowMin80_BodyMaxHeight40_AppNameKeyboardReachable()
    {
        SRSCreatorViewModel vm = CreateVm();
        var view = new SRSCreatorView { DataContext = vm };
        (Window window, Grid root) = CompactViewRig.HostAt(view, CompactInner);
        try
        {

            int configRow = Grid.GetRow(window.GetVisualDescendants().OfType<ScrollViewer>().Single(sv => Grid.GetRow(sv) == 1));
            Assert.Equal(80, root.RowDefinitions[configRow].MinHeight);

            ScrollViewer body = root.GetVisualDescendants().OfType<ScrollViewer>().Single(s => s.Name == "HelpBody");
            Assert.Equal(40, body.MaxHeight);

            TextBox appName = window.GetVisualDescendants().OfType<TextBox>().Single(t => t.Width == 400);
            CompactViewRig.AssertReachableByKeyboard(window, appName);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void CompactBodyScroller_NotFocusableNormally_FocusableAndNamedInCompact()
    {
        SRSCreatorViewModel vm = CreateVm();
        var normalView = new SRSCreatorView { DataContext = vm };
        (Window normalWindow, Grid normalRoot) = CompactViewRig.HostAt(normalView, ExpandedInner);
        try
        {
            // Flat mode force-expands the body (so this scroller IS realized/attached even
            // though the header stays hidden) — criterion F requires it NOT be a new Tab stop.
            ScrollViewer body = normalRoot.GetVisualDescendants().OfType<ScrollViewer>()
                .Single(sv => sv.Name == "HelpBody");
            Assert.False(body.Focusable);
        }
        finally { normalWindow.Close(); }

        SRSCreatorViewModel vm2 = CreateVm();
        var compactView = new SRSCreatorView { DataContext = vm2 };
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
    /// Avalonia's ScrollViewer handles PAGE keys, not arrows. All four built-ins exercised with
    /// genuine key input against a REAL, attached ScrollViewer — never a synthetic Offset-setter
    /// poke.
    /// <para>
    /// MEASURED: this view's actual intro prose (172 characters) renders at ~35 DIPs at the
    /// app's own enforced minimum width (<c>MainWindow.MinWidth="700"</c>, confirmed in
    /// MainWindow.axaml) — under the 40-DIP HelpBodyMaxHeight donation cap, so it never
    /// genuinely overflows and there is nothing for the real production text to page through at
    /// any window size the app allows. The body's own Text is therefore temporarily lengthened
    /// (synthetic content, this test only) so the four keys can be proven against REAL overflow;
    /// the scroller/keys are a generic mechanism (this class of ScrollViewer, not this specific
    /// prose) and <see cref="CompactBodyScroller_NotFocusableNormally_FocusableAndNamedInCompact"/>
    /// already covers the production text's own focusability/naming.
    /// </para>
    /// </summary>
    [AvaloniaFact]
    public void CompactBodyScroller_AllFourPageKeys_MoveOffsetBothWaysAndToExtents()
    {
        SRSCreatorViewModel vm = CreateVm();
        var view = new SRSCreatorView { DataContext = vm };
        (Window window, Grid root) = CompactViewRig.HostAt(view, CompactInner);
        try
        {
            Dispatcher.UIThread.RunJobs();

            ScrollViewer body = root.GetVisualDescendants().OfType<ScrollViewer>().Single(s => s.Name == "HelpBody");
            TextBlock introText = body.GetVisualDescendants().OfType<TextBlock>().Single();
            introText.Text = string.Concat(Enumerable.Repeat("Create an SRS from a sample video file. ", 20));
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
    /// independently scrolled to its top AND its bottom extreme, the pinned Create SRS button's
    /// bounds — translated into window coordinates — stay fully inside the window the entire
    /// time, with Cancel/ProgressMessage/ProgressBar all forced visible (the worst case for the
    /// pinned band's own height). Pre-change (today's DockPanel), the equivalent button
    /// collapsed to a zero-height sliver at the very bottom edge under these exact conditions —
    /// this test is verified to fail against that pre-change layout.
    /// </summary>
    [AvaloniaFact]
    public void PinnedActionBand_CreateSRSButtonStaysWithinWindow_BandOneScrolledToTopAndBottom()
    {
        SRSCreatorViewModel vm = CreateVm();
        vm.IsCreating = true;
        vm.ShowProgress = true; // forces Cancel + ProgressMessage + ProgressBar visible
        var view = new SRSCreatorView { DataContext = vm };
        (Window window, Grid root) = CompactViewRig.HostAt(view, CompactInner);
        try
        {
            Assert.Contains("compactHeight", root.Classes);
            Button createButton = window.GetVisualDescendants().OfType<Button>().Single(b => b.Content is "Create SRS");
            Button cancelButton = window.GetVisualDescendants().OfType<Button>().Single(b => b.Content is "Cancel");
            ProgressBar bar = window.GetVisualDescendants().OfType<ProgressBar>().Single();
            Assert.True(cancelButton.IsVisible);
            Assert.True(bar.IsVisible);

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

    /// <summary>
    /// A degenerate (zero-width or zero-height) control translates to a single point, which
    /// trivially satisfies any containment check — exactly the pre-change defect (the Create SRS
    /// button collapsing to <c>Height=0</c>) would have slipped past a containment-only check.
    /// Effective visibility and a positive size are asserted FIRST, unconditionally, so a
    /// collapsed/invisible control fails outright instead of being reported as "contained".
    /// <para>
    /// CLIP-AWARE since gate finding NEW-4 — see
    /// <c>SRSReconstructorCompactTests.AssertFullyWithinWindow</c>'s own doc for the full reasoning
    /// (the window's outer rectangle is not the visible region; the geometry is delegated to
    /// <see cref="CompactViewRig.IsFullyVisibleWithinWindow"/>, which already owns the cumulative
    /// clip walk, rather than hand-copied a third time).
    /// </para>
    /// <para>
    /// This view is the one where the distinction bites hardest:
    /// <see cref="PinnedActionBand_CreateSRSButtonStaysWithinWindow_BandOneScrolledToTopAndBottom"/>
    /// deliberately scrolls the config band to both extremes, which is exactly the manoeuvre that
    /// moves content behind a ScrollViewer's clip while leaving its window-space coordinates inside
    /// the window.
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
    /// The discriminating evidence for gate finding NEW-4, kept as a committed test rather than a
    /// throwaway diagnostic: the window-rect-only check this suite used to carry genuinely
    /// FALSE-PASSES a control the user cannot see, and the clip-aware form genuinely catches it.
    /// <para>
    /// The scenario is not contrived — it is the shipped compact layout. The config band's
    /// ScrollViewer is row 1; the pinned action band and the log occupy rows 2 and 3 BELOW it. So
    /// content scrolled past the band's own bottom edge lands in window space that is still inside
    /// the window, behind the pinned rows. MEASURED at 700x450 with the band at the top: the
    /// Output row sits at y≈155 in a viewport only ≈127 DIPs tall, so <c>OutputTextBox</c> is
    /// entirely hidden while every one of its corners is comfortably inside the window rectangle.
    /// It is one of 41 controls in this view alone that the old check reported as "contained" in
    /// that single state.
    /// </para>
    /// <para>
    /// Both halves are asserted, because either alone proves nothing: that the OLD form passes (so
    /// this really is a false pass, not just a control that happens to be out of bounds), and that
    /// the NEW form fails. The old form is reproduced inline rather than described — a comment
    /// claiming what the deleted code did is not checkable.
    /// </para>
    /// </summary>
    [AvaloniaFact]
    public void ClipAwareContainment_CatchesAControlScrolledBehindTheBandClip_WhichTheWindowRectCheckMisses()
    {
        SRSCreatorViewModel vm = CreateVm();
        var view = new SRSCreatorView { DataContext = vm };
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

    // ── 6. Frame-rig parity (criterion F: normal-mode pixels unchanged) ──

    /// <summary>
    /// RECAPTURED after the hug-bug fix (HorizontalAlignment/HorizontalContentAlignment=
    /// "Stretch" on the Expander — see the view's own XAML comment). Two figures, kept distinct:
    /// (1) the intro TextBlock's OWN inset — measured from the TextBlock itself, 4 DIPs, the
    /// documented "per house rule" margin; (2) the PIXEL comparison, which captures the FULL
    /// HOSTED ROW (<c>newRow0</c>, the Expander) rather than the narrower TextBlock: comparing
    /// only the cropped-to-672 inner TextBlock would silently exclude the 4-DIP trailing strip
    /// INSIDE the Expander's own bounds from any scrutiny — full-row reference parity would
    /// remain unproven. With the hug-bug fixed, <c>newRow0</c> itself is the
    /// SAME 676 width as old's own natural width (asserted below, not assumed), so the full row —
    /// including that trailing strip — is genuinely compared byte-for-byte, no width-based crop.
    /// Uses <c>RenderTargetBitmap</c> + <c>CopyPixels</c>, the same technique as Reconstructor's
    /// own hardened version and HexViewControlTests — geometry alone cannot catch a shifted
    /// glyph, a recolored brush, or a stray border inside the surviving region.
    /// <para>
    /// The last exclusion pathway was removed entirely rather than merely left unused: there is
    /// no mask parameter any more, no crop and no intersection, and the two captures must
    /// rasterise to the SAME integer pixel size or the test fails naming both
    /// (<see cref="AssertFullRasterPixelIdentity"/>). MEASURED: both sides lay out at exactly
    /// 676.000000 x 35.000000 DIPs, so both rasterise to 676x35 — 94,640 bytes each, all of them
    /// compared. MEASURED and unchanged by that tightening:
    /// unlike Reconstructor's own longer text (no WrapPanel/links row exists below this view's
    /// intro to also check), this view's shorter intro does NOT push any word across a line-break
    /// boundary at the narrower 672-DIP TextBlock measure — the whole buffer matches byte for
    /// byte. A future content change that genuinely reflows must be fixed or escalated, not
    /// masked back out.
    /// </para>
    /// </summary>
    [AvaloniaFact]
    public void FrameRig_NormalMode_HeaderRegionMatchesPreDisclosureShape()
    {
        SRSCreatorViewModel vm = CreateVm();
        var view = new SRSCreatorView { DataContext = vm };
        (Window newWindow, Grid newRoot) = CompactViewRig.HostAt(view, ExpandedInner);
        try
        {
            Assert.DoesNotContain("compactHeight", newRoot.Classes);
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();

            Control newRow0 = newRoot.Children.OfType<Control>().Single(c => Grid.GetRow(c) == 0);
            Size newRowSize = newRow0.Bounds.Size;

            // The intro TextBlock's own inset is still measured separately (for the documented
            // 4-DIP figure below), but the PIXEL comparison captures the FULL HOSTED ROW
            // (newRow0, the Expander) — not the cropped-to-672 descendant TextBlock. Capturing
            // only the inner TextBlock would silently exclude the 4-DIP trailing strip inside
            // the Expander's own bounds from ANY scrutiny — that strip IS part of what a user
            // sees, and a real defect confined to it (a stray border, wrong background) would
            // never be caught by comparing only the narrower inner control.
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

                // Height must match exactly — the visually significant dimension (a
                // taller/shorter header block would shift every row below it). Confirmed exact:
                // nothing about the width narrowing below causes the TextBlock to wrap onto an
                // extra line.
                Assert.Equal(oldSize.Height, newRowSize.Height, precision: 0);

                // The intro TextBlock's own documented, intentional inset (Margin="0,0,4,0",
                // "per house rule") — MEASURED, not the pre-hug-bug-fix figure this test
                // originally carried (which conflated the inset with the hug bug itself).
                double widthNarrowing = oldSize.Width - newCaptionSize.Width;
                Assert.Equal(4.0, widthNarrowing, precision: 0);

                // The hosted ROW itself (the Expander, post-hug-bug-fix) is now the SAME 676
                // width as old's own natural width — MEASURED, not assumed: this is exactly what
                // the earlier "0.0 narrowing when comparing newRow0 directly" measurement already
                // established. This is the readable DIP-level statement of that claim; it is NOT
                // what licenses the pixel comparison below (a rounded-DIP equality can hide a
                // whole-raster-column disagreement). AssertFullRasterPixelIdentity re-derives and
                // enforces agreement itself, exactly, in integer pixels.
                Assert.Equal(oldSize.Width, newRowSize.Width, precision: 0);

                AssertFullRasterPixelIdentity(oldRow0, oldSize, newRow0, newRowSize);
            }
            finally { oldWindow.Close(); }
        }
        finally { newWindow.Close(); }
    }

    /// <summary>
    /// Proves <see cref="AssertFullRasterPixelIdentity"/>'s size gate genuinely DISCRIMINATES —
    /// that a capture-size disagreement now fails loudly instead of silently shrinking the
    /// compared region to the intersection, which could otherwise go unnoticed ("rounded width
    /// equality plus <c>Floor(Math.Min(...))</c> can silently omit a terminal raster column/row;
    /// full-buffer parity remains unproven"). Earlier versions of this check each reported
    /// "byte-for-byte, zero excluded" with that pathway still open, so the claim is made
    /// executable here rather than argued.
    /// <para>
    /// Uses the REAL pair of controls the frame-rig test compares and perturbs only the SIZE
    /// handed to the helper, by a sub-DIP amount chosen (from the measured geometry, not
    /// hardcoded) to land squarely in the old blind spot: same value once rounded to whole DIPs —
    /// so the discarded <c>precision: 0</c> equality accepted it — yet a whole extra raster line
    /// once <c>Ceiling</c> runs, which the discarded <c>Floor(Math.Min(...))</c> region then
    /// dropped without a word. Both of those properties are asserted, so the construction cannot
    /// quietly stop being the blind spot. Run for BOTH dimensions (the omitted line may be a
    /// column or a row), then the untampered sizes are re-run against the SAME two controls and
    /// still pass — proving the failures above were the perturbation, not a real defect.
    /// </para>
    /// </summary>
    [AvaloniaFact]
    public void AssertFullRasterPixelIdentity_SubDipDriftAcrossARasterLine_FailsInsteadOfShrinkingToTheIntersection()
    {
        SRSCreatorViewModel vm = CreateVm();
        var view = new SRSCreatorView { DataContext = vm };
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

                // Untampered, same two controls, same helper: still byte-for-byte identical across
                // the whole buffer — so the two failures above were caused by the perturbation and
                // not by a real parity defect the drift happened to expose.
                AssertFullRasterPixelIdentity(oldRow0, oldSize, newRow0, newRowSize);

                void AssertDriftedSizeFails(Size drifted)
                {
                    // The discarded gate accepted this pair (identical to whole DIPs)...
                    Assert.Equal(oldSize.Width, drifted.Width, precision: 0);
                    Assert.Equal(oldSize.Height, drifted.Height, precision: 0);

                    // ...while the captures genuinely differ by one whole raster line, which the
                    // discarded Floor(Math.Min(...)) region then excluded from every byte compared.
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

    /// <summary>
    /// Returns a value that rounds to the SAME whole DIP as <paramref name="value"/> but whose
    /// <c>Ceiling</c> — the capture size — differs by exactly one raster line. Derived from the
    /// measured value rather than hardcoded, because which direction lands in the blind spot
    /// depends on the fractional part: when <c>Ceiling == Round</c> the drift must go up (into the
    /// next whole DIP's lower half), otherwise the fraction already rounds down and snapping to
    /// the whole DIP itself is what changes the ceiling.
    /// </summary>
    private static double DriftAcrossOneRasterLine(double value) =>
        Math.Ceiling(value) == Math.Round(value) ? Math.Ceiling(value) + 0.4 : Math.Round(value);

    /// <summary>Verbatim reconstruction of SRSCreatorView.axaml's row-0 TextBlock before this task (git history).</summary>
    private static Window BuildPreDisclosureRow0Window()
    {
        var textBlock = new TextBlock
        {
            Text = "Create an SRS (Sample Rescue Storage) file from a sample video file. The SRS stores enough data to reconstruct the exact sample from any copy of the same video.",
            Foreground = (IBrush?)Application.Current!.FindResource("ForegroundSecondary"),
            FontSize = (double)Application.Current!.FindResource("FontSizeCaption")!,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 6),
        };

        return new Window { Width = CompactInvariantRig.InnerWidth, SizeToContent = SizeToContent.Height, Content = textBlock };
    }

    /// <summary>
    /// Renders both controls to a <see cref="RenderTargetBitmap"/> at their OWN true geometry
    /// (each sized to its own full bounds, independent of whatever window each is actually hosted
    /// in) and requires true byte-for-byte identity of the ENTIRE buffer on BOTH sides. There is
    /// no mask, no crop, no intersection and no offset: every byte of both captures participates.
    /// <para>
    /// The reason this is written the way it is: the previous version
    /// checked size agreement only at ROUNDED DIP granularity
    /// (<c>Assert.Equal(w1, w2, precision: 0)</c>) and then derived its comparison region from
    /// <c>Floor(Math.Min(...))</c>. Those two facts combine into a silent exclusion pathway — a
    /// pair like 676.0 vs 676.4 passes the rounded check, yet <c>Ceiling</c> makes the two
    /// captures 676 and 677 raster columns wide, and <c>Floor(Min(...))</c> then quietly compares
    /// 676 of them, leaving the wider capture's terminal column entirely unscrutinised while the
    /// test still reported "full parity, zero excluded". The same arithmetic applies to the
    /// terminal row via the height. Whether that pathway is currently reachable is beside the
    /// point: an unproven region must fail loudly, not shrink. MEASURED on the discarded body
    /// with exactly that pair: its size gate passed, the captures came out 676x35 and 677x35, the
    /// compared region was 676x35, and 140 bytes of the wider capture (one terminal column x 35
    /// rows) were never read — under a message that claimed "no width-based crop".
    /// </para>
    /// <para>
    /// MEASURED, and the reason the gate below is load-bearing rather than pedantic:
    /// <c>RenderTargetBitmap.Render</c> lays the visual out to the BITMAP's size, so the capture
    /// size is an input to the rendering, not just a canvas around it. Rendering this view's own
    /// row into a 677-wide bitmap instead of its natural 676 perturbs the SHARED columns too
    /// (first difference at x=0, y=5 — the text reflows), not merely the extra one. So a raster
    /// disagreement does not mean "the same picture, one line longer"; it means two different
    /// layouts. Clamping to their intersection would compare two different renderings and report
    /// parity — strictly worse than the omitted-terminal-line problem that motivated this fix.
    /// </para>
    /// <para>
    /// So raster agreement is now asserted EXACTLY, in integer pixels, in BOTH dimensions, BEFORE
    /// a single byte is read — a mismatch is a failure that names both sizes, never a clamp. Once
    /// past it, ONE <see cref="PixelSize"/> value renders both sides, so equal stride and equal
    /// buffer length are structural rather than merely asserted, and the loop below walks the
    /// whole buffer end to end. Proven to discriminate by
    /// <see cref="AssertFullRasterPixelIdentity_SubDipDriftAcrossARasterLine_FailsInsteadOfShrinkingToTheIntersection"/>.
    /// </para>
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

        // Past the gate the two sizes are the same value, so a SINGLE PixelSize renders both:
        // equal stride and equal buffer length are structural here, not an assumption.
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
    // the finished view, WITH Create SRS enabled). Each entry is CompactViewRig.Describe's own
    // format (real automation peer name plus x:Name, reported separately — see its own doc) — a
    // human-readable regression net
    // (catches renames, additions, removals), NOT the discriminating check itself. The ordering
    // check itself is AssertTabWalk's OWN independent, reference-based one
    // (ResolveIndependentExpectedOrder + AssertSameControlSequence, both forward and reverse —
    // see AssertTabWalk's own doc for why the fixture strings alone cannot do this), proven to
    // discriminate by AssertSameControlSequence_SwappedPositions_FailsNamingTheMismatch and
    // AssertSameControlSequence_IdenticallyDescribedControls_AreDistinguishedByReference.
    // This view's three "Browse" buttons used to describe identically; naming them
    // ("Browse for sample file"/"…main file"/"…output path") means NO entry in these fixtures
    // describes identically to another any more.
    // The two entries that used to read name="" — the Main-file picker and the App-name box —
    // were a SECOND identically-described pair, and real a11y debt behind it. Both are named now:
    // "Main file path" follows the "<subject> path" convention, and the App-name box is LabeledBy
    // its "App name:" caption, which is why its measured peer name carries the caption's colon. ──

    /// <summary>
    /// Normal mode, starting at Sample File's own Browse button — PROVEN first (not presumed):
    /// the reverse walk anchored at the tail end (Save log) retraces this exact sequence
    /// backwards and lands back on this same Browse button, empirically confirming nothing
    /// precedes it. From there: Sample File's Browse + its TextBox, Main file's Browse/Clear +
    /// its TextBox, Output's Browse + its TextBox, the App name TextBox, Create SRS
    /// (InputPath/OutputPath set so it is genuinely enabled and its own position is pinned —
    /// CanExecute false for the default inert VM would otherwise leave it absent and
    /// unverified, the same situation the Reconstructor's own "Start" button fixture
    /// documents), then Save log. Cancel is absent (hidden, IsCreating false).
    /// </summary>
    private static readonly IReadOnlyList<string> NormalModeTabOrderFixture =
    [
        "TextBox name=\"Sample file path\" id=\"InputTextBox\"",
        "Button name=\"Browse for sample file\" id=\"\"",
        "TextBox name=\"Main file path\" id=\"\"",
        "Button name=\"Clear\" id=\"\"",
        "Button name=\"Browse for main file\" id=\"\"",
        "TextBox name=\"Output path\" id=\"OutputTextBox\"",
        "Button name=\"Browse for output path\" id=\"\"",
        "TextBox name=\"App name:\" id=\"\"",
        "Button name=\"Create SRS\" id=\"\"",
        "Button name=\"Save log...\" id=\"\"",
    ];

    /// <summary>
    /// Compact order: disclosure header toggle → (body skipped: Help starts collapsed
    /// per condition 5, so the plain-prose body is IsVisible=false and correctly excluded from
    /// Tab order) → identical tail to normal mode (this walk starts one stop earlier, at the
    /// header toggle, rather than at Sample File's Browse button — likewise PROVEN first here by
    /// its own reverse walk landing back on the toggle).
    /// </summary>
    private static readonly IReadOnlyList<string> CompactModeTabOrderFixture =
    [
        "ScrollViewer name=\"Help content\" id=\"HelpBody\"",
        "TextBox name=\"Sample file path\" id=\"InputTextBox\"",
        "Button name=\"Browse for sample file\" id=\"\"",
        "TextBox name=\"Main file path\" id=\"\"",
        "Button name=\"Clear\" id=\"\"",
        "Button name=\"Browse for main file\" id=\"\"",
        "TextBox name=\"Output path\" id=\"OutputTextBox\"",
        "Button name=\"Browse for output path\" id=\"\"",
        "TextBox name=\"App name:\" id=\"\"",
        "Button name=\"Create SRS\" id=\"\"",
        "Button name=\"Save log...\" id=\"\"",
    ];
}
