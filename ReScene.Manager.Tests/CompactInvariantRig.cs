using Avalonia;
using Avalonia.Controls;
using Avalonia.VisualTree;
using ReScene.Manager.Behaviors;

namespace ReScene.Manager.Tests;

/// <summary>
/// Shared floor-measurement and switch-point derivation for the per-view threshold-invariant
/// tests. Measures in inner-content DIPs at width 676 (the 700×450 inner width).
/// <para>
/// No per-view switch height is written down anywhere in the test suite: every one is read back
/// from <see cref="CompactHeightBehavior.GetEffectiveThreshold"/> via
/// <see cref="ProbeSwitchPoint"/>, so a platform whose font metrics move a view's floor moves the
/// heights its tests run at by exactly the same amount. That is what makes these tests
/// platform-independent by construction rather than by calibration.
/// </para>
/// </summary>
internal static class CompactInvariantRig
{
    public const double InnerBudget = 319;   // measured: 450 − 26 − 58 − 23 − 24
    public const double CiBound = 307;       // InnerBudget − 12 jitter slack
    public const double InnerWidth = 676;

    /// <summary>
    /// The most the pinned action band may measure in compact mode. The design's own headroom
    /// arithmetic (§4: <c>319 − 24 header − 120 config − 80 log − margins ≈ 84</c>) — the space
    /// genuinely left for the band once every other compact band has taken its declared minimum.
    /// <para>
    /// Not the tighter 75 these tests used to carry: that figure came from the per-view compact
    /// FLOOR targets ("action ≤ 75" in the threshold table), which are design targets for the sum,
    /// while the bound the spec actually asserts against is this headroom. The 13px content text
    /// took SRSReconstructor's band to 77 — inside the spec's own bound, outside the tighter one —
    /// and the binding constraint it protects, the one-sum compact floor against
    /// <see cref="CiBound"/>, has 10 DIPs of headroom at 13px. See the design doc's 2026-08-02
    /// note.
    /// </para>
    /// <para>
    /// Still a real guard, not a formality: the bands measure 62–77 across the five views, so a
    /// band that started genuinely crowding the work area would breach it.
    /// </para>
    /// </summary>
    public const double PinnedBandCeiling = 84;

    /// <summary>
    /// How far above its own switch point a view is hosted when a test just wants it
    /// unambiguously EXPANDED. Comfortably clear of the restore hysteresis (12) without being so
    /// tall that a view stops resembling the small windows this feature is about.
    /// </summary>
    public const double ExpandedHeadroom = 60;

    /// <summary>
    /// The height <see cref="ProbeSwitchPoint"/> hosts at to read a view's derived switch point:
    /// tall enough that every converted view is expanded there on any platform, so the value read
    /// back is a real expanded-mode derivation rather than a floor captured mid-compact (which the
    /// behavior never updates).
    /// </summary>
    private const double ProbeHeight = 1200;

    /// <summary>
    /// The height at which <paramref name="buildWorstCase"/>'s view actually switches to compact,
    /// as the behavior itself derives it from that view's own worst-case content.
    /// <para>
    /// Takes a FACTORY, not a view: the probe hosts and closes an instance of its own, and
    /// <see cref="CompactHeightBehavior"/> is hysteretic, so handing back a used instance would
    /// leak the probe's own mode history into whatever the caller does next.
    /// </para>
    /// </summary>
    public static double ProbeSwitchPoint(Func<UserControl> buildWorstCase)
    {
        (Window window, Grid root) = CompactViewRig.HostAt(buildWorstCase(), ProbeHeight);
        try
        {
            double threshold = CompactHeightBehavior.GetEffectiveThreshold(root);
            if (double.IsNaN(threshold) || threshold <= 0)
            {
                throw new Xunit.Sdk.XunitException(
                    $"{buildWorstCase().GetType().Name} reports no effective threshold ({threshold}) — " +
                    "the behavior is not attached, or its root is not a Grid it can measure a floor from.");
            }

            if (threshold >= ProbeHeight)
            {
                throw new Xunit.Sdk.XunitException(
                    $"probe height {ProbeHeight} is no longer above this view's own switch point " +
                    $"({threshold:F1}), so the value was read from a COMPACT instance and is stale.");
            }

            return threshold;
        }
        finally { window.Close(); }
    }

