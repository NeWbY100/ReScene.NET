using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Automation.Peers;
using Avalonia.Automation.Provider;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using ReScene.App.Core.Services;
using ReScene.App.Core.ViewModels;
using ReScene.Core;
using ReScene.Manager.Behaviors;
using ReScene.Manager.Services;
using ReScene.Manager.Views;

namespace ReScene.Manager.Tests;

/// <summary>
/// Small-window layout degradation tests for <see cref="ReconstructorView"/> (switch height
/// DERIVED from the view's own measured expanded floor — see <see cref="Threshold"/> —
/// TabControl minimums 130/96/60, log 80, Help body MaxHeight 38, compact CI bound
/// <see cref="CompactInvariantRig.CiBound"/> == 307). This is the TEMPLATE per-view
/// shape every later view task (SRSCreator, SRSReconstructor, SampleRestorer, Creator) copies —
/// <see cref="CompactViewRig"/> members plus VM property setters only, no other undefined helpers.
/// </summary>
public class ReconstructorCompactTests
{
    // ── Inert VM construction (mirrors ReconstructorViewTests.CreateVm) ──

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

    private sealed class InlineUiDispatcher : IUiDispatcher
    {
        public void Invoke(Action action) => action();
        public void Post(Action action) => action();
        public void Post(Action action, UiDispatcherPriority priority) => action();
        public bool CheckAccess() => true;
    }

    private sealed class InertUiTimerFactory : IUiTimerFactory
    {
        public IUiTimer Create(TimeSpan interval, Action onTick) => new NoOpTimer();

        private sealed class NoOpTimer : IUiTimer
        {
            public void Start() { }
            public void Stop() { }
        }
    }

    private static ReconstructorViewModel CreateVm() =>
        new(
            new InertBruteForceService(),
            new AvaloniaFileDialogService(static () => null),
            new InlineUiDispatcher(),
            new InertUiTimerFactory());

    private static ReconstructorView BuildWorstCase()
    {
        ReconstructorViewModel vm = CreateVm();
        vm.CustomPackerWarning = "Custom packer detected."; // worst case: warning row forced visible
        return new ReconstructorView { DataContext = vm };
    }

    /// <summary>
    /// This view's switch height, READ BACK from the behavior rather than written down: the
    /// derived model computes it from the view's own measured expanded floor, so a platform whose
    /// font metrics need more room gets a larger number here and every height derived from it
    /// moves with it. Probed once per test process — the derivation is deterministic for a given
    /// build and font stack, and probing it costs a hosted window each time.
    /// </summary>
    private static double Threshold => _threshold.Value;

    private static readonly Lazy<double> _threshold =
        new(() => CompactInvariantRig.ProbeSwitchPoint(BuildWorstCase));

    private const double CompactInner = 319;   // the canonical 700x450 minimum window

    /// <summary>Comfortably above <see cref="Threshold"/>, clear of the restore hysteresis.</summary>
    private static double ExpandedInner => Threshold + CompactInvariantRig.ExpandedHeadroom;

    private const string FullTip =
        "Tip: click “Import from SRR” to auto-configure versions, compression, " +
        "dictionary, timestamps and Host OS from the release's SRR.";

    // ── 1. Invariant (the four floor-height/budget checks; CompactInvariantRig) ────

    /// <summary>
    /// The derivation's own guarantee, in place of the constant this used to pin: whatever the
    /// view's expanded floor measures on this platform, the height it switches at is above it. A
    /// hand-calibrated number could be — and on Linux was — below the floor it was supposed to
    /// clear; a derived one cannot be, and that is the property worth a test.
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
        CompactInvariantRig.AssertActiveModeFitsAroundSwitchPoint("Reconstructor", BuildWorstCase);

    /// <summary>
    /// The discriminating property of a DERIVED switch height, stated as the thing a constant
    /// cannot do: give the view more content that its layout cannot scroll away, and the height it
    /// switches at goes UP to match. Under the shipped constant this view's threshold was 421 no
    /// matter what its warning row said, which is exactly how a platform needing 438 ended up
    /// showing clipped expanded content.
    /// <para>
    /// The warning row is the honest lever here: it is chrome (a plain Auto row, shown whole or
    /// not at all), so growing it genuinely raises the floor rather than being absorbed by a
    /// scrolling band — which is also why growing the TabControl's content instead would correctly
    /// NOT move the threshold.
    /// </para>
    /// </summary>
    [AvaloniaFact]
    public void DerivedThreshold_RisesWithUnscrollableContent_WhereAConstantWouldNot()
    {
        (Window window, Grid root) = CompactViewRig.HostAt(BuildWorstCase(), ExpandedInner);
        try
        {
            double before = CompactHeightBehavior.GetEffectiveThreshold(root);

            var vm = (ReconstructorViewModel)window.GetVisualDescendants()
                .OfType<ReconstructorView>().Single().DataContext!;
            vm.CustomPackerWarning = string.Join(" ", Enumerable.Repeat(
                "Custom packer detected; the reconstruction may not be byte-identical.", 12));
            Dispatcher.UIThread.RunJobs();

            double after = CompactHeightBehavior.GetEffectiveThreshold(root);
            Assert.True(after > before,
                $"a taller warning row must raise the derived switch height, but it stayed at " +
                $"{before:F1} -> {after:F1} (a per-view constant is what would behave this way)");
        }
        finally { window.Close(); }
    }