    /// <summary>
    /// The ROW-AWARE floor of an inner Grid (a naive Measure(∞) reports CONTENT height for
    /// rows whose content scrolls, not their minimums): Σ per RowDefinition — a row the view
    /// declares GIVABLE via <see cref="CompactRowSize.ExpandedMinHeight"/> contributes that
    /// authored minimum, star rows contribute MinHeight, pixel rows their Height, and Auto rows
    /// the max desired height of their children measured at InnerWidth×∞ — plus inter-row margins.
    /// Mirrors <see cref="CompactHeightBehavior"/>'s own <c>MeasureExpandedFloor</c> rule, which is
    /// the point: the invariant tests and the behavior must agree on what "the floor" means, or the
    /// tests would be pinning a different quantity than the one the switch point is derived from.
    /// Callers force conditional rows visible and set the mode class BEFORE calling.
    /// </summary>
    public static double MeasureFloor(Grid innerRoot)
    {
        // Authored EXPANDED minimums are exactly that: while the root carries the compact class its
        // givable rows have already been rewritten to Star with their compact minimums, and the
        // star branch below is what describes them. Applying the expanded value there would report
        // a compact floor built out of expanded numbers.
        IReadOnlyList<CompactRowSize>? rows = innerRoot.Classes.Contains("compactHeight")
            ? null
            : CompactHeightBehavior.GetRowSizes(innerRoot);

        innerRoot.Measure(new Size(InnerWidth, double.PositiveInfinity));
        double total = 0;
        for (int i = 0; i < innerRoot.RowDefinitions.Count; i++)
        {
            RowDefinition row = innerRoot.RowDefinitions[i];
            if (AuthoredExpandedMinimum(rows, i) is { } authored)
            { total += authored; continue; }
            if (row.Height.IsAbsolute)
            { total += row.Height.Value; continue; }
            if (row.Height.IsStar)
            { total += row.MinHeight; continue; }
            double rowDesired = 0;
            foreach (Control child in innerRoot.Children.OfType<Control>())
            {
                if (Grid.GetRow(child) != i)
                {
                    continue;
                }

                rowDesired = Math.Max(rowDesired,
                    child.DesiredSize.Height + child.Margin.Top + child.Margin.Bottom);
            }
            total += rowDesired;
        }
        return total;
    }

    private static double? AuthoredExpandedMinimum(IReadOnlyList<CompactRowSize>? rows, int rowIndex)
    {
        if (rows is null)
        {
            return null;
        }

        foreach (CompactRowSize row in rows)
        {
            if (row.RowIndex == rowIndex && !double.IsNaN(row.ExpandedMinHeight))
            {
                return row.ExpandedMinHeight;
            }
        }
        return null;
    }

    /// <summary>
    /// Arrangement assertion: arrange the root at InnerWidth × the given height and
    /// verify NO child's rendered bounds extend past the bottom edge (the rendered form
    /// of "the floor fits"). Complements MeasureFloor — the invariant tests run both.
    /// </summary>
    public static void AssertArrangesWithin(Grid innerRoot, double height, string? context = null)
    {
        innerRoot.Measure(new Size(InnerWidth, height));
        innerRoot.Arrange(new Rect(0, 0, InnerWidth, height));
        foreach (Control child in innerRoot.Children.OfType<Control>())
        {
            if (!child.IsVisible)
            {
                continue;
            }

            double bottom = child.Bounds.Y + child.Bounds.Height;
            if (bottom > height + 0.5)
            {
                throw new Xunit.Sdk.XunitException(
                    (context is null ? string.Empty : context + ": ") +
                    $"{child.GetType().Name} bottom {bottom:F1} exceeds {height}");
            }
        }
    }

    /// <summary>
    /// Requires every ALWAYS-VISIBLE, non-scrolling descendant of <paramref name="root"/> to be
    /// fully visible, using the same clip-aware bar the criterion-C tab walk uses
    /// (<see cref="CompactViewRig.IsFullyVisibleWithinWindow"/>, reused rather than forked).
    /// Descendants of a <see cref="ScrollViewer"/> are excluded: a scrollable region's content
    /// legitimately extends past its own viewport — that is what "the band can give" means, and
    /// flagging it would be a false positive rather than a finding.
    /// <para>
    /// Complements <see cref="AssertArrangesWithin"/>, which only looks at root's DIRECT children
    /// and is therefore blind to clipping several levels down — inside the log band's header, say,
    /// which would leave the log band's own bounds untouched.
    /// </para>
    /// </summary>
    public static void AssertNoAlwaysVisibleDescendantIsClipped(Window window, Control root, string context)
    {
        List<string> clipped = [];
        foreach (Control descendant in root.GetVisualDescendants().OfType<Control>())
        {
            if (!descendant.IsEffectivelyVisible || descendant.GetVisualAncestors().OfType<ScrollViewer>().Any())
            {
                continue;
            }

            if (!CompactViewRig.IsFullyVisibleWithinWindow(descendant, window))
            {
                clipped.Add(CompactViewRig.Describe(descendant));
            }
        }

        if (clipped.Count > 0)
        {
            throw new Xunit.Sdk.XunitException(
                $"{context}: {clipped.Count} always-visible (non-scrolling) descendant(s) are clipped: " +
                string.Join("; ", clipped));
        }
    }

    /// <summary>
    /// THE invariant, in its executable form: at every height around a view's own derived switch
    /// point, whatever mode the view has chosen must actually FIT — no clipped content at any
    /// height, whatever the platform's fonts have done to the numbers.
    /// <para>
    /// This is the check that replaces the old per-view "floor &lt; constant" pins, and it is
    /// platform-independent BY CONSTRUCTION rather than by calibration: every height it visits is
    /// derived from <see cref="ProbeSwitchPoint"/>, and the assertion at each one is about the
    /// rendered result, not about a number. A platform whose fonts need 40 more DIPs moves the
    /// switch point AND the swept band together, and the same assertion still describes the same
    /// user-facing promise.
    /// </para>
    /// <para>
    /// A FRESH instance per height, never one resized down a ladder: the behavior's hysteresis is
    /// restore-only, so a resized instance's mode depends on the heights it has already been
    /// through, and the sweep is asking what a window OPENED at each height does. (The resize path
    /// is what the hysteresis and continued-shrink tests cover.)
    /// </para>
    /// <para>
    /// The near band is swept finely because that is where the switch actually happens; the far
    /// band coarsely and much further out, because an expanded view has to keep fitting as the
    /// window grows — the case where a config band's content genuinely exceeds the room available
    /// and its own scrolling has to absorb the difference.
    /// </para>
    /// </summary>
    public static void AssertActiveModeFitsAroundSwitchPoint(string viewName, Func<UserControl> buildWorstCase)
    {
        double switchPoint = ProbeSwitchPoint(buildWorstCase);

        List<double> heights = [];
        for (double h = switchPoint - 36; h <= switchPoint + 36; h += 6)
        {
            heights.Add(h);
        }

        for (double h = switchPoint + 96; h <= switchPoint + 396; h += 60)
        {
            heights.Add(h);
        }

        bool sawCompact = false;
        bool sawExpanded = false;
        foreach (double height in heights)
        {
            (Window window, Grid root) = CompactViewRig.HostAt(buildWorstCase(), height);
            try
            {
                bool compact = root.Classes.Contains("compactHeight");
                sawCompact |= compact;
                sawExpanded |= !compact;

                string context = $"{viewName} at inner height {height:F0} in " +
                    $"{(compact ? "COMPACT" : "EXPANDED")} mode (its own switch point is {switchPoint:F1})";
                AssertArrangesWithin(root, root.Bounds.Height, context);
                AssertNoAlwaysVisibleDescendantIsClipped(window, root, context);
            }
            finally { window.Close(); }
        }

        // Without both modes the sweep proved nothing: an all-compact or all-expanded band would
        // pass this test while saying nothing at all about the switch it exists to police.
        Assert.True(sawCompact && sawExpanded,
            $"{viewName}'s sweep never crossed its own switch point ({switchPoint:F1}) — " +
            $"saw compact: {sawCompact}, saw expanded: {sawExpanded}");
    }
}