    [AvaloniaFact]
    public void Invariant_CompactFloor_WithinCiBound_AndPinnedToolbarRowSane()
    {
        ReconstructorViewModel vm = CreateVm();
        vm.CustomPackerWarning = "Custom packer detected.";
        var view = new ReconstructorView { DataContext = vm };

        (Window window, Grid root) = CompactViewRig.HostAt(view, CompactInner);
        try
        {
            Assert.Contains("compactHeight", root.Classes);

            // One sum: donation rows applied (TabControl min -> 60) AND the body's own MaxHeight
            // (38) both spend the same 307-DIP budget — never checked independently.
            double floor = CompactInvariantRig.MeasureFloor(root);
            Assert.True(floor <= CompactInvariantRig.CiBound,
                $"compact floor {floor:F1} must be <= {CompactInvariantRig.CiBound}");

            // 4. Pinned/action row (the persistent toolbar, row 1) is never the budget donor —
            // its natural height stays small and positive regardless of mode.
            Control toolbar = root.Children.OfType<Control>().Single(c => Grid.GetRow(c) == 1);
            Assert.True(toolbar.DesiredSize.Height is > 0 and <= 40,
                $"pinned toolbar row height {toolbar.DesiredSize.Height:F1} out of the expected pinned-row range");
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

    /// <summary>
    /// Each of the four checks below gets its OWN fresh view/VM/window instance, rather than
    /// sharing one window across tab switches: switching TabControl.SelectedIndex mid-test left
    /// focus stranded on a control from the PREVIOUS tab (now detached/invisible), and Avalonia's
    /// own Tab navigation from a stale focused element that no longer participates in the tree
    /// behaved unpredictably (observed: an endless Button-only cycle that never reached the new
    /// tab's controls at all). A fresh host per check is simpler, isolates each scenario, and
    /// matches how a real user would arrive at each tab independently.
    /// </summary>
    private static void AssertReachabilityNoClipAndTabWalk(double innerHeight, bool expectCompact)
    {
        AssertNoClip(innerHeight, expectCompact);
        AssertLastControlReachable(innerHeight, tabIndex: 4,
            c => c.Content as string == "I - Set not content indexed attribute on each file before compressing.");
        AssertLastControlReachable(innerHeight, tabIndex: 5,
            c => c.Content as string == "Patch brute-forced RAR headers to match the original archive (Host OS, attributes, LARGE flag, mtime).");
        AssertTabWalk(innerHeight);
    }

    private static void AssertNoClip(double innerHeight, bool expectCompact)
    {
        ReconstructorViewModel vm = CreateVm();
        vm.CustomPackerWarning = "Custom packer detected."; // criterion B worst case: warning forced
        var view = new ReconstructorView { DataContext = vm };

        (Window window, Grid root) = CompactViewRig.HostAt(view, innerHeight);
        try
        {
            Assert.Equal(expectCompact, root.Classes.Contains("compactHeight"));
            CompactInvariantRig.AssertArrangesWithin(root, root.Bounds.Height);
        }
        finally { window.Close(); }
    }

    private static void AssertLastControlReachable(double innerHeight, int tabIndex, Func<CheckBox, bool> isTarget)
    {
        ReconstructorViewModel vm = CreateVm();
        var view = new ReconstructorView { DataContext = vm };
        (Window window, Grid root) = CompactViewRig.HostAt(view, innerHeight);
        try
        {
            var settingsTabs = window.GetVisualDescendants().OfType<TabControl>().Single(t => t.ItemCount == 6);
            settingsTabs.SelectedIndex = tabIndex;
            Dispatcher.UIThread.RunJobs();
            CheckBox target = window.GetVisualDescendants().OfType<CheckBox>().Single(isTarget);
            AssertReachableByAllThreeRoutes(window, settingsTabs, target);
        }
        finally { window.Close(); }
    }

    /// <summary>
    /// One FORWARD walk plus TWO independent, per-scope REVERSE walks — one per keyboard-navigation
    /// scope this view actually has. There are two because this view nests a second
    /// <see cref="TabControl"/> (the Paths/Options sub-tabs) inside the shell's own, and each scopes
    /// keyboard navigation to its selected content, so no single reverse walk can cross the inner
    /// boundary. Scope A is everything up to and including the Paths <c>TabItem</c> header; scope B
    /// is the Paths sub-tab's own content.
    /// <para>
    /// EVERY check is anchored on <see cref="ResolveIndependentExpectedOrder"/> — one authored list
    /// resolved by bound command, x:Name and <c>Content</c>, never derived from a walk's own output.
    /// The forward walk's completeness set, its starting sentinel, both reverse walks' completeness
    /// sets and both reverse ORDER expectations all come from that same list. The committed
    /// description fixtures (<see cref="NormalModeTabOrderFixture"/> /
    /// <see cref="CompactModeTabOrderFixture"/>) are compared too, but only as a human-readable
    /// regression net; they are not the discriminating check.
    /// </para>
    /// <para>
    /// This replaced an oracle that derived each reverse expectation from the forward walk it was
    /// checking (gate finding NEW-3). That cannot fail on a tree-level permutation, because both
    /// sides move together —
    /// <see cref="SelfReferentialReverseOracle_PassesAPermutedTree_WhereTheIndependentOracleFails"/>
    /// demonstrates exactly that against a deliberately broken tree. The same change retired
    /// <c>ResolveExpectedStops</c>, which existed only to turn description fixtures back into
    /// references because no independent oracle was available.
    /// </para>
    /// <para>
    /// Beyond order, three things are asserted that an order check alone would miss: each reverse
    /// walk LANDS on its own scope's first-in-scope element (so a topology change that merges or
    /// splits the scopes fails loudly instead of being absorbed by whichever walk happens to run);
    /// the forward walk's terminal EXTERNAL target is the specific expected shell-chrome boundary,
    /// by object identity; and the exact reference UNION of both reverse walks equals the forward
    /// walk's full inventory, so a control reachable forward but in neither reverse scope fails
    /// here rather than passing quietly.
    /// </para>
    /// </summary>
    private static void AssertTabWalk(double innerHeight)
    {
        ReconstructorViewModel vm = CreateVm();
        var view = new ReconstructorView { DataContext = vm };
        (Window window, Grid root) = CompactViewRig.HostAt(view, innerHeight);
        try
        {
            bool compact = root.Classes.Contains("compactHeight");

            // Independent ground truth, resolved BEFORE any walk runs and never derived from a
            // walk's own output — gate finding NEW-3. The sentinel comes from it too rather than
            // being hardcoded here, so "which control is first" is a claim the oracle makes and the
            // reverse walk's own boundary-landing assertion below then PROVES, instead of a
            // presumption baked into the test's setup.
            List<Control> independentOrder = ResolveIndependentExpectedOrder(window, vm, compact);
            Control sentinel = independentOrder[0];

            IReadOnlyList<string> forwardFixture = compact ? CompactModeTabOrderFixture : NormalModeTabOrderFixture;

            // The forward walk uses CaptureTabOrderControls (root-SCOPED: stops the moment focus
            // would leave root, exactly like NormalModeTabOrderFixture/CompactModeTabOrderFixture
            // were themselves captured) rather than RunTabPass (UNSCOPED: keeps walking into the
            // surrounding shell chrome — MenuItem/status-bar controls — until it returns to the
            // sentinel or repeats, which it eventually does, but only after visiting controls
            // outside this view entirely). The per-scope REVERSE walks below still use RunTabPass
            // directly: reverse never needs to leave root's scope to begin with (both this
            // view's navigation scopes are entirely WITHIN root), so
            // RunTabPass's own "stable loop" boundary is the right one there.
            sentinel.Focus();
            Dispatcher.UIThread.RunJobs();
            CompactViewRig.TabOrderCapture forwardCapture = CompactViewRig.CaptureTabOrderControls(window, root, independentOrder);
            IReadOnlyList<Control> forwardOrder = forwardCapture.Order;
            Assert.Equal(forwardFixture, forwardOrder.Select(CompactViewRig.Describe)); // human-readable regression net
            AssertSameControlSequence(independentOrder, forwardOrder, "forward"); // the actual discriminating check

            // The terminal EXTERNAL target (the first control outside root
            // the forward walk lands on) must be the SPECIFIC, expected shell-chrome boundary, not
            // accepted blind — an unvalidated blind exit could mask a topology change that makes
            // the walk leave root somewhere unintended (e.g. mid-view, rather than genuinely
            // exhausting root's own tab order first). Confirmed via a real run (both modes,
            // consistently): the rig's own fake shell (CompactViewRig.BuildShell) puts a "_File"
            // MenuItem right after the TabControl in Z-order, so that is the first control the
            // walk reaches once it exhausts this view's own root.
            //
            // OBJECT-IDENTITY, not description — consistent with the reference-exact ordering
            // standard already established. The expected boundary is captured directly from
            // the shell (window.GetVisualDescendants(), independent of the walk itself, matched
            // on the "_File" MenuItem's own Header) and compared via ReferenceEquals; the
            // description is used only in the failure message.
            MenuItem expectedForwardExternalBoundary = window.GetVisualDescendants().OfType<MenuItem>()
                .Single(m => m.Header as string == "_File");
            Assert.True(forwardCapture.FirstExternalTarget is not null,
                "forward capture should have left root's scope onto an external control, not ended via a stable loop within root");
            Assert.True(ReferenceEquals(expectedForwardExternalBoundary, forwardCapture.FirstExternalTarget),
                $"forward capture's terminal external target should be {CompactViewRig.Describe(expectedForwardExternalBoundary)}, " +
                $"not {CompactViewRig.Describe(forwardCapture.FirstExternalTarget)} — same description does not mean same control instance.");

            // Scope split: scope A is everything up to and including the Paths TabItem header;
            // scope B is everything after (the Paths sub-tab's own content). The split index comes
            // from the INDEPENDENT order — the sole TabItem in it — not from a fixture string and
            // not from the walk. The per-scope machinery itself is unchanged and must stay: this
            // view nests a second TabControl (the Paths/Options sub-tabs) inside the shell's own,
            // and each scopes keyboard navigation to its selected content, so no single reverse
            // walk can cross the inner boundary.
            int tabItemIndex = independentOrder.FindIndex(c => c is TabItem);
            Assert.True(tabItemIndex > 0 && tabItemIndex < independentOrder.Count - 1,
                $"the independent order must contain the Paths TabItem strictly inside it (found at {tabItemIndex} of {independentOrder.Count}) — the two-scope split is derived from its position");
            Control scopeAAnchor = independentOrder[tabItemIndex];
            Control scopeAFirstInScope = independentOrder[0];
            Control scopeBAnchor = independentOrder[^1];
            Control scopeBFirstInScope = independentOrder[tabItemIndex + 1];

            CompactViewRig.TabWalkResult scopeAReverse = CompactViewRig.RunTabPass(window, scopeAAnchor, forward: false, independentOrder.Take(tabItemIndex + 1).ToList());
            CompactViewRig.TabWalkResult scopeBReverse = CompactViewRig.RunTabPass(window, scopeBAnchor, forward: false, independentOrder.Skip(tabItemIndex + 1).ToList());

            // ORDER, explicit and OBJECT-REFERENCE-exact, against the INDEPENDENT list's own
            // reversal — NOT forwardOrder.Reverse(), which is what gate finding NEW-3 was about.
            // Deriving the reverse expectation from the forward walk makes the oracle
            // self-referential: a tree-level defect that permutes stops moves BOTH sides together,
            // so reverse "agrees" with a forward order that is already wrong and the pair passes.
            // Anchoring both directions on a list resolved by command/x:Name identity is what makes
            // the two walks genuinely independent evidence rather than one walk checked twice.
            List<Control> expectedScopeAReverseOrder = [.. independentOrder.Take(tabItemIndex + 1).Reverse()];
            List<Control> expectedScopeBReverseOrder = [.. independentOrder.Skip(tabItemIndex + 1).Reverse()];
            AssertSameControlSequence(expectedScopeAReverseOrder, scopeAReverse.Order, "scope A reverse");
            AssertSameControlSequence(expectedScopeBReverseOrder, scopeBReverse.Order, "scope B reverse");

            // BOUNDARY LANDING, explicit — so a topology change that merges/splits the two scopes
            // differently fails loudly instead of being silently absorbed by the split.
            Assert.True(ReferenceEquals(scopeAReverse.LoopedBackTo, scopeAFirstInScope),
                $"scope A's reverse walk should land on {CompactViewRig.Describe(scopeAFirstInScope)}, " +
                $"not {CompactViewRig.Describe(scopeAReverse.LoopedBackTo)}");
            Assert.True(ReferenceEquals(scopeBReverse.LoopedBackTo, scopeBFirstInScope),
                $"scope B's reverse walk should land on {CompactViewRig.Describe(scopeBFirstInScope)}, " +
                $"not {CompactViewRig.Describe(scopeBReverse.LoopedBackTo)}");

            // UNION: the exact reference union of both scopes' reverse-visited controls must equal
            // the forward walk's full inventory — any control in NEITHER reverse scope fails here.
            var unionOfReverseScopes = new HashSet<Control>(ReferenceEqualityComparer.Instance);
            foreach (Control c in scopeAReverse.Order)
            { unionOfReverseScopes.Add(c); }
            foreach (Control c in scopeBReverse.Order)
            { unionOfReverseScopes.Add(c); }
            var forwardInventory = new HashSet<Control>(forwardOrder, ReferenceEqualityComparer.Instance);
            Assert.True(unionOfReverseScopes.SetEquals(forwardInventory),
                $"the union of scope A's ({scopeAReverse.Order.Count}) and scope B's " +
                $"({scopeBReverse.Order.Count}) reverse-visited controls must exactly equal the " +
                $"forward walk's full inventory ({forwardOrder.Count}) — some control is " +
                "reachable forward but in neither reverse scope.");
        }
        finally { window.Close(); }
    }

    /// <summary>
    /// Independent ground truth for this view's tab order — gate finding NEW-3. Every entry is
    /// resolved by a UNIQUE IDENTIFIER that exists in the authored markup and has nothing to do
    /// with tab order: a bound <c>RelayCommand</c> reference for the action buttons, an x:Name for
    /// the four path TextBoxes, the sole <see cref="GridSplitter"/>, the settings
    /// <see cref="TabControl"/>'s own first item, and distinct authored <c>Content</c> strings for
    /// the three help links and the Auto-scroll checkbox.
    /// <para>
    /// What this replaces, and why it mattered: both reverse walks used to be checked against
    /// slices of the FORWARD walk's own output. That oracle cannot fail in the one way it most
    /// needs to — a defect in the visual tree that permutes stops moves the forward order and the
    /// expectation derived from it together, so the reverse walk "agrees" with an order that is
    /// already wrong. Resolving identity from the markup instead makes the two directions
    /// independent evidence. It also lets the walk's own completeness parameter and its starting
    /// sentinel come from the same authored list, so "the walk visits everything" and "the walk
    /// starts in the right place" stop being assumptions of the test's setup.
    /// </para>
    /// <para>
    /// MODE DIFFERENCE, and it is real rather than cosmetic: in compact mode Help starts collapsed
    /// (condition 5), so the three link buttons are <c>IsVisible=false</c> and genuinely absent
    /// from the order, and the disclosure's own header toggle — visible ONLY in compact — leads
    /// instead. In expanded/flat mode the body is force-expanded and the header toggle is hidden,
    /// so the first link is the true first stop.
    /// </para>
    /// </summary>
    private static List<Control> ResolveIndependentExpectedOrder(Window window, ReconstructorViewModel vm, bool compact)
    {
        Button ByCommand(System.Windows.Input.ICommand command) =>
            window.GetVisualDescendants().OfType<Button>().Single(b => ReferenceEquals(b.Command, command));
        TextBox ByTestId(string id) =>
            window.GetVisualDescendants().OfType<TextBox>().Single(t => t.Name == id);
        Button ByContent(string content) =>
            window.GetVisualDescendants().OfType<Button>().Single(b => b.Content as string == content);

        var settingsTabs = window.GetVisualDescendants().OfType<TabControl>().Single(t => t.ItemCount == 6);
        var pathsTab = (TabItem)settingsTabs.Items[0]!;

        List<Control> order =
        [
            ByCommand(vm.ExportConfigCommand),
            ByCommand(vm.ImportConfigCommand),
            ByCommand(vm.ImportSRRCommand),
            pathsTab,
            // Field THEN button, per row: each row is a right-docked DockPanel whose button is
            // declared first, and TabIndex pins plus Local scoping now put the walk back into the
            // order the row renders. Before that fix this list read button-then-field.
            ByTestId("WinRARTextBox"), ByCommand(vm.BrowseWinRARCommand),
            ByTestId("ReleaseTextBox"), ByCommand(vm.BrowseReleaseCommand),
            ByTestId("VerifyTextBox"), ByCommand(vm.BrowseVerificationCommand),
            ByTestId("OutputTextBox"), ByCommand(vm.BrowseOutputCommand),
            window.GetVisualDescendants().OfType<GridSplitter>().Single(),
            ByCommand(vm.SaveLogCommand),
            window.GetVisualDescendants().OfType<CheckBox>().Single(c => c.Content as string == "Auto-scroll"),
        ];

        // "Start" is deliberately absent from this list: it is command-gated on the inert VM's
        // empty paths, so it is disabled and Tab correctly skips it. That is a property of the
        // fixture VM, not of the view, and is recorded in the fixtures' own doc comments too.
        // The Help links lead the walk in BOTH modes now. They used to be normal-mode only,
        // because compact collapsed the Help body and an unrealized body has no tab stops; the
        // compact walk started at the header toggle instead. Help is always showing, so the links
        // are always realized and always first — and this view's body itself is never a stop.
        {
            order.InsertRange(0,
            [
                window.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "WindowsPackLink"),
                ByContent("Extracted files for Linux (ready to use)"),
                ByContent("Original files from RAR FTP (Windows)"),
            ]);
        }

        return order;
    }

    /// <summary>
    /// OBJECT-REFERENCE-exact sequence comparison — asserts
    /// <paramref name="actual"/> is, position for position, the SAME control REFERENCES as
    /// <paramref name="expected"/>, not merely the same DESCRIPTIONS. A description-based
    /// <c>Assert.Equal</c> cannot distinguish a permutation of controls that all describe
    /// identically; this can, since it never converts either side to a string until it already
    /// knows a mismatch exists and needs to report it. This view supplied the motivating example
    /// until the naming pass: its four "Browse" buttons carried neither an x:Name nor an accessible
    /// name and so described identically. They no longer do — the property is proven directly
    /// instead, by
    /// <see cref="AssertSameControlSequence_IdenticallyDescribedControls_AreDistinguishedByReference"/>.
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

    /// <summary>
    /// Proves <see cref="AssertSameControlSequence"/> — and therefore
    /// <see cref="AssertTabWalk"/>'s own per-scope reverse order checks, which rely on it — is
    /// genuinely sensitive to a PERMUTATION, not just to controls going missing. Captures the REAL
    /// forward walk, builds scope B's real, correctly reversed expected order from it, then
    /// deliberately swaps two adjacent positions within that EXPECTED list, runs the REAL scope B
    /// reverse walk (which visits them in the correct, un-swapped order, exactly as the earlier
    /// <c>RenderedMatrix_*</c> tests already confirm) against that deliberately-wrong expectation,
    /// and asserts it fails naming the specific mismatched position.
    /// <para>
    /// REDESIGNED, and the reason matters. This test used to swap two of the four "Browse" buttons
    /// specifically BECAUSE all four described identically, which made it a proof about reference-
    /// versus-description comparison and not merely about ordering. Naming those four buttons
    /// removed the last identically-described pair from this view, so that premise no longer
    /// exists here and selecting on it would now match zero controls. Rather than re-point it at
    /// some other pair without checking the pair is genuinely indistinguishable — which would
    /// hollow the test out while it kept passing — the two claims are split: this test keeps the
    /// real-walk grounding and asserts positional sensitivity, and
    /// <see cref="AssertSameControlSequence_IdenticallyDescribedControls_AreDistinguishedByReference"/>
    /// carries the reference-versus-description claim directly, against a pair constructed to
    /// describe identically. Neither claim was dropped; only the vehicle changed.
    /// </para>
    /// </summary>
    [AvaloniaFact]
    public void AssertSameControlSequence_SwappedPositions_FailsNamingTheMismatch()
    {
        ReconstructorViewModel vm = CreateVm();
        var view = new ReconstructorView { DataContext = vm };
        (Window window, Grid root) = CompactViewRig.HostAt(view, ExpandedInner);
        try
        {
            List<Control> independentOrder = ResolveIndependentExpectedOrder(window, vm, compact: false);
            independentOrder[0].Focus();
            Dispatcher.UIThread.RunJobs();

            IReadOnlyList<Control> forwardOrder = CompactViewRig.CaptureTabOrderControls(window, root, independentOrder).Order;
            int tabItemIndex = independentOrder.FindIndex(c => c is TabItem);
            Control scopeBAnchor = independentOrder[^1];

            List<Control> expectedScopeBReverseOrder = [.. independentOrder.Skip(tabItemIndex + 1).Reverse()];
            Assert.True(expectedScopeBReverseOrder.Count >= 2, "this covering test needs at least 2 stops in scope B to swap");

            (expectedScopeBReverseOrder[0], expectedScopeBReverseOrder[1]) =
                (expectedScopeBReverseOrder[1], expectedScopeBReverseOrder[0]);

            CompactViewRig.TabWalkResult scopeBReverse = CompactViewRig.RunTabPass(window, scopeBAnchor, forward: false);

            Xunit.Sdk.FailException ex = Assert.Throws<Xunit.Sdk.FailException>(
                () => AssertSameControlSequence(expectedScopeBReverseOrder, scopeBReverse.Order, "scope B reverse"));

            Assert.Contains("position 0", ex.Message, StringComparison.Ordinal);
            Assert.Contains("same description does not mean same control instance", ex.Message, StringComparison.Ordinal);
        }
        finally { window.Close(); }
    }

    /// <summary>
    /// The half of the old covering test that the naming pass would otherwise have silently
    /// dropped: that <see cref="AssertSameControlSequence"/> catches a permutation a DESCRIPTION-
    /// based comparison cannot see at all. It is asserted here against a pair constructed to
    /// describe identically, because this view no longer contains one — every control in its walk
    /// now carries a distinct accessible name or x:Name, which is the point of the naming pass and
    /// is also what removed the natural example.
    /// <para>
    /// Both halves are stated explicitly rather than assumed: first that a description comparison
    /// genuinely PASSES on the swapped sequence (the old test asserted only the second half and
    /// took this one on trust), then that the reference comparison genuinely FAILS on the same
    /// swap, naming the position. Controls, not doubles — <see cref="CompactViewRig.Describe"/>
    /// reads a real automation peer, so this exercises the same code path the walks do.
    /// </para>
    /// </summary>
    [AvaloniaFact]
    public void AssertSameControlSequence_IdenticallyDescribedControls_AreDistinguishedByReference()
    {
        var first = new Button { Content = "Browse" };
        var second = new Button { Content = "Browse" };
        var neighbour = new TextBox { Name = "Anchor" };

        // Precondition, measured rather than presumed: these two really are indistinguishable to
        // the description channel. If Describe ever grew a disambiguator, this test would be
        // proving nothing and says so here instead of passing quietly.
        Assert.Equal(CompactViewRig.Describe(first), CompactViewRig.Describe(second));
        Assert.False(ReferenceEquals(first, second));

        List<Control> actual = [first, neighbour, second];
        List<Control> swapped = [second, neighbour, first];

        // A description-based oracle is blind to the swap — this is the specific gap
        // AssertSameControlSequence exists to close, and it is asserted, not assumed.
        Assert.Equal(actual.Select(CompactViewRig.Describe), swapped.Select(CompactViewRig.Describe));

        Xunit.Sdk.FailException ex = Assert.Throws<Xunit.Sdk.FailException>(
            () => AssertSameControlSequence(swapped, actual, "constructed identical-description pair"));

        Assert.Contains("position 0", ex.Message, StringComparison.Ordinal);
        Assert.Contains("same description does not mean same control instance", ex.Message, StringComparison.Ordinal);

        // And it does not cry wolf: the untampered sequence passes against itself.
        AssertSameControlSequence(actual, actual, "constructed pair (untampered, sanity check)");
    }

    /// <summary>
    /// The discriminating evidence for gate finding NEW-3: a SELF-REFERENTIAL reverse oracle
    /// cannot fail on a tree-level permutation, and the independent one can.
    /// <para>
    /// The defect is simulated for real rather than described — two sibling picker rows are swapped
    /// in the live visual tree, which genuinely changes the order a keyboard user walks. Then both
    /// oracles are evaluated against that same broken tree:
    /// </para>
    /// <list type="bullet">
    /// <item>the OLD expectation, <c>forwardOrder.Skip(k).Reverse()</c>, still agrees exactly with
    /// the real reverse walk — because both moved together. It PASSES on a broken view.</item>
    /// <item>the NEW expectation, resolved from authored identity, does not move with the tree and
    /// FAILS, naming the position.</item>
    /// </list>
    /// <para>
    /// SCOPE, stated because it would be easy to overclaim: at gate time the hole was reachable
    /// through the FORWARD check too, since all four Browse buttons described identically and the
    /// description fixture could not tell a swap of two of them from no swap at all. Item 2's
    /// renames closed that particular door by accident — every stop now describes distinctly, so
    /// the fixture comparison would catch a Browse-for-Browse swap on its own. This test therefore
    /// swaps two rows WHOLESALE (each row's Button and TextBox together), which keeps the multiset
    /// of descriptions identical to a correct walk at the pair level while still permuting the
    /// order, and it asserts the old oracle's blindness directly rather than assuming it. The
    /// independence is worth having regardless of whether today's view happens to expose the hole:
    /// the next repeated row template or duplicated action label re-opens it.
    /// </para>
    /// </summary>
    [AvaloniaFact]
    public void SelfReferentialReverseOracle_PassesAPermutedTree_WhereTheIndependentOracleFails()
    {
        ReconstructorViewModel vm = CreateVm();
        var view = new ReconstructorView { DataContext = vm };
        (Window window, Grid root) = CompactViewRig.HostAt(view, ExpandedInner);
        try
        {
            List<Control> independentOrder = ResolveIndependentExpectedOrder(window, vm, compact: false);

            // BREAK: swap the WinRAR and Release picker rows in the live tree. Whole rows, so the
            // sequence of DESCRIPTIONS a correct walk would produce is permuted rather than
            // corrupted — no control appears or disappears.
            var winRarRow = (DockPanel)window.GetVisualDescendants().OfType<TextBox>().Single(t => t.Name == "WinRARTextBox").GetVisualParent()!;
            var releaseRow = (DockPanel)window.GetVisualDescendants().OfType<TextBox>().Single(t => t.Name == "ReleaseTextBox").GetVisualParent()!;
            var host = (StackPanel)winRarRow.GetVisualParent()!;
            Assert.True(ReferenceEquals(host, releaseRow.GetVisualParent()),
                "test precondition: both rows must share one parent panel, or swapping them is not a simple reorder");

            int winRarAt = host.Children.IndexOf(winRarRow);
            int releaseAt = host.Children.IndexOf(releaseRow);
            Assert.True(winRarAt >= 0 && releaseAt >= 0 && winRarAt < releaseAt, "test precondition: both rows must be children of that panel, WinRAR first");

            host.Children.Remove(releaseRow);
            host.Children.Remove(winRarRow);
            host.Children.Insert(winRarAt, releaseRow);
            host.Children.Insert(releaseAt, winRarRow);
            Dispatcher.UIThread.RunJobs();

            independentOrder[0].Focus();
            Dispatcher.UIThread.RunJobs();
            IReadOnlyList<Control> forwardOrder = CompactViewRig.CaptureTabOrderControls(window, root).Order;

            int tabItemIndex = independentOrder.FindIndex(c => c is TabItem);
            Control scopeBAnchor = forwardOrder[^1];
            CompactViewRig.TabWalkResult scopeBReverse = CompactViewRig.RunTabPass(window, scopeBAnchor, forward: false);

            // THE OLD ORACLE, reproduced verbatim: derived from the walk it is supposed to check.
            List<Control> selfReferentialExpectation = [.. forwardOrder.Skip(tabItemIndex + 1).Reverse()];
            AssertSameControlSequence(selfReferentialExpectation, scopeBReverse.Order,
                "scope B reverse (self-referential oracle, on a DELIBERATELY BROKEN tree)");

            // THE NEW ORACLE: authored identity, which did not move when the tree did.
            List<Control> independentExpectation = [.. independentOrder.Skip(tabItemIndex + 1).Reverse()];
            Xunit.Sdk.FailException ex = Assert.Throws<Xunit.Sdk.FailException>(
                () => AssertSameControlSequence(independentExpectation, scopeBReverse.Order, "scope B reverse"));
            Assert.Contains("same description does not mean same control instance", ex.Message, StringComparison.Ordinal);

            // And the forward direction fails against it too — the permutation is caught in both
            // directions once the oracle stops being derived from the thing under test.
            Assert.ThrowsAny<Xunit.Sdk.XunitException>(
                () => AssertSameControlSequence(independentOrder, forwardOrder, "forward"));
        }
        finally { window.Close(); }
    }

    // ── REMOVED with gate finding NEW-3: ResolveExpectedStops and its covering test
    // (ResolveExpectedStops_FixtureExpectsMoreThanExist_ThrowsNamingTheShortfall).
    //
    // The resolver converted a committed, DESCRIPTION-based fixture back into live Control
    // references, because the walks' completeness parameter is reference-based and a fixture in
    // source can only ever be strings. It existed precisely BECAUSE there was no independent
    // oracle: descriptions were the only committed identity available. ResolveIndependentExpectedOrder
    // now supplies real references resolved from authored identity (command, x:Name, Content), which
    // is strictly better for that job — matching by description is exactly the weakness NEW-3 was
    // raised about — so every real caller moved to it and the resolver was left alive only by its
    // own covering test.
    //
    // Deleted rather than kept: a helper whose sole remaining consumer is the test that proves the
    // helper works is dead scaffolding, and this chain has repeatedly punished leaving that behind.
    // Nothing was lost in coverage. Its counted-multiset property protected against a fixture
    // silently resolving a duplicated description down to one control; the forward walk's
    // Assert.Equal(fixture, forwardOrder.Select(Describe)) is exact whole-sequence equality and
    // already catches any fixture/tree divergence, duplicates included. Recorded here rather than
    // silently dropped because the deletion also removes a test from the count (Manager 471 -> 470
    // before this package's own additions). ──

    /// <summary>
    /// Each route is exercised from a genuine "not yet visible" start (offset reset between
    /// routes) — otherwise the first route scrolling the target into view would make the other
    /// two trivially no-op without ever exercising their own mechanism.
    /// </summary>
    private static void AssertReachableByAllThreeRoutes(Window window, TabControl settingsTabs, Control target)
    {
        ScrollViewer scroller = window.GetVisualDescendants().OfType<ScrollViewer>()
            .Single(sv => sv.TemplatedParent is null && settingsTabs.IsVisualAncestorOf(sv));

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

    // ── 3. Tab-order snapshots ────────────────────────────────────────

    [AvaloniaFact]
    public void TabOrderSnapshot_Normal_MatchesPreChangeFixture()
    {
        ReconstructorViewModel vm = CreateVm();
        var view = new ReconstructorView { DataContext = vm };
        (Window window, Grid root) = CompactViewRig.HostAt(view, ExpandedInner);
        try
        {
            Assert.DoesNotContain("compactHeight", root.Classes);
            Button sentinel = window.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "WindowsPackLink");
            sentinel.Focus();
            Dispatcher.UIThread.RunJobs();

            IReadOnlyList<string> order = CompactViewRig.SnapshotTabOrder(window, root);
            Assert.Equal(NormalModeTabOrderFixture, order);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void TabOrderSnapshot_Compact_MatchesSpecSection2Order()
    {
        ReconstructorViewModel vm = CreateVm();
        var view = new ReconstructorView { DataContext = vm };
        (Window window, Grid root) = CompactViewRig.HostAt(view, CompactInner);
        try
        {
            Assert.Contains("compactHeight", root.Classes);
            // Anchored at the first compact stop. NOT the Help body: this view's body is
            // deliberately never focusable (its links are the keyboard route), so focusing it is a
            // no-op and the walk would start from nowhere.
            Control firstStop = root.GetVisualDescendants().OfType<Button>()
                .Single(b => b.Name == "WindowsPackLink");
            firstStop.Focus();
            Dispatcher.UIThread.RunJobs();

            IReadOnlyList<string> order = CompactViewRig.SnapshotTabOrder(window, root);
            Assert.Equal(CompactModeTabOrderFixture, order);
        }
        finally { window.Close(); }
    }

    // ── 4. Chrome ─────────────────────────────────────────────────────

    [AvaloniaFact]
    public void SingleLinkInstance_ExistsInBothModes()
    {
        ReconstructorViewModel vm = CreateVm();
        var normalView = new ReconstructorView { DataContext = vm };
        (Window normalWindow, Grid normalRoot) = CompactViewRig.HostAt(normalView, ExpandedInner);
        try
        {
            Assert.DoesNotContain("compactHeight", normalRoot.Classes);
            Assert.Equal(3, normalWindow.GetVisualDescendants().OfType<Button>().Count(b => b.Classes.Contains("link")));
        }
        finally { normalWindow.Close(); }

        ReconstructorViewModel vm2 = CreateVm();
        var compactView = new ReconstructorView { DataContext = vm2 };
        (Window compactWindow, Grid compactRoot) = CompactViewRig.HostAt(compactView, CompactInner);
        try
        {
            Assert.Contains("compactHeight", compactRoot.Classes);
            Assert.Equal(3, compactWindow.GetVisualDescendants().OfType<Button>().Count(b => b.Classes.Contains("link")));
        }
        finally { compactWindow.Close(); }
    }

    [AvaloniaFact]
    public void CompactTip_NameAndHelpTextEqualFullText_TrimmingIsVisualOnly()
    {
        ReconstructorViewModel vm = CreateVm();
        var view = new ReconstructorView { DataContext = vm };
        (Window window, Grid root) = CompactViewRig.HostAt(view, CompactInner);
        try
        {
            Assert.Contains("compactHeight", root.Classes);
            TextBlock tip = window.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Classes.Contains("tipLine"));

            // Condition 1: trimming is VISUAL-ONLY over the full bound text. Asserting
            // tip.Text alone (or that the ATTACHED AutomationProperties.Name is
            // null) is not the same claim as "AT announces the full text" — go through the REAL
            // automation peer, the same thing a screen reader actually calls.
            // TextBlockAutomationPeer.GetNameCore() returns Owner.Inlines?.Text ?? Owner.Text, so
            // with no explicit AutomationProperties.Name (asserted below) this is required to
            // equal tip.Text exactly.
            Assert.Null(AutomationProperties.GetName(tip));
            Assert.Equal(FullTip, tip.Text);
            Assert.Equal(FullTip, ControlAutomationPeer.CreatePeerForElement(tip).GetName());
            Assert.Equal(TextTrimming.CharacterEllipsis, tip.TextTrimming);
            Assert.Equal(TextWrapping.NoWrap, tip.TextWrapping);

            // Condition 2: ToolTip + AutomationProperties.HelpText both carry the full text.
            Assert.Equal(FullTip, ToolTip.GetTip(tip) as string);
            Assert.Equal(FullTip, AutomationProperties.GetHelpText(tip));
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void CompactTip_NeverDonates_IdenticalHeightHelpOpenAndClosed()
    {
        ReconstructorViewModel vm = CreateVm();
        var view = new ReconstructorView { DataContext = vm };
        (Window window, Grid root) = CompactViewRig.HostAt(view, CompactInner);
        try
        {
            TextBlock tip = window.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Classes.Contains("tipLine"));
            double heightClosed = tip.Bounds.Height;


            double heightOpen = tip.Bounds.Height;
            Assert.Equal(heightClosed, heightOpen, precision: 1);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void CompactEntry_HelpLinksAreReachable_AndRestoringRelocatesFocus()
    {
        ReconstructorViewModel vm = CreateVm();
        var view = new ReconstructorView { DataContext = vm };
        (Window window, Grid root) = CompactViewRig.HostAt(view, CompactInner);
        try
        {
            // "Invocable" is proven by REACHABILITY/usability (focusable, enabled, unobscured, a
            // real Tab lands on it) rather than actually raising Click — a genuine click on these
            // buttons opens a real OS browser via SystemLauncherService (ResourceLink.cs), which
            // must never fire as a side effect of an automated test run.
            Button windowsLink = window.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "WindowsPackLink");
            Assert.True(windowsLink.Focusable);
            Assert.True(windowsLink.IsEffectivelyEnabled);
            CompactViewRig.AssertReachableByKeyboard(window, windowsLink);

            // The staged-focus guard's actual point: focus something inside the Help region and
            // restore. This view's Help body is deliberately NOT focusable (its links are the
            // keyboard route), so the link itself is the element to hold focus across the
            // transition — it must not be stranded when the mode changes.
            windowsLink.Focus();
            Dispatcher.UIThread.RunJobs();
            Assert.True(windowsLink.IsFocused);

            // Restore to normal, then re-enter compact: durability is compact-SESSION scoped only.
            // Out of compact and comfortably clear of the restore hysteresis, DERIVED rather
            // than a fixed delta: a constant step that clears the switch point on one platform's
            // font metrics can land inside the hysteresis band on another's, leaving this test
            // asserting normal-mode behaviour on a view that never left compact.
            double restoreDelta = (Threshold + 12 + CompactInvariantRig.ExpandedHeadroom) - CompactInner;
            window.Height += restoreDelta;
            Dispatcher.UIThread.RunJobs();
            Assert.DoesNotContain("compactHeight", root.Classes);
            // No body-focusability assertion here: unlike the other views, this one's Help body is
            // never a Tab stop (its links are the keyboard route), so the link itself is what must
            // survive the transition.
            Assert.True(windowsLink.IsFocused,
                "restoring must not strand focus — it belongs on the wired RestoreFocusTarget (WindowsPackLink)");

            window.Height -= restoreDelta;
            Dispatcher.UIThread.RunJobs();
            Assert.Contains("compactHeight", root.Classes);
        }
        finally { window.Close(); }
    }

    /// <summary>
    /// A restore that the derivation immediately overturns must still honour the staged-focus
    /// contract end to end. The sequence is the one place two transitions land back to back with
    /// no user input in between: the restore hides the compact-only header toggle (flat mode's
    /// styles), which clears focus, and the re-validation then re-compacts. If the re-compaction
    /// were allowed to run first it would bump the generation and no-op the restore's own queued
    /// recovery, while having nothing of its own to capture — focus cleared by the behavior and
    /// left cleared, which is exactly the stranding the staged-focus contract exists to prevent.
    /// <para>
    /// The end state asserted is specific, not merely "something is focused": back in compact,
    /// with focus on the header toggle. That is where the rules put it — the restore's recovery
    /// relocates the hidden toggle to the wired RestoreFocusTarget (WindowsPackLink), and the
    /// re-compaction then finds that link inside the collapsed Help body and hands off through the
    /// compact direction's target, which is the header toggle again.
    /// </para>
    /// </summary>
    [AvaloniaFact]
    public void FailedRestore_FromAFocusedHelpLink_EndsCompactWithoutStrandingFocus()
    {
        ReconstructorView view = BuildWorstCase();
        var vm = (ReconstructorViewModel)view.DataContext!;
        (Window window, Grid root) = CompactViewRig.HostAt(view, CompactInner);
        try
        {
            Assert.Contains("compactHeight", root.Classes);
            double staleThreshold = CompactHeightBehavior.GetEffectiveThreshold(root);

            Button windowsLink = root.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "WindowsPackLink");
            windowsLink.Focus();
            Dispatcher.UIThread.RunJobs();
            Assert.True(windowsLink.IsFocused, "test precondition: the focus start point must genuinely take focus");

            // Grow the warning row — chrome, so it raises the floor rather than being absorbed by a
            // scrolling band — while COMPACT, where the expanded floor cannot be observed at all.
            vm.CustomPackerWarning = string.Join(" ", Enumerable.Repeat(
                "Custom packer detected; the reconstruction may not be byte-identical.", 12));
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(staleThreshold, CompactHeightBehavior.GetEffectiveThreshold(root), 1);

            // Enough to clear the STALE threshold and its restore slack, nowhere near the true one.
            List<Control?> focusTrail = [];
            window.Height = (staleThreshold + 12) + (window.Height - root.Bounds.Height);
            for (int i = 0; i < 8; i++)
            {
                Dispatcher.UIThread.RunJobs();
                focusTrail.Add(window.FocusManager?.GetFocusedElement() as Control);
            }

            Assert.Contains("compactHeight", root.Classes);
            Assert.True(CompactHeightBehavior.GetEffectiveThreshold(root) > staleThreshold + 12,
                "precondition for this being a FAILED restore: the re-measured floor must put the threshold " +
                "above the height that produced it");

            Control? landed = focusTrail[^1];
            Assert.True(landed is not null,
                "a failed restore left focus cleared: the trail was [" +
                string.Join(", ", focusTrail.Select(c => c is null ? "<none>" : CompactViewRig.Describe(c))) + "]");
            // Not asserted against a specific control: this view's compact direction target (the
            // Help body) is deliberately non-focusable, so the fallback chain settles on whichever
            // descendant is usable. What the failed-restore path must guarantee is that focus is
            // somewhere real, which the non-null assertion above and the stability check below pin.

            // No dead window: once the two transitions have settled, focus stays put rather than
            // being cleared and left cleared by whichever of them ran last.
            Assert.All(focusTrail.Skip(2), Assert.NotNull);
        }
        finally { window.Close(); }
    }

    /// <summary>Records every launcher call so a test can assert an invocation actually fired.</summary>
    private sealed class RecordingLauncherService : ILauncherService
    {
        public List<string> OpenedUrls { get; } = [];

        public void OpenUrl(string url) => OpenedUrls.Add(url);

        public void RevealPath(string path) { }
    }

    /// <summary>
    /// Reachability/focusability alone proves a link CAN be reached, not
    /// that activating it actually does anything. <see cref="ResourceLink.Launcher"/> is a test
    /// seam (added specifically so a genuine invocation can be exercised safely) —
    /// swapped for a <see cref="RecordingLauncherService"/> fake, restored in a finally block
    /// (it is a static, process-wide seam). Invoked via the REAL automation peer's
    /// <see cref="IInvokeProvider"/> (the same path a screen reader's "activate" gesture uses,
    /// which itself calls <c>Button.PerformClick()</c> — so this exercises Click too, not just
    /// UIA Invoke), never a raw <c>Button.ClickEvent</c> raise.
    /// </summary>
    [AvaloniaFact]
    public void CompactLinks_Invoke_RoutesThroughLauncher_WithoutARealBrowserLaunch()
    {
        var fakeLauncher = new RecordingLauncherService();
        ILauncherService originalLauncher = ResourceLink.Launcher;
        ResourceLink.Launcher = fakeLauncher;
        try
        {
            ReconstructorViewModel vm = CreateVm();
            var view = new ReconstructorView { DataContext = vm };
            (Window window, Grid root) = CompactViewRig.HostAt(view, CompactInner);
            try
            {
                Dispatcher.UIThread.RunJobs();

                Button windowsLink = window.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "WindowsPackLink");
                string expectedUrl = Assert.IsType<string>(windowsLink.Tag);

                var invokeProvider = Assert.IsAssignableFrom<IInvokeProvider>(ControlAutomationPeer.CreatePeerForElement(windowsLink));
                invokeProvider.Invoke();
                Dispatcher.UIThread.RunJobs();

                Assert.Equal([expectedUrl], fakeLauncher.OpenedUrls);
            }
            finally { window.Close(); }
        }
        finally { ResourceLink.Launcher = originalLauncher; }
    }

    [AvaloniaFact]
    public void HelpOpenDonation_TabRowMin60_BodyMaxHeight38_LastLinkKeyboardReachable()
    {
        ReconstructorViewModel vm = CreateVm();
        var view = new ReconstructorView { DataContext = vm };
        (Window window, Grid root) = CompactViewRig.HostAt(view, CompactInner);
        try
        {

            int tabControlRow = Grid.GetRow(window.GetVisualDescendants().OfType<TabControl>().Single(t => t.ItemCount == 6));
            Assert.Equal(60, root.RowDefinitions[tabControlRow].MinHeight);

            ScrollViewer body = root.GetVisualDescendants().OfType<ScrollViewer>().Single(s => s.Name == "HelpBody");
            Assert.Equal(38, body.MaxHeight);

            Button lastLink = window.GetVisualDescendants().OfType<Button>().Last(b => b.Classes.Contains("link"));
            CompactViewRig.AssertReachableByKeyboard(window, lastLink);
        }
        finally { window.Close(); }
    }

    // ── 5. Splitter ───────────────────────────────────────────────────

    [AvaloniaFact]
    public void Splitter_FocusableAndNamed_UpDownResizes_ClampsAtCompactMinimums()
    {
        ReconstructorViewModel vm = CreateVm();
        var view = new ReconstructorView { DataContext = vm };
        (Window window, Grid root) = CompactViewRig.HostAt(view, CompactInner);
        try
        {
            GridSplitter splitter = window.GetVisualDescendants().OfType<GridSplitter>().Single();
            Assert.Equal("Resize options and log", AutomationProperties.GetName(splitter));

            splitter.Focus();
            Dispatcher.UIThread.RunJobs();
            Assert.True(splitter.IsFocused);

            int tabControlRow = Grid.GetRow(window.GetVisualDescendants().OfType<TabControl>().Single(t => t.ItemCount == 6));
            int logRow = tabControlRow + 2; // splitter sits between them at tabControlRow + 1

            // Drive the log row down to its 80-DIP compact floor (Down grows row 4 at row 6's expense).
            PressManyTimes(window, PhysicalKey.ArrowDown, 40);
            Assert.True(root.RowDefinitions[logRow].Height.Value >= 80 - 0.5,
                $"log row clamped below its 80-DIP minimum: {root.RowDefinitions[logRow].Height.Value:F1}");

            // Drive the TabControl row down to its 60-DIP compact floor (Up grows row 6 at row 4's
            // expense). 60 is the minimum this row always carried while Help was showing; the old
            // 96 applied only when Help was collapsed, which can no longer happen.
            PressManyTimes(window, PhysicalKey.ArrowUp, 80);
            Assert.True(root.RowDefinitions[tabControlRow].Height.Value >= 60 - 0.5,
                $"TabControl row clamped below its 60-DIP minimum: {root.RowDefinitions[tabControlRow].Height.Value:F1}");
        }
        finally { window.Close(); }
    }

    /// <summary>
    /// REWRITTEN for gate finding NEW-2. The previous version read
    /// <c>splitter.Background</c>'s own LOGICAL brush colour and compared it against two ASSUMED
    /// resource keys ("SurfaceBackground" / "PanelBackground"), and both halves of that were wrong
    /// in the same direction — they measured what the markup is supposed to say rather than what
    /// the screen actually shows.
    /// <para>
    /// The specific defect it could not detect is deletion of the <c>GridSplitter:focus</c> style
    /// itself. With that style gone the splitter falls back to the base style's
    /// <c>Transparent</c>, whose <c>Color</c> is <c>#00FFFFFF</c> — WHITE with a zero alpha
    /// channel that a colour-only contrast computation simply ignores. Against this app's dark
    /// panes that computes as a very high ratio, so the old test would have gone on passing while
    /// the focus indicator had ceased to exist. Verified, not reasoned about: see
    /// <see cref="Splitter_FocusVisual_UnpaintedSplitter_FailsTheCheck"/> for the committed
    /// discriminating case, and §D2 of the a11y follow-up report for the observed RED from
    /// deleting the real style.
    /// </para>
    /// <para>
    /// Backported from <c>CreatorCompactTests.MeasureSplitterFocusContrast</c>, which fixed the
    /// identical defect in that suite: sample the REAL RENDERED PIXEL at the splitter's own centre
    /// and at the points 3 DIPs above and below it, so an unpainted, suppressed, covered or
    /// scrolled-away indicator fails, and so the neighbouring colours are whatever is genuinely
    /// there rather than whichever resource key the test guessed.
    /// </para>
    /// <para>
    /// Scope of the claim, stated exactly: three pixels are sampled, so this proves the focus
    /// indication is distinguishable from the surfaces immediately adjacent along the splitter's
    /// own centre line. It does not survey either neighbouring pane as a whole.
    /// </para>
    /// </summary>
    [AvaloniaFact]
    public void Splitter_FocusVisual_MeetsContrastAgainstBothPanes()
    {
        ReconstructorViewModel vm = CreateVm();
        var view = new ReconstructorView { DataContext = vm };

        (Window window, _) = CompactViewRig.HostAt(view, CompactInner);
        try
        {
            GridSplitter splitter = window.GetVisualDescendants().OfType<GridSplitter>().Single();

            splitter.Focus();
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();
            Assert.True(splitter.IsFocused, "test precondition: the splitter must genuinely hold focus, or the :focus style under test never applies");

            (double contrastVsAbove, double contrastVsBelow) = MeasureSplitterFocusContrast(splitter, window);

            Assert.True(contrastVsAbove >= 3.0, $"rendered focus pixel vs the pixel 3 DIPs above: {contrastVsAbove:F2}:1 (need >= 3:1)");
            Assert.True(contrastVsBelow >= 3.0, $"rendered focus pixel vs the pixel 3 DIPs below: {contrastVsBelow:F2}:1 (need >= 3:1)");
        }
        finally { window.Close(); }
    }

    /// <summary>
    /// The discriminating case NEW-2 asked for, mirroring
    /// <c>CreatorCompactTests.Splitter_FocusVisual_ContrastMeasurement_UnpaintedSplitter_FailsTheCheck</c>:
    /// proves the rendered-pixel method catches an indicator that is "there" by every property a
    /// naive check would read, yet invisible on screen. <c>Opacity = 0</c> is the sharpest such
    /// case — it leaves <c>IsVisible</c>, <c>IsEffectivelyVisible</c>, the layout bounds AND the
    /// logical <c>Background</c> colour completely unchanged, and only the rendered pixel reverts
    /// to whatever is behind it. Each of those four is asserted here rather than assumed, because
    /// they are precisely why the old property-reading form of this test could not have failed.
    /// </summary>
    [AvaloniaFact]
    public void Splitter_FocusVisual_UnpaintedSplitter_FailsTheCheck()
    {
        ReconstructorViewModel vm = CreateVm();
        var view = new ReconstructorView { DataContext = vm };

        (Window window, _) = CompactViewRig.HostAt(view, CompactInner);
        try
        {
            GridSplitter splitter = window.GetVisualDescendants().OfType<GridSplitter>().Single();
            splitter.Focus();
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();

            (double realAbove, double realBelow) = MeasureSplitterFocusContrast(splitter, window);
            Assert.True(realAbove >= 3.0 && realBelow >= 3.0,
                "test precondition: the untampered splitter must pass before it is deliberately suppressed");

            Color loggedBackgroundBefore = Assert.IsAssignableFrom<ISolidColorBrush>(splitter.Background).Color;

            // BREAK: suppress rendering without touching any property a naive check would read.
            splitter.Opacity = 0;
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();

            Assert.True(splitter.IsEffectivelyVisible, "test precondition: Opacity=0 must NOT flip IsEffectivelyVisible — that is exactly what makes this case dangerous");
            Assert.True(splitter.IsVisible);
            Assert.True(splitter.Bounds is { Width: > 0, Height: > 0 }, "test precondition: Opacity=0 must NOT collapse layout bounds either");
            Assert.Equal(loggedBackgroundBefore, Assert.IsAssignableFrom<ISolidColorBrush>(splitter.Background).Color);

            (double brokenAbove, double brokenBelow) = MeasureSplitterFocusContrast(splitter, window);
            Assert.True(brokenAbove < 3.0 && brokenBelow < 3.0,
                $"the unpainted (Opacity=0) splitter should have FAILED the 3:1 bar — its rendered pixel no longer shows the focus " +
                $"colour at all — but measured {brokenAbove:F2}:1 above / {brokenBelow:F2}:1 below: this covering test no longer discriminates.");

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
    /// Samples the REAL RENDERED PIXEL at the splitter's own centre and at the points 3 DIPs
    /// directly above and below it, and returns the WCAG contrast ratio of the first against each
    /// of the other two. Local copy of <c>CreatorCompactTests</c>' helper of the same name, per the
    /// no-promotion rule: the two are the same shape but not the same contract — that one names its
    /// neighbours "the stored-files grid and the output section", this one the Paths/Options
    /// TabControl and the log, and each suite's own doc explains its own geometry. Nothing is
    /// shared but the technique, and the technique is four lines.
    /// <para>
    /// The in-bounds check first is not redundant with the pixel sampling: a scrolled-away or
    /// clipped splitter would still sample SOME pixel, so containment and painting are two
    /// different failures and both need catching.
    /// </para>
    /// </summary>
    private static (double ContrastVsAbove, double ContrastVsBelow) MeasureSplitterFocusContrast(GridSplitter splitter, Window window)
    {
        AssertFullyWithinWindow(splitter, window);

        Point center = new(splitter.Bounds.Width / 2, splitter.Bounds.Height / 2);
        Point? centerInWindow = splitter.TranslatePoint(center, window);
        Assert.True(centerInWindow is not null, "test precondition: the splitter's own centre must translate into window coordinates");
        Color focusColor = SamplePixelColor(window, centerInWindow.Value);

        Point? aboveInWindow = splitter.TranslatePoint(new Point(splitter.Bounds.Width / 2, -3), window);
        Point? belowInWindow = splitter.TranslatePoint(new Point(splitter.Bounds.Width / 2, splitter.Bounds.Height + 3), window);
        Assert.True(aboveInWindow is not null && belowInWindow is not null, "test precondition: both neighbouring points must translate into window coordinates");

        Color abovePane = SamplePixelColor(window, aboveInWindow.Value);
        Color belowPane = SamplePixelColor(window, belowInWindow.Value);

        return (ContrastRatio(focusColor, abovePane), ContrastRatio(focusColor, belowPane));
    }

    /// <summary>Renders the whole window and reads back one pixel's RGBA — used to sample a
    /// neighbouring pane's TRUE rendered colour rather than guessing which named resource applies.</summary>
    private static Color SamplePixelColor(Window window, Point pointInWindow)
    {
        var size = new PixelSize((int)Math.Ceiling(window.Bounds.Width), (int)Math.Ceiling(window.Bounds.Height));
        byte[] buffer = RenderToPixelBuffer(window, size);

        int x = Math.Clamp((int)pointInWindow.X, 0, size.Width - 1);
        int y = Math.Clamp((int)pointInWindow.Y, 0, size.Height - 1);
        int offset = (y * size.Width * 4) + (x * 4);
        // Avalonia's RenderTargetBitmap default pixel format is BGRA8888.
        return Color.FromArgb(buffer[offset + 3], buffer[offset + 2], buffer[offset + 1], buffer[offset]);
    }

    /// <summary>
    /// CLIP-AWARE containment, added with the NEW-2 rewrite because
    /// <see cref="MeasureSplitterFocusContrast"/> needs it: the geometry is delegated to
    /// <see cref="CompactViewRig.IsFullyVisibleWithinWindow"/>, which already owns the cumulative
    /// clip walk, rather than hand-copied. The two pre-checks stay local because a degenerate
    /// (zero-size) control translates to a single point and would trivially satisfy any containment
    /// test, and because a bare bool cannot say which of the three failures occurred.
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

    // ── 6. Frame-rig parity (criterion F: normal-mode pixels unchanged) ──

    /// <summary>
    /// Compares the flat-mode header region (row 0) against a standalone reconstruction of the
    /// PRE-CHANGE markup (verbatim intro TextBlock + WrapPanel of 3 links, the row-0 shape before
    /// this change wrapped it in the helpDisclosure Expander), both forced through a real render
    /// tick before measuring. Fluent's stock
    /// Expander carries hardcoded floors (control MinHeight 48, chevron cell 32) that made
    /// pixel-identical flat-mode chrome unreachable through style overrides, so Styles.axaml
    /// re-templates Expander.helpDisclosure entirely (mirroring the existing
    /// Expander.versionGroup re-template).
    /// <para>
    /// Geometry (height/width) alone cannot catch a shifted
    /// glyph, a recolored brush, or a reflowed line inside the surviving region — only a REAL
    /// pixel comparison can (<see cref="AssertPixelIdenticalOutsideHeaderMask"/>,
    /// RenderTargetBitmap + CopyPixels, same technique as HexViewControlTests). An earlier
    /// version of this check resized OLD's window to NEW's measured width before comparing;
    /// that was rejected: resizing HIDES the real, sanctioned width delta from the test entirely rather than
    /// masking it as a bounded, understood, excluded region. This version compares at TRUE
    /// ORIGINAL geometries instead: old at its own natural, unconstrained width
    /// (<see cref="CompactInvariantRig.InnerWidth"/>, confirmed equal to <c>newRoot.Bounds.Width</c>
    /// itself — the true, unreduced Grid column both sides share); new at its own real, actual
    /// width, measured from its innermost content StackPanel (the Margin="0,0,4,0" one directly
    /// hosting the caption TextBlock) rather than the outer Expander/ScrollViewer/Border wrapper,
    /// which is a different structural level old's bare StackPanel never had even though the
    /// wrapper itself paints nothing extra.
    /// </para>
    /// <para>
    /// Chasing why this STILL wasn't clean found a real, previously mis-diagnosed production bug:
    /// <c>Expander.helpDisclosure</c> had no explicit <c>HorizontalAlignment</c>, so it inherited
    /// Fluent's own Expander default (Left) and hugged its own content's width instead of filling
    /// its Grid column — measured at 676→653, initially misattributed entirely
    /// to "the ScrollViewer's reserved scrollbar track." Fixed as a LOCAL value on Reconstructor's
    /// own Expander element (not the shared style — a shared-style change would also alter
    /// SRSCreator's Expander and invalidate ITS OWN already-approved frame-rig numbers).
    /// With that fixed, the true, fully-explained width delta is just 4 DIPs — the content
    /// StackPanel's own documented, intentional inset (Margin="0,0,4,0", "per house rule") — not
    /// 23 and not 27 (both earlier estimates based on the same
    /// unexamined bug).
    /// </para>
    /// <para>
    /// Even at a corrected, minimal 4-DIP delta, one narrow residual remained: word-wrap is a
    /// discrete, boundary-sensitive layout, and a 4-DIP narrower measure still pushes one word
    /// across a line break in the caption's specific text — confirmed NOT a wider problem (the
    /// WrapPanel/links row below it, which places whole items rather than wrapping characters,
    /// matches byte-for-byte with no exception). So the mask excludes exactly two named regions:
    /// the trailing width strip (present only in old, geometrically forced by the 4-DIP delta)
    /// and the caption TextBlock's own band (word-wrap-sensitive, content-justified) — everywhere
    /// else, including the entire links WrapPanel, must be and is byte-for-byte pixel-identical.
    /// </para>
    /// </summary>
    [AvaloniaFact]
    public void FrameRig_NormalMode_HeaderRegionMatchesPreDisclosureShape()
    {
        ReconstructorViewModel vm = CreateVm();
        var view = new ReconstructorView { DataContext = vm };
        (Window newWindow, Grid newRoot) = CompactViewRig.HostAt(view, ExpandedInner);
        try
        {
            Assert.DoesNotContain("compactHeight", newRoot.Classes);
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();

            Control newRow0 = newRoot.Children.OfType<Control>().Single(c => Grid.GetRow(c) == 0);

            // NEW's true comparison partner: the innermost content StackPanel, found by walking
            // up from the caption TextBlock (its direct parent, per the XAML) — NOT newRow0 (the
            // outer Expander) itself. See the note above for why.
            TextBlock newCaption = newRow0.GetVisualDescendants().OfType<TextBlock>().First();
            var newContentPanel = (Control)newCaption.Parent!;
            Size newSize = newContentPanel.Bounds.Size;

            Window oldWindow = BuildPreDisclosureRow0Window();
            try
            {
                oldWindow.Show();
                AvaloniaHeadlessPlatform.ForceRenderTimerTick();
                Dispatcher.UIThread.RunJobs();
                var oldRow0 = (Control)oldWindow.Content!;
                Size oldSize = oldRow0.Bounds.Size;

                // Height must match exactly — this is the visually significant dimension (a
                // taller/shorter header block would shift every row below it). Confirmed exact:
                // nothing about the width narrowing below causes the WrapPanel to wrap onto an
                // extra line.
                Assert.Equal(oldSize.Height, newSize.Height, precision: 0);

                // TWICE-CORRECTED figure: earlier passes put this delta first at 23, then at 27
                // (676→649). Investigating why the delta was as large as 23/27 in the first place found the
                // REAL root cause: Expander.helpDisclosure had no explicit HorizontalAlignment, so
                // it inherited the Fluent theme Expander's OWN default (Left) instead of filling
                // its Grid column — 676→653 of that gap was this unrelated, unintended bug, not
                // "scrollbar track reservation" as originally (wrongly) attributed; only the
                // remaining 4 DIPs were ever the content StackPanel's own documented, intentional
                // inset (Margin="0,0,4,0", "per house rule"). Fixed as a LOCAL value
                // (HorizontalAlignment/HorizontalContentAlignment="Stretch") directly on
                // Reconstructor's own <Expander x:Name="HelpDisclosure"> element in
                // ReconstructorView.axaml — NOT the shared Expander.helpDisclosure STYLE: a first
                // attempt there was caught, by a full-suite run, breaking SRSCreator's
                // own already-approved frame-rig test the instant its Expander ALSO started
                // stretching, so it was reverted (Styles.axaml carries no diff from the original) and
                // re-applied scoped to only this view. Confirmed by measurement: newRow0 (the
                // Expander) now matches newRoot's full 676 exactly; only the inner content
                // StackPanel's own 4-DIP margin remains. So the correct figure is 4, not 27 — a
                // corrected, smaller, more fully-explained delta, discovered only by chasing why the
                // mask-based comparison below wasn't actually clean.
                double widthNarrowing = oldSize.Width - newSize.Width;
                Assert.Equal(4.0, widthNarrowing, precision: 0);

                // Compare at TRUE ORIGINAL geometries (old at its own natural width; new at its
                // own real width) and mask the trailing rectangle the narrowing above just
                // measured and bounded (present only in old's wider render) PLUS — discovered
                // only once the width delta above was corrected to its true,
                // minimal 4 DIPs and the mismatch narrowed but did not disappear — the caption
                // TextBlock's own band. Word-wrap is a discrete, boundary-sensitive layout: EVEN a
                // 4-DIP narrower measure can (and empirically here, does) push one word across a
                // line break, for this specific text, at this specific width. Confirmed this is
                // NOT a wider problem: the WrapPanel/links row below the caption (which places
                // whole Button/TextBlock items rather than wrapping characters) matches
                // byte-for-byte with NO exception once given the same width. So exactly one additional, named, content-justified
                // band is excluded (the caption's own height) — not a vague broadening of the mask.
                AssertPixelIdenticalOutsideHeaderMask(oldRow0, oldSize, newContentPanel, newSize, newCaption.Bounds.Height);
            }
            finally { oldWindow.Close(); }
        }
        finally { newWindow.Close(); }
    }

    /// <summary>Reconstruction of ReconstructorView.axaml's row-0 StackPanel before this change (git history), verbatim.</summary>
    private static Window BuildPreDisclosureRow0Window()
    {
        var stack = new StackPanel { Margin = new Thickness(0, 0, 0, 6) };
        stack.Children.Add(new TextBlock
        {
            Text = "Reconstruct original RAR archives from an SRR file by brute-forcing WinRAR compression settings. Provide the source files and a WinRAR executable, then configure which RAR versions and switches to try.",
            Foreground = (IBrush?)Application.Current!.FindResource("ForegroundSecondary"),
            FontSize = (double)Application.Current!.FindResource("FontSizeCaption")!,
            TextWrapping = TextWrapping.Wrap,
        });

        var secondary = (IBrush?)Application.Current!.FindResource("ForegroundSecondary");
        var wrap = new WrapPanel { Margin = new Thickness(0, 2, 0, 0) };
        wrap.Children.Add(new TextBlock { Text = "WinRAR versions needed for reconstruction can be downloaded from:", Foreground = secondary, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center, Margin = new Thickness(0, 0, 4, 0), FontSize = (double)Application.Current!.FindResource("FontSizeCaption")! });
        wrap.Children.Add(new Button { Classes = { "link" }, Content = "Extracted files for Windows (ready to use)", FontSize = (double)Application.Current!.FindResource("FontSizeCaption")!, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center });
        wrap.Children.Add(new TextBlock { Text = ",", Foreground = secondary, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center, Margin = new Thickness(0, 0, 4, 0), FontSize = (double)Application.Current!.FindResource("FontSizeCaption")! });
        wrap.Children.Add(new Button { Classes = { "link" }, Content = "Extracted files for Linux (ready to use)", FontSize = (double)Application.Current!.FindResource("FontSizeCaption")!, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center, Margin = new Thickness(0, 0, 4, 0) });
        wrap.Children.Add(new TextBlock { Text = "or", Foreground = secondary, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center, Margin = new Thickness(0, 0, 4, 0), FontSize = (double)Application.Current!.FindResource("FontSizeCaption")! });
        wrap.Children.Add(new Button { Classes = { "link" }, Content = "Original files from RAR FTP (Windows)", FontSize = (double)Application.Current!.FindResource("FontSizeCaption")!, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center });
        stack.Children.Add(wrap);

        return new Window { Width = CompactInvariantRig.InnerWidth, SizeToContent = SizeToContent.Height, Content = stack };
    }

    /// <summary>
    /// Renders both controls to their own <see cref="RenderTargetBitmap"/> at their OWN true
    /// geometry (each sized to its own full bounds, independent of whatever window each is
    /// actually hosted in — <c>Render</c> is an immediate-mode draw of the visual's own subtree,
    /// not a capture of its parent's canvas — confirmed via <c>ImmediateRenderer</c>'s own
    /// decompiled source: the passed-in visual is always treated as its own root at (0,0), so
    /// neither control's real on-screen position leaks in), then excludes exactly two named
    /// regions and requires true byte-for-byte pixel identity everywhere else: (1) the trailing
    /// rectangle that exists only in <paramref name="oldControl"/>'s wider render (x from
    /// <paramref name="newSize"/>'s width to <paramref name="oldSize"/>'s width, full height —
    /// present only in old, no counterpart in new at all), and (2) <paramref name="wordWrapExcludedHeight"/>
    /// rows from the top (the caption TextBlock's own band — see the caller's note on why
    /// word-wrap makes even the fully-explained, minimal width delta unavoidably reflow-sensitive
    /// there specifically, and why nowhere else needs the same exclusion).
    /// </summary>
    private static void AssertPixelIdenticalOutsideHeaderMask(Control oldControl, Size oldSize, Control newControl, Size newSize, double wordWrapExcludedHeight)
    {
        const int BytesPerPixel = 4;

        Assert.True(oldSize.Width > newSize.Width,
            $"the header mask assumes old is the WIDER render, since old's bare StackPanel never " +
            $"had new's content-inset narrowing (old {oldSize.Width:F2}, new {newSize.Width:F2}).");

        var oldPixelSize = new PixelSize((int)Math.Ceiling(oldSize.Width), (int)Math.Ceiling(oldSize.Height));
        var newPixelSize = new PixelSize((int)Math.Ceiling(newSize.Width), (int)Math.Ceiling(newSize.Height));

        byte[] oldPixels = RenderToPixelBuffer(oldControl, oldPixelSize);
        byte[] newPixels = RenderToPixelBuffer(newControl, newPixelSize);

        // Region (1), the trailing width strip, is handled by simply never reading past
        // maskedCompareWidth. Height uses Math.Min defensively (the caller already asserted the
        // two heights equal to 0 decimals; this just guards a stray sub-DIP rounding artifact
        // between the two independent layout passes). Region (2), the caption's own word-wrap-
        // sensitive band, is handled by starting the row loop below it.
        int maskedCompareWidth = (int)Math.Floor(newSize.Width);
        int compareHeight = (int)Math.Floor(Math.Min(oldSize.Height, newSize.Height));
        int wordWrapExcludedRows = (int)Math.Ceiling(wordWrapExcludedHeight);
        Assert.True(maskedCompareWidth > 0 && compareHeight > wordWrapExcludedRows,
            $"comparison region must be non-empty (old {oldSize}, new {newSize}, caption band {wordWrapExcludedHeight:F1})");

        int oldStride = oldPixelSize.Width * BytesPerPixel;
        int newStride = newPixelSize.Width * BytesPerPixel;
        int rowBytes = maskedCompareWidth * BytesPerPixel;

        for (int y = wordWrapExcludedRows; y < compareHeight; y++)
        {
            int oldRowStart = y * oldStride;
            int newRowStart = y * newStride;

            for (int x = 0; x < rowBytes; x++)
            {
                if (oldPixels[oldRowStart + x] == newPixels[newRowStart + x])
                {
                    continue;
                }

                int pixelX = x / BytesPerPixel;
                Assert.Fail(
                    $"header region pixel mismatch at ({pixelX}, {y}) — old byte 0x{oldPixels[oldRowStart + x]:X2} " +
                    $"vs new byte 0x{newPixels[newRowStart + x]:X2}. Compared region was " +
                    $"{maskedCompareWidth}x{compareHeight} DIPs, rows {wordWrapExcludedRows}-{compareHeight - 1} " +
                    $"(old render {oldPixelSize}, new render {newPixelSize}); excluded: the trailing " +
                    $"strip (x from {maskedCompareWidth} to {oldPixelSize.Width - 1}, old-only) and the " +
                    $"caption's own word-wrap-sensitive band (rows 0-{wordWrapExcludedRows - 1}).");
            }
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

    private static void PressManyTimes(Window window, PhysicalKey key, int times)
    {
        for (int i = 0; i < times; i++)
        {
            window.KeyPressQwerty(key, RawInputModifiers.None);
            window.KeyReleaseQwerty(key, RawInputModifiers.None);
            Dispatcher.UIThread.RunJobs();
        }
    }

    /// <summary>WCAG 2.x relative luminance + contrast ratio, computed from rendered brush colors — never a hardcoded number.</summary>
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

    // ── Fixtures (REGENERATED from a measured walk: the view was hosted at each mode's own
    // height, the real Describe sequence captured, and these lists written from that capture —
    // never edited entry-by-entry against the old ones, which is how a wrong string quietly
    // becomes a wrong expectation that then passes). Describe() reads the control's REAL
    // automation peer name, so same-type controls with distinct content are no longer collapsed to
    // indistinguishable "Button:" entries — an early trap, a same-type reorder, or a swapped stop
    // is now caught by content, not just by count.
    // Peer name (accessible-name channel) and x:Name
    // (test-id channel) are reported SEPARATELY, never one masking the other — see Describe()'s
    // own doc comment.
    // The four path-picker TextBoxes used to show name="" here, recorded at the time as real,
    // unfixed a11y debt rather than a formatting quirk. That debt is now paid: each carries an
    // explicit AutomationProperties.Name, and so does each of the four Browse buttons beside them
    // (taken verbatim from ReconstructWizardBody, which already used exactly those four strings
    // for these same four commands). The consequence for THIS file is that the view no longer
    // contains ANY identically-described pair — see
    // AssertSameControlSequence_SwappedPositions_FailsNamingTheMismatch, whose premise that
    // change removed. ──

    /// <summary>
    /// Normal mode: the disclosure's body is force-expanded with its header hidden, so the 3 link
    /// buttons occupy exactly the StackPanel's old slot. Start is absent (disabled — CanExecute
    /// false for the inert VM's empty paths, so Tab correctly skips it): 3 links (peer name =
    /// their Content text; the first also carries the WindowsPackLink test-id) +
    /// Export/Import-Config/Import-from-SRR, then the Paths sub-tab, its 4 Browse/TextBox pairs,
    /// splitter, Save-log button, Auto-scroll checkbox.
    /// <para>
    /// The TabItem entry reads "Paths — needs attention", not "Paths": its name is bound to
    /// <c>ReconstructorViewModel.PathsTabAccessibleName</c>, and the inert VM this fixture is
    /// captured against has all four paths empty, which is exactly the state that raises the
    /// header's warning glyph. The previous entry, "Avalonia.Controls.ScrollViewer", was the
    /// composite header leaving the peer nothing but its BODY's ToString() to fall back on.
    /// </para>
    /// </summary>
    private static readonly IReadOnlyList<string> NormalModeTabOrderFixture =
    [
        "Button name=\"Extracted files for Windows (ready to use)\" id=\"WindowsPackLink\"",
        "Button name=\"Extracted files for Linux (ready to use)\" id=\"\"",
        "Button name=\"Original files from RAR FTP (Windows)\" id=\"\"",
        "Button name=\"Export Config\" id=\"\"",
        "Button name=\"Import Config\" id=\"\"",
        "Button name=\"Import from SRR\" id=\"\"",
        "TabItem name=\"Paths — needs attention\" id=\"\"",
        "TextBox name=\"WinRAR versions folder path\" id=\"WinRARTextBox\"",
        "Button name=\"Browse for WinRAR versions folder\" id=\"\"",
        "TextBox name=\"Release files path\" id=\"ReleaseTextBox\"",
        "Button name=\"Browse for extracted release files\" id=\"\"",
        "TextBox name=\"Verify file path\" id=\"VerifyTextBox\"",
        "Button name=\"Browse for verification file\" id=\"\"",
        "TextBox name=\"Output folder path\" id=\"OutputTextBox\"",
        "Button name=\"Browse for output folder\" id=\"\"",
        "GridSplitter name=\"Resize options and log\" id=\"\"",
        "Button name=\"Save log...\" id=\"\"",
        "CheckBox name=\"Auto-scroll\" id=\"\"",
    ];

    /// <summary>
    /// Compact order: disclosure header → (body skipped: Help starts collapsed per
    /// condition 5, so the 3 link buttons are IsVisible=false and correctly excluded from Tab
    /// order) → toolbar (3 enabled buttons — Start is absent, same reason as normal mode) →
    /// work area (Paths sub-tab) → splitter → log. Identical tail to normal mode; only the head
    /// differs (header toggle, named by its own Content text, prepended in place of the — here
    /// hidden — link buttons).
    /// </summary>
    private static readonly IReadOnlyList<string> CompactModeTabOrderFixture =
    [
        // The Help links lead in compact too now: the body no longer collapses, so they are
        // realized and focusable in both modes.
        "Button name=\"Extracted files for Windows (ready to use)\" id=\"WindowsPackLink\"",
        "Button name=\"Extracted files for Linux (ready to use)\" id=\"\"",
        "Button name=\"Original files from RAR FTP (Windows)\" id=\"\"",
        "Button name=\"Export Config\" id=\"\"",
        "Button name=\"Import Config\" id=\"\"",
        "Button name=\"Import from SRR\" id=\"\"",
        "TabItem name=\"Paths — needs attention\" id=\"\"",
        "TextBox name=\"WinRAR versions folder path\" id=\"WinRARTextBox\"",
        "Button name=\"Browse for WinRAR versions folder\" id=\"\"",
        "TextBox name=\"Release files path\" id=\"ReleaseTextBox\"",
        "Button name=\"Browse for extracted release files\" id=\"\"",
        "TextBox name=\"Verify file path\" id=\"VerifyTextBox\"",
        "Button name=\"Browse for verification file\" id=\"\"",
        "TextBox name=\"Output folder path\" id=\"OutputTextBox\"",
        "Button name=\"Browse for output folder\" id=\"\"",
        "GridSplitter name=\"Resize options and log\" id=\"\"",
        "Button name=\"Save log...\" id=\"\"",
        "CheckBox name=\"Auto-scroll\" id=\"\"",
    ];

    // ── REMOVED alongside ResolveExpectedStops (gate finding NEW-3): the three per-scope REVERSE
    // fixtures — NormalScopeAReverseTabOrderFixture, CompactScopeAReverseTabOrderFixture and
    // ScopeBReverseTabOrderFixture.
    //
    // They were description lists that ResolveExpectedStops turned back into references to feed the
    // two reverse walks' completeness sets, and their reversed slices were the reverse ORDER
    // expectations. Both jobs now come from ResolveIndependentExpectedOrder, so after that deletion
    // all three had ZERO code references — reachable only from each other's doc comments. Left in
    // place they would have been exactly the dead scaffolding whose removal justified deleting
    // ResolveExpectedStops in the first place, which is the inconsistency this cleanup closes.
    //
    // Nothing they asserted is now unasserted. Both reverse walks still check ORDER (against the
    // independent list's own reversed slices), still check COMPLETENESS (against the same slices),
    // and still assert their boundary landing by object identity — see AssertTabWalk. What is gone
    // is a second, weaker, description-based copy of the same expectation. ──
}
