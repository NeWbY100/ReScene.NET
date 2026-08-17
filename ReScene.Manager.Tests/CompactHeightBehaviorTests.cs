using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using ReScene.Manager.Behaviors;

namespace ReScene.Manager.Tests;

/// <summary>
/// Contract tests for <see cref="CompactHeightBehavior"/>: threshold semantics
/// with restore-only hysteresis, ignored zero bounds, RowSizes application with
/// splitter-capture, help-open donation, class preservation, and staged focus.
/// </summary>
public class CompactHeightBehaviorTests
{
    private const double Threshold = 300;

    private static (Window Window, Grid Root) Host(double height, IReadOnlyList<CompactRowSize>? rows = null)
    {
        var root = new Grid
        {
            RowDefinitions = new RowDefinitions("Auto,150,*"),
        };
        root.Children.Add(new Border { Height = 40, [Grid.RowProperty] = 0 });
        root.Children.Add(new Border { [Grid.RowProperty] = 1 });
        root.Children.Add(new Border { [Grid.RowProperty] = 2 });
        CompactHeightBehavior.SetThreshold(root, Threshold);
        if (rows is not null)
        {
            CompactHeightBehavior.SetRowSizes(root, rows);
        }

        var window = new Window { Width = 700, Height = height, Content = root };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return (window, root);
    }

    [AvaloniaFact]
    public void FreshInstance_AtThresholdPlusOne_IsExpanded()
    {
        (Window w, Grid root) = Host(Threshold + 1);
        try
        {
            Assert.DoesNotContain("compactHeight", root.Classes);
        }
        finally { w.Close(); }
    }

    [AvaloniaFact]
    public void FreshInstance_BelowThreshold_IsCompact()
    {
        (Window w, Grid root) = Host(Threshold - 1);
        try
        {
            Assert.Contains("compactHeight", root.Classes);
        }
        finally { w.Close(); }
    }

    [AvaloniaFact]
    public void Hysteresis_RestoreOnlyAtThresholdPlus12()
    {
        (Window w, Grid root) = Host(Threshold - 1);
        try
        {
            Assert.Contains("compactHeight", root.Classes);

            w.Height = Threshold + 6;              // inside the hysteresis band
            Dispatcher.UIThread.RunJobs();
            Assert.Contains("compactHeight", root.Classes);

            w.Height = Threshold + 12;             // restore boundary
            Dispatcher.UIThread.RunJobs();
            Assert.DoesNotContain("compactHeight", root.Classes);
        }
        finally { w.Close(); }
    }

    [AvaloniaFact]
    public void RapidCrossings_EndStateWins_NoClassChurnLeftovers()
    {
        (Window w, Grid root) = Host(Threshold + 50);
        try
        {
            root.Classes.Add("keepMe");
            for (int i = 0; i < 6; i++)
            {
                w.Height = (i % 2 == 0) ? Threshold - 40 : Threshold + 40;
                Dispatcher.UIThread.RunJobs();
            }
            // Ended high (i=5 odd → +40, above restore boundary).
            Assert.DoesNotContain("compactHeight", root.Classes);
            Assert.Contains("keepMe", root.Classes);   // other classes never touched
        }
        finally { w.Close(); }
    }

    // ── Derived switch height ─────────────────────────────────────────────
    //
    // The tests above drive a root carrying an EXPLICIT Threshold, which the derived floor of that
    // rig (40 + 150 + 0 = 190, so 210 with the margin) never reaches — so they pin the explicit
    // path unchanged, exactly as they did before the switch height became derivable. The tests
    // below drive roots with NO explicit value at all, where the behavior's own measurement of the
    // view is the only thing deciding.

    /// <summary>Chrome row: fixed, non-givable, and therefore MEASURED into the floor.</summary>
    private const double DerivedChromeHeight = 40;

    /// <summary>The star row's authored minimum — givable, so this is all the floor owes it.</summary>
    private const double DerivedStarFloor = 60;

    /// <summary>
    /// A root whose switch height is entirely DERIVED: <c>Enabled</c> attaches it, no
    /// <c>Threshold</c> is set. Rows are chrome (Auto, fixed height) / body (Auto, caller-sized) /
    /// tail (Star with a minimum), which is the shape every converted view reduces to — something
    /// that must be shown whole, something whose size is the variable under test, and something
    /// that can give.
    /// </summary>
    private static (Window Window, Grid Root, Border Body) DerivedHost(
        double height, double bodyHeight, IReadOnlyList<CompactRowSize>? rows = null)
    {
        (Grid root, Border body) = BuildDerivedRoot(bodyHeight, rows);

        var window = new Window { Width = 700, Height = height, Content = root };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return (window, root, body);
    }

    /// <summary>The <see cref="DerivedHost"/> tree on its own, unattached — for tests that need to
    /// watch what happens as it is put into a live tree for the first time.</summary>
    private static (Grid Root, Border Body) BuildDerivedRoot(
        double bodyHeight, IReadOnlyList<CompactRowSize>? rows = null)
    {
        var root = new Grid { RowDefinitions = new RowDefinitions("Auto,Auto,*") };
        root.RowDefinitions[2].MinHeight = DerivedStarFloor;

        var body = new Border { Height = bodyHeight, [Grid.RowProperty] = 1 };
        root.Children.Add(new Border { Height = DerivedChromeHeight, [Grid.RowProperty] = 0 });
        root.Children.Add(body);
        root.Children.Add(new Border { [Grid.RowProperty] = 2 });

        if (rows is not null)
        {
            CompactHeightBehavior.SetRowSizes(root, rows);
        }

        CompactHeightBehavior.SetEnabled(root, true);
        return (root, body);
    }

    /// <summary>
    /// Every completed layout pass, as (height the pass gave the root, whether the compact class
    /// was on it at that moment). A frame is rendered from a completed layout pass, so a recorded
    /// pass with a real height and the wrong mode IS a frame the user can see in the wrong mode —
    /// which is what the flash tests below assert never happens.
    /// </summary>
    private static List<(double Height, bool Compact)> RecordLayoutPasses(Grid root)
    {
        List<(double Height, bool Compact)> passes = [];
        root.LayoutUpdated += (_, _) => passes.Add((root.Bounds.Height, root.Classes.Contains("compactHeight")));
        return passes;
    }

    private static void AssertNoPassShowedTheWrongMode(
        List<(double Height, bool Compact)> passes, bool expectedCompact, string context)
    {
        Assert.True(passes.Exists(p => p.Height > 0),
            $"{context}: no layout pass ever gave the root a height, so this proved nothing");

        List<(double Height, bool Compact)> wrong =
            [.. passes.Where(p => p.Height > 0 && p.Compact != expectedCompact)];

        Assert.True(wrong.Count == 0,
            $"{context}: {wrong.Count} of {passes.Count} layout passes were presentable frames in the " +
            $"WRONG mode (expected {(expectedCompact ? "compact" : "expanded")}) — at heights " +
            string.Join(", ", wrong.Select(p => p.Height.ToString("F0"))));
    }

    /// <summary>
    /// Sets the window height that puts the ROOT at <paramref name="innerHeight"/>, then settles.
    /// Drained repeatedly for the same reason <see cref="ShrinkTo"/> is: a height change can post
    /// work that itself posts more — here, the layout pass that follows a restore re-reads the
    /// floor and may queue another evaluation on the strength of it, and that second job is not in
    /// the queue when the first drain begins.
    /// </summary>
    private static void SetInnerHeight(Window window, Grid root, double innerHeight)
    {
        window.Height = innerHeight + (window.Height - root.Bounds.Height);
        for (int i = 0; i < 5; i++)
        {
            Dispatcher.UIThread.RunJobs();
        }
    }

    /// <summary>
    /// A view put into a live tree for the first time must reach its verdict in the SAME layout
    /// pass that first gives it a size — never one pass showing the expanded default at a height
    /// that calls for compact, which is a frame the user sees.
    /// <para>
    /// This is the tab-flash the derived model made structural. A posted evaluation cannot make
    /// that first frame: the first post arrives while the bounds are still zero and returns having
    /// done nothing, and the one after it runs only once the layout pass it is reacting to has
    /// already completed and been presented. The first bounds notification is the last moment at
    /// which the decision can still be part of the frame, so that is where it is now made.
    /// </para>
    /// </summary>
    [AvaloniaFact]
    public void FirstAttach_ReachesItsVerdictInTheSameLayoutPassThatFirstSizesTheView()
    {
        (Grid root, _) = BuildDerivedRoot(bodyHeight: 150);   // floor 250, so the switch is at 270
        List<(double Height, bool Compact)> passes = RecordLayoutPasses(root);

        var host = new Decorator();
        var window = new Window { Width = 700, Height = 200, Content = host };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        try
        {
            // The "click into the tab" moment: the root has never been laid out anywhere.
            host.Child = root;
            for (int i = 0; i < 5; i++)
            {
                Dispatcher.UIThread.RunJobs();
            }

            Assert.Contains("compactHeight", root.Classes);
            AssertNoPassShowedTheWrongMode(passes, expectedCompact: true, "first attach below the switch point");
        }
        finally { window.Close(); }
    }

    /// <summary>
    /// The same guarantee in the other direction, which the flash's own symmetry demands: a view
    /// whose remembered verdict is compact, re-attached into a window that has since grown, must
    /// not present a compact frame at the larger size either.
    /// <para>
    /// Restoring is the harder direction because the remembered floor is what the view measured
    /// before it went compact, and the expanded layout is not there to re-measure until it has been
    /// put back. It works because the floor SURVIVES the detach — see
    /// <see cref="RememberedVerdict_SurvivesDetachAndReattach"/> — so the switch height is known at
    /// the first bounds notification rather than one layout pass later.
    /// </para>
    /// </summary>
    [AvaloniaFact]
    public void Reattach_IntoAWindowThatGrew_RestoresWithoutPresentingACompactFrame()
    {
        (Grid root, _) = BuildDerivedRoot(bodyHeight: 150);   // switch at 270

        var host = new Decorator { Child = root };
        var window = new Window { Width = 700, Height = 200, Content = host };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        try
        {
            Assert.Contains("compactHeight", root.Classes);

            host.Child = null;                 // tab switched away
            Dispatcher.UIThread.RunJobs();

            window.Height = 600;               // ...and the window grew while it was away
            Dispatcher.UIThread.RunJobs();

            List<(double Height, bool Compact)> passes = RecordLayoutPasses(root);
            host.Child = root;                 // tab switched back
            for (int i = 0; i < 5; i++)
            {
                Dispatcher.UIThread.RunJobs();
            }

            Assert.DoesNotContain("compactHeight", root.Classes);
            AssertNoPassShowedTheWrongMode(passes, expectedCompact: false, "reattach into a grown window");
        }
        finally { window.Close(); }
    }

    /// <summary>
    /// And the converse: a remembered EXPANDED verdict re-attached into a window that has since
    /// shrunk must not present an expanded frame at the smaller size.
    /// </summary>
    [AvaloniaFact]
    public void Reattach_IntoAWindowThatShrank_CompactsWithoutPresentingAnExpandedFrame()
    {
        (Grid root, _) = BuildDerivedRoot(bodyHeight: 150);   // switch at 270

        var host = new Decorator { Child = root };
        var window = new Window { Width = 700, Height = 600, Content = host };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        try
        {
            Assert.DoesNotContain("compactHeight", root.Classes);

            host.Child = null;
            Dispatcher.UIThread.RunJobs();

            window.Height = 200;
            Dispatcher.UIThread.RunJobs();

            List<(double Height, bool Compact)> passes = RecordLayoutPasses(root);
            host.Child = root;
            for (int i = 0; i < 5; i++)
            {
                Dispatcher.UIThread.RunJobs();
            }

            Assert.Contains("compactHeight", root.Classes);
            AssertNoPassShowedTheWrongMode(passes, expectedCompact: true, "reattach into a shrunken window");
        }
        finally { window.Close(); }
    }

    /// <summary>
    /// Why the reattach cases above can decide immediately: the verdict and the measured floor
    /// outlive a detach. Per-control state lives in a <c>ConditionalWeakTable</c> keyed by the
    /// control and the style class sits on the control itself, so a view that leaves the tree and
    /// comes back is not starting over — it re-applies what it already knew and re-validates from
    /// there. Nothing extra is retained to make that true, which is why the detachment guarantees
    /// (no phantom root Tab stop, no recovery acting on a dead tree) are unaffected.
    /// </summary>
    [AvaloniaFact]
    public void RememberedVerdict_SurvivesDetachAndReattach()
    {
        (Grid root, _) = BuildDerivedRoot(bodyHeight: 150);

        var host = new Decorator { Child = root };
        var window = new Window { Width = 700, Height = 200, Content = host };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        try
        {
            double threshold = CompactHeightBehavior.GetEffectiveThreshold(root);
            Assert.Contains("compactHeight", root.Classes);

            host.Child = null;
            Dispatcher.UIThread.RunJobs();

            Assert.False(root.IsAttachedToVisualTree());
            Assert.Contains("compactHeight", root.Classes);
            Assert.Equal(threshold, CompactHeightBehavior.GetEffectiveThreshold(root), 1);
            Assert.False(root.Focusable, "a detached root must never keep transient focusability");
        }
        finally { window.Close(); }
    }

    /// <summary>
    /// The arithmetic the whole derived model rests on, stated once: the switch height is the
    /// view's own measured expanded floor plus the margin — chrome measured, givable rows counted
    /// at their minimums.
    /// </summary>
    [AvaloniaFact]
    public void DerivedThreshold_IsTheMeasuredFloorPlusMargin()
    {
        const double BodyHeight = 150;
        (Window w, Grid root, _) = DerivedHost(500, BodyHeight);
        try
        {
            double expectedFloor = DerivedChromeHeight + BodyHeight + DerivedStarFloor;
            Assert.Equal(expectedFloor + 20, CompactHeightBehavior.GetEffectiveThreshold(root), 1);
        }
        finally { w.Close(); }
    }

    /// <summary>
    /// The property that a per-view constant cannot have, and the reason this feature exists: give
    /// the view more content its layout cannot scroll away and the height it switches at rises to
    /// match — so a window that comfortably fitted the old content, and no longer fits the new,
    /// goes compact instead of showing the difference clipped.
    /// <para>
    /// This is also the shape of the CI failure that motivated the change: a platform whose font
    /// metrics make the same content measure taller is, to the behavior, indistinguishable from
    /// more content.
    /// </para>
    /// </summary>
    [AvaloniaFact]
    public void DerivedThreshold_TracksAGrowingFloor_WhereAConstantWouldNot()
    {
        (Window w, Grid root, Border body) = DerivedHost(400, bodyHeight: 150);
        try
        {
            double before = CompactHeightBehavior.GetEffectiveThreshold(root);
            Assert.DoesNotContain("compactHeight", root.Classes);

            body.Height = 300;
            Dispatcher.UIThread.RunJobs();

            double after = CompactHeightBehavior.GetEffectiveThreshold(root);
            Assert.Equal(before + 150, after, 1);
            Assert.Contains("compactHeight", root.Classes);   // 400 no longer clears the new floor
        }
        finally { w.Close(); }
    }

    /// <summary>
    /// An explicit <c>Threshold</c> is a MINIMUM, never a ceiling: below the view's own derived
    /// floor it simply does not bind. A caller cannot use it to hold a view expanded in a window
    /// its content does not fit — which is what would reintroduce the clipped band the invariant
    /// forbids.
    /// </summary>
    [AvaloniaFact]
    public void ExplicitThreshold_BelowTheDerivedFloor_IsOnlyAMinimum_AndDoesNotBind()
    {
        (Window w, Grid root, _) = DerivedHost(500, bodyHeight: 150);
        try
        {
            CompactHeightBehavior.SetThreshold(root, 100);
            Dispatcher.UIThread.RunJobs();

            Assert.Equal(DerivedChromeHeight + 150 + DerivedStarFloor + 20,
                CompactHeightBehavior.GetEffectiveThreshold(root), 1);
        }
        finally { w.Close(); }
    }

    /// <summary>
    /// The other half of "minimum": ABOVE the derived floor an explicit value still governs, so a
    /// view that wants to go compact earlier than its own content strictly requires can still say
    /// so. Nothing about the derivation takes that away.
    /// </summary>
    [AvaloniaFact]
    public void ExplicitThreshold_AboveTheDerivedFloor_StillGoverns()
    {
        (Window w, Grid root, _) = DerivedHost(600, bodyHeight: 150);
        try
        {
            CompactHeightBehavior.SetThreshold(root, 450);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(450, CompactHeightBehavior.GetEffectiveThreshold(root), 1);

            SetInnerHeight(w, root, 449);
            Assert.Contains("compactHeight", root.Classes);
        }
        finally { w.Close(); }
    }

    /// <summary>
    /// The givable-row rule, declared form: a row the view marks with an
    /// <see cref="CompactRowSize.ExpandedMinHeight"/> contributes that authored minimum however
    /// tall its content is, because the content scrolls and only the minimum is owed. Without this
    /// the floor of a band the view caps to the room available would chase the very height it is
    /// compared against, and no window would ever be tall enough.
    /// </summary>
    [AvaloniaFact]
    public void GivableRow_ContributesItsAuthoredExpandedMinimum_NotItsContentHeight()
    {
        const double AuthoredMinimum = 50;
        CompactRowSize[] rows =
        [
            new(RowIndex: 1, NormalHeight: double.NaN, CompactMinHeight: 20,
                Mode: CompactRowMode.AutoToStar, ExpandedMinHeight: AuthoredMinimum),
        ];

        (Window w, Grid root, Border body) = DerivedHost(500, bodyHeight: 150, rows);
        try
        {
            double expected = DerivedChromeHeight + AuthoredMinimum + DerivedStarFloor + 20;
            Assert.Equal(expected, CompactHeightBehavior.GetEffectiveThreshold(root), 1);

            // ...and it stays the authored value as the content grows, which is exactly what
            // "this band can give" means.
            body.Height = 400;
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(expected, CompactHeightBehavior.GetEffectiveThreshold(root), 1);
        }
        finally { w.Close(); }
    }

    /// <summary>
    /// The givable-row rule, structural form: a Star row gives by construction, so the floor owes
    /// it its MinHeight and nothing more, however tall the content inside it happens to measure.
    /// This is the rule the Reconstructor relies on — both its TabControl and its log band are
    /// Star rows with authored minimums in the XAML, so it needs no declaration at all.
    /// </summary>
    [AvaloniaFact]
    public void StarRow_ContributesItsMinimum_NotItsContentHeight()
    {
        (Window w, Grid root, _) = DerivedHost(500, bodyHeight: 150);
        try
        {
            double before = CompactHeightBehavior.GetEffectiveThreshold(root);

            root.Children.Add(new Border { Height = 300, [Grid.RowProperty] = 2 });
            Dispatcher.UIThread.RunJobs();

            Assert.Equal(before, CompactHeightBehavior.GetEffectiveThreshold(root), 1);
        }
        finally { w.Close(); }
    }

    /// <summary>
    /// Help donation is a COMPACT-mode mechanism and must not reach the expanded floor. Expanded
    /// mode renders the Help body flat and unconstrained — its largest state, already measured as
    /// chrome — so there is no donated budget to account for, and the floor must not quietly swap
    /// in a donation minimum just because <c>HelpOpen</c> happens to read true.
    /// </summary>
    [AvaloniaFact]
    public void CompactMinimums_NeverEnterTheExpandedFloor()
    {
        CompactRowSize[] rows =
        [
            new(RowIndex: 1, NormalHeight: double.NaN, CompactMinHeight: 20,
                Mode: CompactRowMode.AutoToStar, ExpandedMinHeight: 50),
        ];

        (Window w, Grid root, _) = DerivedHost(500, bodyHeight: 150, rows);
        try
        {
            double expanded = CompactHeightBehavior.GetEffectiveThreshold(root);

            // The compact minimum (20) is far below the expanded minimum (50) this row declares.
            // If the floor ever read the compact value, the threshold would drop.
            Dispatcher.UIThread.RunJobs();

            Assert.DoesNotContain("compactHeight", root.Classes);   // still expanded
            Assert.Equal(expanded, CompactHeightBehavior.GetEffectiveThreshold(root), 1);
        }
        finally { w.Close(); }
    }

    /// <summary>
    /// Hysteresis is a property of the SWITCH, not of a constant: it applies just the same when
    /// the switch height is derived. A fresh instance at the derived threshold is expanded; an
    /// already-compact one needs the derived threshold plus the restore slack to come back.
    /// </summary>
    [AvaloniaFact]
    public void DerivedThreshold_Hysteresis_RestoreOnlyAtDerivedPlusTwelve()
    {
        (Window w, Grid root, _) = DerivedHost(600, bodyHeight: 150);
        try
        {
            double threshold = CompactHeightBehavior.GetEffectiveThreshold(root);

            SetInnerHeight(w, root, threshold - 1);
            Assert.Contains("compactHeight", root.Classes);

            SetInnerHeight(w, root, threshold + 6);         // inside the hysteresis band
            Assert.Contains("compactHeight", root.Classes);

            SetInnerHeight(w, root, threshold + 12);        // restore boundary
            Assert.DoesNotContain("compactHeight", root.Classes);
        }
        finally { w.Close(); }
    }

    /// <summary>
    /// The anti-flap guarantee, which is what makes it safe for a restore to be re-validated at
    /// all. A floor that grew while the view was compact is invisible until the expanded layout is
    /// back — so a restore CAN turn out to be wrong. When it does, the behavior returns to compact
    /// and then RESTS: the newly-measured floor raises the threshold above the very height that
    /// produced the failed restore, so restoring again would need a strictly greater height. One
    /// flip, then still.
    /// </summary>
    [AvaloniaFact]
    public void RestoreThatNoLongerFits_ReturnsToCompact_AndThenRests()
    {
        (Window w, Grid root, Border body) = DerivedHost(600, bodyHeight: 150);
        try
        {
            double staleThreshold = CompactHeightBehavior.GetEffectiveThreshold(root);
            SetInnerHeight(w, root, staleThreshold - 1);
            Assert.Contains("compactHeight", root.Classes);

            // Content grows while compact, where the expanded floor cannot be observed at all.
            body.Height = 400;
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(staleThreshold, CompactHeightBehavior.GetEffectiveThreshold(root), 1);

            int classChanges = 0;
            root.Classes.CollectionChanged += (_, _) => classChanges++;

            // Enough to clear the STALE threshold and its restore slack, nowhere near the true one.
            SetInnerHeight(w, root, staleThreshold + 12);

            Assert.Contains("compactHeight", root.Classes);
            Assert.True(CompactHeightBehavior.GetEffectiveThreshold(root) > staleThreshold + 12,
                "the re-measured floor must put the threshold above the height that produced the failed restore — " +
                "that is what makes another restore attempt impossible rather than merely unlikely");

            // Exactly one round trip: the class came off for the restore and went straight back on.
            Assert.Equal(2, classChanges);

            // ...and it settles there. Further dispatcher turns must not produce another attempt.
            for (int i = 0; i < 5; i++)
            {
                Dispatcher.UIThread.RunJobs();
            }

            Assert.Contains("compactHeight", root.Classes);
            Assert.Equal(2, classChanges);
        }
        finally { w.Close(); }
    }

    /// <summary>
    /// The focus half of the failed-restore story, which
    /// <see cref="RestoreThatNoLongerFits_ReturnsToCompact_AndThenRests"/> deliberately says
    /// nothing about: it asserts where the CLASS ends up, and a run that ends compact can still
    /// have thrown keyboard focus away on the way there.
    /// <para>
    /// A failed restore is the one place two transitions land back to back with no user input in
    /// between, and the restore's own staged recovery is the only job holding a capture of what
    /// the restore hid. If the re-validation ran first it would hide the compact-only control the
    /// restore had just revealed, bump the generation so that recovery rejects itself as stale, and
    /// find nothing of its own to capture — focus cleared by the behavior and left cleared. So the
    /// recovery is queued first and this test is what says so.
    /// </para>
    /// <para>
    /// The landing asserted is specific: the wired <c>RestoreFocusTarget</c>. The restore's
    /// recovery relocates there because the compact-only holder went invisible; the re-compaction
    /// then captures it, finds it perfectly usable in compact too, and leaves it alone.
    /// <see cref="ReconstructorCompactTests"/> covers the other direction on the real view, where
    /// the restore target is itself unusable in compact and the chain carries on to the Help header
    /// toggle.
    /// </para>
    /// </summary>
    [AvaloniaFact]
    public void FailedRestore_NeverLeavesFocusCleared_AndLandsOnTheRestoreTarget()
    {
        // Four rows rather than the usual three: the restore target gets its own, ABOVE the
        // growable body. A grid whose floor exceeds its height overflows downwards, so a restore
        // target below the body would be clipped out at exactly the moment the failed restore needs
        // it and the chain would fall through to the root terminal — a valid landing, but not the
        // one the real views have, where the target sits in an always-visible band.
        var root = new Grid { RowDefinitions = new RowDefinitions("Auto,Auto,Auto,*") };
        root.RowDefinitions[3].MinHeight = DerivedStarFloor;

        // Sized under the chrome row's own height so its visibility never moves the floor — this
        // test is about focus, and a compact-only control that also changed the switch point would
        // make the sequence harder to read without testing anything more.
        var compactOnly = new Button { Content = "compact only", Height = 24, [Grid.RowProperty] = 0 };
        compactOnly.Classes.Add("compactOnly");
        var restoreTarget = new Button { Content = "restore target", [Grid.RowProperty] = 1 };
        var body = new Border { Height = 150, [Grid.RowProperty] = 2 };

        root.Children.Add(new Border { Height = DerivedChromeHeight, [Grid.RowProperty] = 0 });
        root.Children.Add(compactOnly);
        root.Children.Add(restoreTarget);
        root.Children.Add(body);
        root.Children.Add(new Border { [Grid.RowProperty] = 3 });

        CompactHeightBehavior.SetEnabled(root, true);
        CompactHeightBehavior.SetRestoreFocusTarget(root, restoreTarget);

        var window = new Window { Width = 700, Height = 600, Content = root };

        // The production pattern for a compact-only control: base style hides it, a class-scoped
        // style under the root's own compactHeight reveals it. Added before Show so the very first
        // evaluation sees the same rules every later one does.
        window.Styles.Add(new Style(x => x.OfType<Button>().Class("compactOnly"))
        {
            Setters = { new Setter(Visual.IsVisibleProperty, false) },
        });
        window.Styles.Add(new Style(x => x.OfType<Grid>().Class("compactHeight").Descendant().OfType<Button>().Class("compactOnly"))
        {
            Setters = { new Setter(Visual.IsVisibleProperty, true) },
        });

        window.Show();
        Dispatcher.UIThread.RunJobs();
        try
        {
            double staleThreshold = CompactHeightBehavior.GetEffectiveThreshold(root);
            SetInnerHeight(window, root, staleThreshold - 1);
            Assert.Contains("compactHeight", root.Classes);
            Assert.True(compactOnly.IsEffectivelyVisible, "test precondition: the compact-only control must be shown while compact");

            compactOnly.Focus();
            Dispatcher.UIThread.RunJobs();
            Assert.True(compactOnly.IsFocused, "test precondition: the compact-only control must genuinely take focus");

            // Content grows while compact, where the expanded floor cannot be observed at all.
            body.Height = 400;
            Dispatcher.UIThread.RunJobs();

            List<Control?> focusTrail = [];
            window.Height = (staleThreshold + 12) + (window.Height - root.Bounds.Height);
            for (int i = 0; i < 8; i++)
            {
                Dispatcher.UIThread.RunJobs();
                focusTrail.Add(window.FocusManager?.GetFocusedElement() as Control);
            }

            Assert.Contains("compactHeight", root.Classes);

            Control? landed = focusTrail[^1];
            Assert.True(landed is not null,
                "a failed restore left focus cleared: the trail was [" +
                string.Join(", ", focusTrail.Select(c => c is null ? "<none>" : c.GetType().Name)) + "]");
            Assert.True(ReferenceEquals(landed, restoreTarget),
                $"focus should have settled on the wired RestoreFocusTarget, not {landed.GetType().Name}");

            // No dead window: once both transitions have settled, focus stays put rather than
            // being cleared and left cleared by whichever of them ran last.
            Assert.All(focusTrail.Skip(2), Assert.NotNull);
        }
        finally { window.Close(); }
    }

    /// <summary>
    /// A fresh view that opens at normal height and is never resized must still end up with a
    /// floor that includes what flat mode reveals. The Help body is forced open by the very first
    /// evaluation, and its content only realizes in the layout pass AFTER that — a pass that
    /// raises no bounds change, because the root's height is the window's to decide. Reading the
    /// floor only at evaluation time would leave this instance quoting a floor measured without
    /// the body in it, for its whole life.
    /// </summary>
    [AvaloniaFact]
    public void FreshNormalInstance_FloorIncludesWhatTheFirstLayoutRevealed()
    {
        const double BodyHeight = 200;
        var root = new Grid { RowDefinitions = new RowDefinitions("Auto,Auto,*") };
        root.RowDefinitions[2].MinHeight = DerivedStarFloor;

        var expander = new Expander
        {
            [Grid.RowProperty] = 0,
            Content = new Border { Height = BodyHeight },
        };
        root.Children.Add(expander);
        root.Children.Add(new Border { Height = DerivedChromeHeight, [Grid.RowProperty] = 1 });
        root.Children.Add(new Border { [Grid.RowProperty] = 2 });

        CompactHeightBehavior.SetEnabled(root, true);

        var window = new Window { Width = 700, Height = 900, Content = root };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        try
        {
            Assert.DoesNotContain("compactHeight", root.Classes);   // never transitions
            Assert.True(expander.IsExpanded, "flat mode must force the Help body open");

            double threshold = CompactHeightBehavior.GetEffectiveThreshold(root);
            Assert.True(threshold > BodyHeight + DerivedChromeHeight + DerivedStarFloor,
                $"the derived threshold ({threshold:F1}) must account for the Help body the first " +
                $"layout revealed — a floor read only at evaluation time would omit its {BodyHeight} DIPs");
        }
        finally { window.Close(); }
    }

    /// <summary>
    /// "No opinion, do nothing": a root with no explicit minimum AND no floor the behavior can
    /// measure (it is not a Grid) must never touch the class, at any height. Silence is the
    /// correct answer there — inventing a switch point for a layout it cannot read would be worse
    /// than leaving it alone.
    /// </summary>
    [AvaloniaFact]
    public void NoExplicitThreshold_AndNoMeasurableFloor_LeavesTheViewAlone()
    {
        var root = new Border { Child = new TextBlock { Text = "not a grid" } };
        CompactHeightBehavior.SetEnabled(root, true);

        var window = new Window { Width = 700, Height = 900, Content = root };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        try
        {
            Assert.True(double.IsNaN(CompactHeightBehavior.GetEffectiveThreshold(root)));
            Assert.DoesNotContain("compactHeight", root.Classes);

            // Any real threshold would make THIS compact; nothing does.
            window.Height = 40;
            Dispatcher.UIThread.RunJobs();
            Assert.DoesNotContain("compactHeight", root.Classes);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void RowSizes_ApplyOnCompact_RestorePreservingSplitterDrag()
    {
        CompactRowSize[] rows = [new(RowIndex: 1, NormalHeight: 150, CompactMinHeight: 60, Mode: CompactRowMode.PixelRestore)];
        (Window w, Grid root) = Host(Threshold + 50, rows);
        try
        {
            // Simulate a user splitter drag at normal size.
            root.RowDefinitions[1].Height = new GridLength(190);
            Dispatcher.UIThread.RunJobs();

            w.Height = Threshold - 1;              // → compact
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(80, root.RowDefinitions[1].MinHeight);

            w.Height = Threshold + 12;             // → restore
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(190, root.RowDefinitions[1].Height.Value); // drag survives round-trip
            Assert.Equal(150, CompactHeightBehavior.GetRowSizes(root)![0].NormalHeight);
        }
        finally { w.Close(); }
    }

    [AvaloniaFact]
    public void AutoToStar_SwapsRowHeightKind_PerMode()
    {
        CompactRowSize[] rows = [new(1, double.NaN, 80, CompactRowMode.AutoToStar)];
        (Window w, Grid root) = Host(Threshold + 50, rows);
        try
        {
            root.RowDefinitions[1].Height = GridLength.Auto;   // three-band normal shape
            Dispatcher.UIThread.RunJobs();

            w.Height = Threshold - 1;                          // → compact
            Dispatcher.UIThread.RunJobs();
            Assert.True(root.RowDefinitions[1].Height.IsStar);
            Assert.Equal(110, root.RowDefinitions[1].MinHeight);

            w.Height = Threshold + 12;                         // → restore
            Dispatcher.UIThread.RunJobs();
            Assert.True(root.RowDefinitions[1].Height.IsAuto);
            Assert.Equal(0, root.RowDefinitions[1].MinHeight);
        }
        finally { w.Close(); }
    }

    [AvaloniaFact]
    public void DescendantGridRowSizes_FollowTheRootsMode()
    {
        (Window w, Grid root) = Host(Threshold + 50);
        try
        {
            var inner = new Grid { RowDefinitions = new RowDefinitions("150,Auto"), [Grid.RowProperty] = 2 };
            inner.Children.Add(new Border());
            CompactHeightBehavior.SetRowSizes(inner,
                [new CompactRowSize(0, 150, 80, CompactRowMode.PixelRestore)]);
            root.Children.Add(inner);
            Dispatcher.UIThread.RunJobs();

            w.Height = Threshold - 1;                          // root goes compact
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(80, inner.RowDefinitions[0].Height.Value);

            w.Height = Threshold + 12;
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(150, inner.RowDefinitions[0].Height.Value);
        }
        finally { w.Close(); }
    }

    /// <summary>
    /// The compact minimum applies for as long as the control is compact, with no second state to
    /// swap into. It used to be one of a PAIR — a larger value while Help was closed, a smaller
    /// donated one while it was open — and the behavior chose between them. Help is always showing
    /// now, so the donated value is simply the compact minimum.
    /// </summary>
    [AvaloniaFact]
    public void CompactMinimum_AppliesForAsLongAsTheControlIsCompact()
    {
        CompactRowSize[] rows = [new(1, 150, 60, CompactRowMode.MinOnly)];
        (Window w, Grid root) = Host(Threshold - 1, rows);
        try
        {
            Assert.Equal(60, root.RowDefinitions[1].MinHeight);

            // Restore: the row goes back to its authored minimum, not to a second compact value.
            root.GetVisualRoot();
            ((Window)root.GetVisualRoot()!).Height = Threshold + 200;
            Dispatcher.UIThread.RunJobs();
            Assert.DoesNotContain("compactHeight", root.Classes);
            Assert.Equal(0, root.RowDefinitions[1].MinHeight);
        }
        finally { w.Close(); }
    }

    [AvaloniaFact]
    public void FocusInsideCollapsingRegion_MovesToDesignatedTarget_OnCompactOnly()
    {
        (Window w, Grid root) = Host(Threshold + 50);
        try
        {
            // Direction-specific targets: compact target = the Help body; restore target = a
            // named normal-mode control.
            var helpBody = new Button { Content = "helpBody", [Grid.RowProperty] = 2 };
            var collapsing = new Button { Content = "link", [Grid.RowProperty] = 0 };
            var restoreTarget = new Button { Content = "firstInput", [Grid.RowProperty] = 1 };
            root.Children.Add(helpBody);
            root.Children.Add(collapsing);
            root.Children.Add(restoreTarget);
            CompactHeightBehavior.SetHelpBody(root, helpBody);
            CompactHeightBehavior.SetRestoreFocusTarget(root, restoreTarget);
            Dispatcher.UIThread.RunJobs();
            // The app-level styles hide row-0 content in compact AND drop the Help body's Tab stop
            // at normal size; the unit test simulates BOTH with the class (without the second
            // simulation the restore leg never strands focus and the assertion is vacuous):
            root.Classes.CollectionChanged += (_, _) =>
            {
                bool compact = root.Classes.Contains("compactHeight");
                collapsing.IsVisible = !compact;
                helpBody.IsVisible = compact;      // flat mode has no Help Tab stop
            };
            Dispatcher.UIThread.RunJobs();

            collapsing.Focus();
            Dispatcher.UIThread.RunJobs();
            Assert.True(collapsing.IsFocused);

            w.Height = Threshold - 1;              // → compact; collapsing hides
            Dispatcher.UIThread.RunJobs();
            Assert.True(helpBody.IsFocused,
                "focus must land on the compact direction target (the Help body)");

            w.Height = Threshold + 12;             // → restore; the Help body's Tab stop goes (flat mode)
            Dispatcher.UIThread.RunJobs();
            Assert.True(restoreTarget.IsFocused,
                "restore-direction stranding must land on the RestoreFocusTarget");
        }
        finally { w.Close(); }
    }

    [AvaloniaFact]
    public void FocusOutsideTheView_IsNeverStolen_ByTransitions()
    {
        // A transition while focus sits OUTSIDE the behavior's
        // root must not move it. The shell is a DockPanel with the root as FILL so the
        // root's height stays window-driven and the transitions genuinely fire
        // (a StackPanel rehost left the root content-sized and the test
        // could pass without any mode change ever happening).
        (Window w, Grid root) = Host(Threshold + 50);
        try
        {
            var outside = new Button { Content = "shell" };
            DockPanel.SetDock(outside, Dock.Top);
            var shell = new DockPanel();
            w.Content = null;
            shell.Children.Add(outside);
            shell.Children.Add(root);               // fill child: window-driven height
            w.Content = shell;
            Dispatcher.UIThread.RunJobs();

            outside.Focus();
            Dispatcher.UIThread.RunJobs();

            w.Height = Threshold - 1;              // → compact
            Dispatcher.UIThread.RunJobs();
            Assert.Contains("compactHeight", root.Classes);   // the transition REALLY ran
            Assert.True(outside.IsFocused, "transitions must never steal focus from outside the view");

            w.Height = Threshold + 40;             // → restore (past hysteresis)
            Dispatcher.UIThread.RunJobs();
            Assert.DoesNotContain("compactHeight", root.Classes);
            Assert.True(outside.IsFocused);
        }
        finally { w.Close(); }
    }

    [AvaloniaFact]
    public void ChainTerminal_RootGetsTransientFocusability()
    {
        // A view with NO focusable descendants forces the chain to its
        // terminal — the root itself, made focusable only for the hand-off.
        (Window w, Grid root) = Host(Threshold + 50);
        try
        {
            var collapsing = new Button { Content = "only", [Grid.RowProperty] = 0 };
            root.Children.Add(collapsing);
            root.Classes.CollectionChanged += (_, _) =>
                collapsing.IsVisible = !root.Classes.Contains("compactHeight");
            Dispatcher.UIThread.RunJobs();
            collapsing.Focus();
            Dispatcher.UIThread.RunJobs();

            w.Height = Threshold - 1;              // → compact; the ONLY focusable hides
            Dispatcher.UIThread.RunJobs();
            Assert.True(root.IsFocused, "the chain must terminate at the root");
            Assert.True(root.Focusable, "behavior grants transient focusability");

            var other = new Button { Content = "x", [Grid.RowProperty] = 2 };
            root.Children.Add(other);
            Dispatcher.UIThread.RunJobs();
            other.Focus();
            Dispatcher.UIThread.RunJobs();
            Assert.False(root.Focusable, "focusability is reset when the root loses focus");
        }
        finally { w.Close(); }
    }

    [AvaloniaFact]
    public void UnfocusableAfterRestore_Relocates_EvenThoughVisible()
    {
        // Restore leaves a compact-only-focusable element visible
        // but unfocusable — focus must move to the RestoreFocusTarget.
        (Window w, Grid root) = Host(Threshold - 1);
        try
        {
            var scroller = new ScrollViewer { [Grid.RowProperty] = 2, Focusable = true };
            var restoreTarget = new Button { Content = "input", [Grid.RowProperty] = 1 };
            root.Children.Add(scroller);
            root.Children.Add(restoreTarget);
            CompactHeightBehavior.SetRestoreFocusTarget(root, restoreTarget);
            root.Classes.CollectionChanged += (_, _) =>
                scroller.Focusable = root.Classes.Contains("compactHeight");
            Dispatcher.UIThread.RunJobs();

            scroller.Focus();
            Dispatcher.UIThread.RunJobs();
            Assert.True(scroller.IsFocused);

            w.Height = Threshold + 40;             // → restore; scroller stays visible
            Dispatcher.UIThread.RunJobs();
            Assert.True(restoreTarget.IsFocused,
                "an unfocusable focus-holder is stranding even when fully visible");
        }
        finally { w.Close(); }
    }

    [AvaloniaFact]
    public void ClippedButRecoverable_Focus_IsBroughtIntoView_NotRelocated()
    {
        // An element merely scrolled out of a viewport is recovered
        // via BringIntoView, never relocated.
        (Window w, Grid root) = Host(Threshold + 50);
        try
        {
            var scroller = new ScrollViewer { [Grid.RowProperty] = 2, Height = 60 };
            var stack = new StackPanel();
            for (int i = 0; i < 10; i++)
            {
                stack.Children.Add(new Button { Content = $"b{i}", Height = 30 });
            }

            scroller.Content = stack;
            root.Children.Add(scroller);
            Dispatcher.UIThread.RunJobs();

            var last = (Button)stack.Children[^1];
            last.Focus();
            scroller.Offset = default;             // scroll the focused button out of view
            Dispatcher.UIThread.RunJobs();

            w.Height = Threshold - 1;              // transition runs the obscurement check
            Dispatcher.UIThread.RunJobs();
            Assert.True(last.IsFocused, "recoverable focus must be brought into view, not relocated");
            Assert.True(scroller.Offset.Y > 0, "BringIntoView must have scrolled the viewer");
        }
        finally { w.Close(); }
    }

    [AvaloniaFact]
    public void Reattach_ReevaluatesFromCurrentBounds()
    {
        (Window w, Grid root) = Host(Threshold - 1);
        try
        {
            Assert.Contains("compactHeight", root.Classes);
            w.Content = null;                      // detach
            Dispatcher.UIThread.RunJobs();
            w.Height = Threshold + 50;
            w.Content = root;                      // reattach at a tall height
            Dispatcher.UIThread.RunJobs();
            Assert.DoesNotContain("compactHeight", root.Classes);
        }
        finally { w.Close(); }
    }

    // ── Focus-recovery regression coverage ────────────────────────────────

    /// <summary>
    /// The deferred (Loaded-priority) recovery job must reject itself once its
    /// premise is stale. This exercises the current-focus guard the most naturally
    /// constructible way: a "user" focus move happens SYNCHRONOUSLY within the very same
    /// transition that captured focus (via the compactHeight class-changed side effect, before
    /// the deferred job is even posted) — simulating focus moving on before the staged recovery
    /// gets its turn. The deferred job must never overwrite that later choice, and — proving it
    /// backed off before even resolving a target, not just coincidentally agreed with it — the
    /// fallback chain's own candidate must never end up focused either.
    /// </summary>
    [AvaloniaFact]
    public void StaleDeferredRecovery_FocusMovedAwayFromCaptured_IsNeverOverwritten()
    {
        (Window w, Grid root) = Host(Threshold + 50);
        try
        {
            var collapsing = new Button { Content = "link", [Grid.RowProperty] = 0 };
            var otherFallbackTarget = new Button { Content = "otherFallback", [Grid.RowProperty] = 1 };
            var elsewhere = new Button { Content = "elsewhere", [Grid.RowProperty] = 2 };
            root.Children.Add(collapsing);
            root.Children.Add(otherFallbackTarget);
            root.Children.Add(elsewhere);
            root.Classes.CollectionChanged += (_, _) =>
            {
                bool compact = root.Classes.Contains("compactHeight");
                collapsing.IsVisible = !compact;
                if (compact)
                {
                    elsewhere.Focus();   // simulated user focus move, synchronous with the transition
                }
            };
            Dispatcher.UIThread.RunJobs();

            collapsing.Focus();
            Dispatcher.UIThread.RunJobs();
            Assert.True(collapsing.IsFocused);

            w.Height = Threshold - 1;   // -> compact; collapsing hides AND focus moves to `elsewhere`
                                        // synchronously, before the deferred recovery job is posted
            Dispatcher.UIThread.RunJobs();

            Assert.True(elsewhere.IsFocused,
                "a focus change that happened before the deferred recovery runs must win");
            Assert.False(otherFallbackTarget.IsFocused,
                "the fallback chain must never even run once focus has already moved away from the captured element");
        }
        finally { w.Close(); }
    }

    /// <summary>
    /// A regression in the guard above: the current-focus guard must yield
    /// ONLY to a USABLE different focus. Here, focus moves from A to B synchronously within
    /// the same transition that captured A — but B is (already) permanently clipped by a plain
    /// ClipToBounds Border, not a ScrollViewer, so nothing ever answers BringIntoView and B stays
    /// obscured. B does NOT auto-clear from FocusManager the way an IsVisible=false element does
    /// (clipping is purely visual), so it is genuinely still "the current focus" when the
    /// deferred job runs — and unlike <see cref="StaleDeferredRecovery_FocusMovedAwayFromCaptured_IsNeverOverwritten"/>'s
    /// `elsewhere` (fully valid), B is itself broken. The recovery must not yield to it — it
    /// must relocate FROM B (not from the originally-captured A) to the direction target.
    /// </summary>
    [AvaloniaFact]
    public void CurrentFocusGuard_YieldsOnlyToUsableFocus_RecoversWhenNewFocusIsAlsoStranded()
    {
        (Window w, Grid root) = Host(Threshold - 1);
        try
        {
            var a = new Button { Content = "A", [Grid.RowProperty] = 0 };
            var clipper = new Border { [Grid.RowProperty] = 1, Height = 20, ClipToBounds = true };
            var bHost = new StackPanel();
            var b = new Button { Content = "B", Height = 30, Margin = new Thickness(0, 50, 0, 0) }; // permanently clipped
            bHost.Children.Add(b);
            clipper.Child = bHost;
            var restoreTarget = new Button { Content = "direction target", [Grid.RowProperty] = 2 };
            root.Children.Add(a);
            root.Children.Add(clipper);
            root.Children.Add(restoreTarget);
            CompactHeightBehavior.SetRestoreFocusTarget(root, restoreTarget);
            root.Classes.CollectionChanged += (_, _) =>
            {
                if (!root.Classes.Contains("compactHeight"))
                {
                    b.Focus();   // "user"/some code moves focus to the ALREADY-clipped B,
                                 // synchronously, within the same transition that captured A
                }
            };
            Dispatcher.UIThread.RunJobs();

            a.Focus();
            Dispatcher.UIThread.RunJobs();
            Assert.True(a.IsFocused);

            w.Height = Threshold + 40;   // -> restore; captures A; the handler above moves
                                         // focus to the obscured B before the deferred job runs
            Dispatcher.UIThread.RunJobs();

            Assert.True(restoreTarget.IsFocused,
                "B is a DIFFERENT, in-scope focus target, but it is itself obscured — the guard " +
                "must not yield to it, and recovery must still relocate to the direction target");
        }
        finally { w.Close(); }
    }

    /// <summary>
    /// A regression in the ResolveRecoveryTarget refactor above: the
    /// ENTRY-time current-focus check (before BringIntoView) was restored correctly there,
    /// but the POST-BringIntoView recheck regressed to generation/mode only, dropping the
    /// "did focus move to something else valid in the meantime" half of the guarantee
    /// established earlier. Here, captured is permanently clipped (nothing answers BringIntoView), so the
    /// obscured branch runs; a handler on captured's OWN RequestBringIntoViewEvent — which
    /// fires synchronously, DURING the BringIntoView() call itself — moves focus to a valid,
    /// unrelated element, simulating a user action racing the recovery attempt. The fallback
    /// chain must yield to it rather than overwrite it — proven discriminating by placing an
    /// EARLIER-in-tree-order, otherwise-eligible fallback candidate that the chain would have
    /// landed on instead, had it run at all.
    /// </summary>
    [AvaloniaFact]
    public void PostBringIntoView_FocusMovedToValidElement_FallbackChainYields()
    {
        (Window w, Grid root) = Host(Threshold + 50);
        try
        {
            var otherFallbackTarget = new Button { Content = "otherFallback", [Grid.RowProperty] = 0 };
            var validElsewhere = new Button { Content = "validElsewhere", [Grid.RowProperty] = 1 };
            var clipper = new Border { [Grid.RowProperty] = 2, Height = 20, ClipToBounds = true };
            var innerStack = new StackPanel();
            var captured = new Button { Content = "captured", Height = 30, Margin = new Thickness(0, 50, 0, 0) };
            innerStack.Children.Add(captured);
            clipper.Child = innerStack;
            root.Children.Add(otherFallbackTarget);
            root.Children.Add(validElsewhere);
            root.Children.Add(clipper);

            // Fires synchronously inside captured.BringIntoView(), simulating focus moving to
            // a valid element WHILE the staged recovery is in progress.
            captured.AddHandler(Control.RequestBringIntoViewEvent, (_, _) => validElsewhere.Focus());
            Dispatcher.UIThread.RunJobs();

            captured.Focus();
            Dispatcher.UIThread.RunJobs();
            Assert.True(captured.IsFocused);

            w.Height = Threshold - 1;   // no HelpExpander set: resolved target is null, so the
                                        // fallback chain (if it ran) would try otherFallbackTarget first
            Dispatcher.UIThread.RunJobs();

            Assert.True(validElsewhere.IsFocused,
                "focus that moved to a valid element during BringIntoView must not be overwritten");
            Assert.False(otherFallbackTarget.IsFocused,
                "the fallback chain must never even run once focus has already moved to something valid");
        }
        finally { w.Close(); }
    }

    /// <summary>
    /// An earlier version of this test was not
    /// discriminating: target sat wholly outside the outer viewport, which even the OLD,
    /// per-clipper-independent check already caught via its "vs outer" test alone, since that
    /// uses the fully-composed transform). THIS geometry is genuinely discriminating: target
    /// straddles the GAP between the two clippers' own ranges. Concretely, in inner-rendered
    /// coordinates: target spans 95..115; inner's own viewport is [0,100] (independently
    /// overlaps target at 95..100); outer's raw window, mapped into inner-rendered space, is
    /// [110,210] (independently overlaps target at 110..115). Each clipper independently finds
    /// SOME overlap with target — but in DISJOINT sub-ranges that share no point (95..100 vs
    /// 110..115, with a 100..110 gap between them), so no single point of target is ever
    /// actually visible through both at once: the true combined region (their intersection,
    /// empty here) excludes it entirely.
    /// It remains true that a SINGLE BringIntoView call cannot recover this shape: for the
    /// discriminating case to exist at all, target must extend beyond the INNER scroller's own
    /// range (if target were wholly within inner's own range, "vs outer independently passes"
    /// would force the combined intersection to include it too — algebraically, X⊆A and X∩B≠∅
    /// together imply X∩(A∩B)≠∅), so inner — always the first ancestor in the bubble path — is
    /// the one that adjusts, and having adjusted it sets e.Handled and the outer never sees
    /// request 1.
    /// An earlier version of this test concluded relocation was the
    /// only available end-state. That was an artifact of the implementation's one-attempt-per-
    /// target rule, not of Avalonia. A SECOND request finds inner already satisfied (it returns
    /// false, leaving e.Handled false) and therefore reaches the outer, which completes the
    /// recovery — so the correct end-state is that target KEEPS focus. The retry-on-progress
    /// rule is covered directly by
    /// <see cref="PartialInnerProgress_SecondRequestReachesOuter_TargetRecovered"/>; what THIS
    /// test still owns, and asserts below, is the DETECTION half.
    /// This test is verified to fail if IsObscured is reverted
    /// to the pre-fix, per-clipper-independent implementation (both
    /// independent checks pass, so IsObscured never even calls BringIntoView and neither offset
    /// nor focus ever changes); it passes with the cumulative-intersection implementation
    /// restored.
    /// </summary>
    [AvaloniaFact]
    public void NestedClippers_DisjointIndependentOverlaps_AreObscuredOnlyByTheCombinedCheck()
    {
        (Window w, Grid root) = Host(Threshold + 50);
        try
        {
            var outer = new ScrollViewer { [Grid.RowProperty] = 2, Height = 100 };
            var inner = new ScrollViewer { Height = 100 };
            var innerStack = new StackPanel();
            innerStack.Children.Add(new Border { Height = 95 });   // pushes target to inner-content-Y 95
            var target = new Button { Content = "target", Height = 20 };
            innerStack.Children.Add(target);
            inner.Content = innerStack;
            var outerStack = new StackPanel();
            outerStack.Children.Add(inner);                        // inner is outer's FIRST content: P=0
            outerStack.Children.Add(new Border { Height = 200 });   // gives outer room to scroll to 110
            outer.Content = outerStack;
            var fallbackTarget = new Button { Content = "fallback", [Grid.RowProperty] = 1 };
            root.Children.Add(fallbackTarget);
            root.Children.Add(outer);
            Dispatcher.UIThread.RunJobs();

            target.Focus();
            inner.Offset = default;                 // inner unscrolled: shows inner-rendered [0,100]
            outer.Offset = new Vector(0, 110);       // outer's raw window becomes inner-rendered
                                                     // [110,210] — independently overlaps target's
                                                     // [95,115] at [110,115], disjoint from inner's
                                                     // own overlap at [95,100] (a 100..110 gap
                                                     // separates the two independent overlaps)
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(0, inner.Offset.Y);
            Assert.Equal(110, outer.Offset.Y);

            w.Height = Threshold - 1;   // any transition runs the post-layout obscurement check
            Dispatcher.UIThread.RunJobs();

            Assert.True(inner.Offset.Y != 0,
                "inner attempted to bring target into its OWN view — proves BringIntoView ran, " +
                "which only happens if IsObscured's initial verdict was true (the old per-clipper " +
                "check would see no obscurement and never call it at all)");
            Assert.True(target.IsFocused,
                "detection triggered recovery, and recovery completes here (fix round 5): inner " +
                "consumed request 1, the retry reached outer, and target is visible through both");
            Assert.False(fallbackTarget.IsFocused,
                "recoverable focus is never relocated");
        }
        finally { w.Close(); }
    }

    /// <summary>
    /// Fallback candidates (entering direction) must be validated, not merely
    /// assumed usable. A Focusable=false descendant, an IsVisible=false descendant, and the
    /// captured element itself (clipped but otherwise Focusable/Enabled, so ONLY the explicit
    /// exclusion keeps it out) are all in the tree; none may be selected, and the chain must
    /// still reach the guaranteed root terminal rather than silently stopping.
    /// </summary>
    [AvaloniaFact]
    public void FallbackChain_EnteringDirection_NeverReselectsClippedCapture_ReachesRootTerminal()
    {
        (Window w, Grid root) = Host(Threshold + 50);
        try
        {
            var clipper = new Border { [Grid.RowProperty] = 2, Height = 20, ClipToBounds = true };
            var innerStack = new StackPanel();
            var captured = new Button { Content = "captured", Height = 30, Margin = new Thickness(0, 50, 0, 0) };
            innerStack.Children.Add(captured);
            clipper.Child = innerStack;

            var unfocusableDescendant = new Button { Content = "unfocusable", Focusable = false, [Grid.RowProperty] = 0 };
            var invisibleDescendant = new Button { Content = "invisible", IsVisible = false, [Grid.RowProperty] = 1 };
            root.Children.Add(unfocusableDescendant);
            root.Children.Add(invisibleDescendant);
            root.Children.Add(clipper);
            Dispatcher.UIThread.RunJobs();

            captured.Focus();
            Dispatcher.UIThread.RunJobs();
            Assert.True(captured.IsFocused);

            w.Height = Threshold - 1;   // no HelpExpander set: resolved target is null, walk begins immediately
            Dispatcher.UIThread.RunJobs();

            Assert.False(captured.IsFocused,
                "captured stays clipped (nothing answers BringIntoView here) and must never be reselected");
            Assert.False(unfocusableDescendant.IsFocused);
            Assert.False(invisibleDescendant.IsFocused);
            Assert.True(root.IsFocused, "every real candidate is unusable: the chain must reach the root terminal");
            Assert.True(root.Focusable, "behavior grants transient focusability at the terminal");
        }
        finally { w.Close(); }
    }

    /// <summary>
    /// The resolved direction target itself can be unusable (restore direction) —
    /// here, a RestoreFocusTarget that is referenced but was never attached to any tree at all.
    /// The chain must skip it (not silently end there) and still reach the root terminal, since
    /// every other real candidate is also unusable.
    /// </summary>
    [AvaloniaFact]
    public void FallbackChain_RestoreDirection_SkipsDetachedRestoreTarget_ReachesRootTerminal()
    {
        (Window w, Grid root) = Host(Threshold - 1);
        try
        {
            var scroller = new ScrollViewer { [Grid.RowProperty] = 2, Focusable = true };
            var unfocusableDescendant = new Button { Content = "unfocusable", Focusable = false, [Grid.RowProperty] = 1 };
            var detachedRestoreTarget = new Button { Content = "detached" }; // referenced but never attached
            root.Children.Add(unfocusableDescendant);
            root.Children.Add(scroller);
            CompactHeightBehavior.SetRestoreFocusTarget(root, detachedRestoreTarget);
            root.Classes.CollectionChanged += (_, _) =>
                scroller.Focusable = root.Classes.Contains("compactHeight");
            Dispatcher.UIThread.RunJobs();

            scroller.Focus();
            Dispatcher.UIThread.RunJobs();
            Assert.True(scroller.IsFocused);

            w.Height = Threshold + 40;   // -> restore: scroller becomes unfocusable (compact-only);
                                         // RestoreFocusTarget resolves to a DETACHED control
            Dispatcher.UIThread.RunJobs();

            Assert.False(unfocusableDescendant.IsFocused);
            Assert.True(root.IsFocused,
                "a detached RestoreFocusTarget must be skipped rather than silently ending the chain there");
            Assert.True(root.Focusable);
        }
        finally { w.Close(); }
    }

    /// <summary>
    /// <c>Generation++</c>'s placement (before the captured-null return)
    /// is correct, but the deferred job's lambda originally read <c>state.Generation</c> LIVE
    /// at run time instead of a value captured at post time — always comparing the live field
    /// to itself, never detecting staleness. Fixed by freezing it into a local before posting.
    /// A genuine two-real-transitions ABA race — where a second transition bumps the
    /// generation strictly BETWEEN the first transition's job being posted and that job
    /// running — is not constructible through the public API. Proven three independent ways:
    /// (1) QueueEvaluate's own coalescing (the updateQueued guard) allows at most one pending
    /// Evaluate at a time, so a second transition's Evaluate cannot even be queued while the
    /// first transition's Evaluate has not yet run.
    /// (2) Once posted, the deferred recovery job runs at Loaded priority (1) — HIGHER than
    /// the Default priority (0) that Evaluate itself (and thus any subsequent transition's
    /// Evaluate) runs at — so within one dispatcher drain, transition A's OWN recovery job is
    /// always serviced before a newly-queued transition B's Evaluate could run.
    /// (3) <c>Dispatcher.RunJobs(priority)</c> is an INCLUSIVE (>=) threshold over discrete,
    /// adjacent priority values (confirmed empirically: Default=0, Loaded=1,
    /// nothing between them), so there is no partial-drain call that lets Default-priority
    /// work run while withholding Loaded-priority work newly posted as a result of it.
    /// This test instead verifies the guarantee the fix actually provides, directly: the
    /// (reflection-reached) private <c>RelocateFocusIfNeeded</c> is invoked with a generation
    /// value that deliberately does not match the live <c>state.Generation</c> — exactly what
    /// a stale, frozen-at-post-time local would look like after a later transition bumped the
    /// live field — and must no-op, never reaching the fallback chain.
    /// </summary>
    [AvaloniaFact]
    public void StaleGeneration_DirectlyInjected_CausesTheDeferredJobToNoOp()
    {
        (Window w, Grid root) = Host(Threshold - 1);
        try
        {
            var collapsing = new Button { Content = "link", [Grid.RowProperty] = 0 };
            var restoreTarget = new Button { Content = "target", [Grid.RowProperty] = 1 };
            root.Children.Add(collapsing);
            root.Children.Add(restoreTarget);
            CompactHeightBehavior.SetRestoreFocusTarget(root, restoreTarget);
            root.Classes.CollectionChanged += (_, _) =>
                collapsing.IsVisible = !root.Classes.Contains("compactHeight");
            Dispatcher.UIThread.RunJobs();

            collapsing.Focus();
            Dispatcher.UIThread.RunJobs();
            Assert.True(collapsing.IsFocused);

            // Hide collapsing directly (what a real restore transition would do to it),
            // without going through a real transition, so RelocateFocusIfNeeded can be
            // invoked afterward with full control over its generation argument.
            collapsing.IsVisible = false;
            Dispatcher.UIThread.RunJobs();

            object state = GetPrivateState(root);
            int liveGeneration = GetGeneration(state);
            // enteringCompact must MATCH state.IsCompact (the root was hosted below the
            // threshold, so it is compact): an earlier version of this test had this argument `false` here,
            // which tripped IsSuperseded's MODE check first and made the generation argument
            // irrelevant — the test no-opped for the wrong reason. Matching the mode leaves the
            // deliberately-mismatched generation as the only thing that can reject the callback.
            InvokeRelocateFocusIfNeeded(root, collapsing, enteringCompact: true, liveGeneration + 1, state);
            Dispatcher.UIThread.RunJobs();

            Assert.False(restoreTarget.IsFocused,
                "a generation that does not match state.Generation must reject the callback " +
                "outright — the fallback chain must never run, so it must never reach the direction target");
        }
        finally { w.Close(); }
    }

    /// <summary>
    /// After the BringIntoView attempt, the recovery must re-run the
    /// FULL resolution — re-resolve what is focused NOW (yielding to a newer VALID focus,
    /// RETARGETING a newer in-scope-but-unusable one) and only THEN evaluate settledness.
    /// An earlier version checked settledness FIRST, so the one case where BringIntoView actually
    /// succeeds — the captured element ends up perfectly visible — returned before any
    /// re-resolution, stranding a control that the very same recovery attempt had just
    /// left focused and unusable. Here <c>captured</c> sits in a real ScrollViewer (so
    /// BringIntoView genuinely recovers it, asserted below) and its own
    /// RequestBringIntoView handler focuses <c>strandedNew</c>, permanently clipped by a
    /// plain ClipToBounds Border. Settled-first sees "captured is fine" and returns;
    /// resolve-first sees that focus now sits on a broken element and recovers THAT.
    /// </summary>
    [AvaloniaFact]
    public void PostBringIntoView_FocusMovedToUnusableElement_IsRecovered_NotStranded()
    {
        (Window w, Grid root) = Host(Threshold + 50);
        try
        {
            // First in tree order, so the fallback walk has a deterministic landing spot.
            var fallbackTarget = new Button { Content = "fallback", [Grid.RowProperty] = 0 };

            var clipper = new Border { [Grid.RowProperty] = 1, Height = 20, ClipToBounds = true };
            var clippedHost = new StackPanel();
            var strandedNew = new Button { Content = "strandedNew", Height = 30, Margin = new Thickness(0, 50, 0, 0) };
            clippedHost.Children.Add(strandedNew);
            clipper.Child = clippedHost;

            var scroller = new ScrollViewer { [Grid.RowProperty] = 2, Height = 60 };
            var stack = new StackPanel();
            for (int i = 0; i < 10; i++)
            {
                stack.Children.Add(new Button { Content = $"b{i}", Height = 30 });
            }

            scroller.Content = stack;

            root.Children.Add(fallbackTarget);
            root.Children.Add(clipper);
            root.Children.Add(scroller);
            Dispatcher.UIThread.RunJobs();

            var captured = (Button)stack.Children[^1];
            captured.Focus();
            scroller.Offset = default;             // scroll captured out of view
            Dispatcher.UIThread.RunJobs();
            Assert.True(captured.IsFocused);
            Assert.Equal(0, scroller.Offset.Y);

            // Registered only now: Focus() itself raises a bring-into-view request
            // (ScrollViewer.BringIntoViewOnFocusChange), which would fire this during setup.
            // Fires synchronously inside captured.BringIntoView(), BEFORE the scroller
            // handles the bubbling request and recovers captured.
            captured.AddHandler(Control.RequestBringIntoViewEvent, (_, _) => strandedNew.Focus());

            w.Height = Threshold - 1;              // transition runs the staged recovery
            Dispatcher.UIThread.RunJobs();

            Assert.True(scroller.Offset.Y > 0,
                "setup precondition: BringIntoView really did recover `captured`, so a " +
                "settledness-first ordering short-circuits right here");
            Assert.False(strandedNew.IsFocused,
                "the element focused DURING the recovery attempt is itself unusable — it must " +
                "not be left stranded just because the originally-captured element got settled");
            Assert.True(fallbackTarget.IsFocused,
                "re-resolution must retarget onto the newly-focused unusable element and relocate it");
        }
        finally { w.Close(); }
    }

    /// <summary>
    /// The OUTER-scroller recovery guarantee. Earlier versions kept
    /// dropping it, the last one claiming it was impossible for nested clippers; it is
    /// not. The geometry that makes it real is an OVERSIZED inner viewport: the inner
    /// scroller already shows the target in full, so it cannot improve anything —
    /// <c>ScrollContentPresenter.BringIntoViewRequested</c> sets
    /// <c>e.Handled = BringDescendantIntoView(...)</c>, and that returns false when no
    /// offset change is needed, so the request bubbles ON to the outer scroller, which is
    /// the only clipper that can clear the cumulative obscurity.
    /// Numbers (root space, after layout; row 2 starts at y=190): outer viewport
    /// [190,290]; outer scrolled to 160 puts inner's 200-tall viewport at [30,230] and the
    /// target at [80,180]. Cumulative visible region = [190,290] ∩ [30,230] = [190,230],
    /// which the target misses entirely → obscured. BringIntoView: inner sees the target
    /// at inner-content [50,150] inside its own [0,200] viewport → no change, unhandled;
    /// outer sees it at outer-content [50,150] against a [160,260] window → scrolls to 50.
    /// The target then lands at root [190,290], fully inside the cumulative region, so it
    /// is recovered and KEEPS focus rather than being relocated.
    /// </summary>
    [AvaloniaFact]
    public void NestedClippers_OnlyOuterCanRecover_BringIntoViewMovesOuter_TargetKeepsFocus()
    {
        (Window w, Grid root) = Host(Threshold + 50);
        try
        {
            var inner = new ScrollViewer { Height = 200 };
            var innerStack = new StackPanel();
            innerStack.Children.Add(new Border { Height = 50 });
            var target = new Button { Content = "target", Height = 100 };
            innerStack.Children.Add(target);
            inner.Content = innerStack;            // extent 150 < viewport 200: inner CANNOT scroll

            var outer = new ScrollViewer { [Grid.RowProperty] = 2, Height = 100 };
            var outerStack = new StackPanel();
            outerStack.Children.Add(inner);        // inner is outer's first content: outer-content Y 0
            outerStack.Children.Add(new Border { Height = 300 });   // scroll room for outer
            outer.Content = outerStack;
            root.Children.Add(outer);
            Dispatcher.UIThread.RunJobs();

            target.Focus();
            inner.Offset = default;
            outer.Offset = new Vector(0, 160);     // pushes target above outer's viewport
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(0, inner.Offset.Y);
            Assert.Equal(160, outer.Offset.Y);

            w.Height = Threshold - 1;              // any transition runs the obscurement check
            Dispatcher.UIThread.RunJobs();

            Assert.Equal(0, inner.Offset.Y);
            Assert.True(outer.Offset.Y < 160,
                "only the OUTER clipper can clear the cumulative obscurity here, so BringIntoView " +
                "must have moved the OUTER offset (the inner one already showed the target in full)");
            Assert.True(target.IsFocused,
                "outer-scroller recovery succeeded, so the target keeps focus and is never relocated");
        }
        finally { w.Close(); }
    }

    /// <summary>
    /// <see cref="StaleGeneration_DirectlyInjected_CausesTheDeferredJobToNoOp"/>
    /// only exercises the mismatch guard itself — it passes just as well against the live-capture
    /// form the fix replaced, so it never discriminated frozen from live lambda capture.
    /// This one does. It reaches the private callback FACTORY (which freezes state.Generation
    /// into a local at creation time), builds a callback, THEN bumps state.Generation behind its
    /// back — the "later transitions landed between post time and run time" window the freeze
    /// exists for — and only then runs it:
    /// <list type="bullet">
    /// <item>frozen capture: the callback still holds the pre-bump generation, sees the
    /// mismatch, and no-ops (GREEN);</item>
    /// <item>live capture (<c>() =&gt; Relocate(..., state.Generation, state)</c>): the field is
    /// read at RUN time, so it equals itself no matter how many transitions intervened, the
    /// guard can never fire, and the callback relocates focus (RED).</item>
    /// </list>
    /// The positive control at the end — the same scenario with a freshly built callback, whose
    /// frozen generation IS current, relocating exactly as expected — proves the no-op above
    /// came from the guard and not from a scenario that could never have relocated anything.
    /// </summary>
    [AvaloniaFact]
    public void FrozenGeneration_CallbackBuiltBeforeLaterTransitions_NoOps()
    {
        (Window w, Grid root) = Host(Threshold - 1);
        try
        {
            var collapsing = new Button { Content = "link", [Grid.RowProperty] = 0 };
            var fallbackCandidate = new Button { Content = "fallback", [Grid.RowProperty] = 1 };
            root.Children.Add(collapsing);
            root.Children.Add(fallbackCandidate);
            Dispatcher.UIThread.RunJobs();

            collapsing.Focus();
            Dispatcher.UIThread.RunJobs();
            Assert.True(collapsing.IsFocused);

            object state = GetPrivateState(root);
            // Built BEFORE the generation moves on — exactly what Evaluate does at post time.
            // enteringCompact matches state.IsCompact (hosted below the threshold), so the mode
            // half of IsSuperseded can never be what rejects this: only the generation can.
            Action callback = InvokeCreateRecoveryCallback(root, collapsing, enteringCompact: true, state);

            // Three later transitions' worth of bumps, landing strictly between the callback's
            // creation and its execution.
            SetGeneration(state, GetGeneration(state) + 3);

            collapsing.IsVisible = false;   // what such a later transition would do to it
            Dispatcher.UIThread.RunJobs();

            callback();
            Dispatcher.UIThread.RunJobs();
            Assert.False(fallbackCandidate.IsFocused,
                "the callback froze the pre-bump generation, so it must reject itself; a LIVE " +
                "read of state.Generation would equal itself here and relocate focus instead");

            InvokeCreateRecoveryCallback(root, collapsing, enteringCompact: true, state)();
            Dispatcher.UIThread.RunJobs();
            Assert.True(fallbackCandidate.IsFocused,
                "positive control: with a matching generation the very same scenario DOES " +
                "relocate — so the no-op above came from the guard, not from an inert scenario");
        }
        finally { w.Close(); }
    }

    /// <summary>
    /// One BringIntoView request per target is not enough. A scroller
    /// that PARTIALLY satisfies a request still consumes it — <c>ScrollContentPresenter</c>
    /// sets <c>e.Handled = BringDescendantIntoView(...)</c>, true whenever it moved — so the
    /// next scroller outward never sees request 1. An earlier one-attempt-per-target rule
    /// therefore relocated focus that a second request would have recovered.
    /// Geometry (the disjoint-overlap shape, whose target straddles the two clippers' gap):
    /// target at inner-content [95,115], inner viewport 100 at offset 0, outer viewport 100 at
    /// offset 110. Request 1: inner scrolls to 15 (bringing target into its OWN view) and
    /// consumes it — target is still cumulatively obscured, since outer's clip excludes it.
    /// Request 2: inner is now satisfied and returns false, so the request bubbles ON and outer
    /// scrolls 110 -> 80, putting target at root [190,210] inside the cumulative region
    /// [190,290] ∩ [110,210]. The loop must issue BOTH — asserted by counting the requests the
    /// target actually receives — and target must keep focus.
    /// </summary>
    [AvaloniaFact]
    public void PartialInnerProgress_SecondRequestReachesOuter_TargetRecovered()
    {
        (Window w, Grid root) = Host(Threshold + 50);
        try
        {
            var inner = new ScrollViewer { Height = 100 };
            var innerStack = new StackPanel();
            innerStack.Children.Add(new Border { Height = 95 });
            var target = new Button { Content = "target", Height = 20 };
            innerStack.Children.Add(target);
            inner.Content = innerStack;

            var outer = new ScrollViewer { [Grid.RowProperty] = 2, Height = 100 };
            var outerStack = new StackPanel();
            outerStack.Children.Add(inner);
            outerStack.Children.Add(new Border { Height = 200 });
            outer.Content = outerStack;

            var fallbackTarget = new Button { Content = "fallback", [Grid.RowProperty] = 1 };
            root.Children.Add(fallbackTarget);
            root.Children.Add(outer);
            Dispatcher.UIThread.RunJobs();

            target.Focus();
            inner.Offset = default;
            outer.Offset = new Vector(0, 110);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(0, inner.Offset.Y);
            Assert.Equal(110, outer.Offset.Y);

            // Attached only after the setup Focus(), which raises a request of its own.
            int requests = 0;
            target.AddHandler(Control.RequestBringIntoViewEvent, (_, _) => requests++);

            w.Height = Threshold - 1;
            Dispatcher.UIThread.RunJobs();

            Assert.Equal(2, requests);
            Assert.True(inner.Offset.Y != 0, "request 1 was partially consumed by inner");
            Assert.True(outer.Offset.Y < 110, "request 2 reached outer, which completed the recovery");
            Assert.True(target.IsFocused, "recoverable focus is brought into view across BOTH clippers, never relocated");
            Assert.False(fallbackTarget.IsFocused);
        }
        finally { w.Close(); }
    }

    /// <summary>
    /// The boundary of <c>MaxBringIntoViewAttempts</c>. With retry gated
    /// on progress, a well-behaved tree terminates on its own — every request either moves a
    /// scroller (and the next one starts from a strictly better position) or moves nothing and
    /// exhausts that target. The cap exists only for the pathological case this rig builds: a
    /// handler that fakes progress forever, nudging an ancestor scroller on every request while
    /// the target stays permanently obscured. Target sits at [25,55] inside a 20-tall
    /// ClipToBounds Border, so it is clipped away no matter what — yet it is within the outer
    /// ScrollViewer's own viewport, so the real BringIntoView never moves that scroller and the
    /// handler's 1px nudge is the sole (and monotone) source of "progress". The loop must stop
    /// at exactly the cap and fall through to relocation.
    /// </summary>
    [AvaloniaFact]
    public void FakedProgressForever_StopsAtTheCap_AndRelocates()
    {
        (Window w, Grid root) = Host(Threshold + 50);
        try
        {
            var clipper = new Border { Height = 20, ClipToBounds = true };
            var clippedHost = new StackPanel();
            var target = new Button { Content = "target", Height = 30, Margin = new Thickness(0, 25, 0, 0) };
            clippedHost.Children.Add(target);
            clipper.Child = clippedHost;

            var scroller = new ScrollViewer { [Grid.RowProperty] = 2, Height = 100 };
            var scrollerStack = new StackPanel();
            scrollerStack.Children.Add(clipper);
            scrollerStack.Children.Add(new Border { Height = 500 });   // genuine scroll room
            scroller.Content = scrollerStack;

            var fallbackTarget = new Button { Content = "fallback", [Grid.RowProperty] = 1 };
            root.Children.Add(fallbackTarget);
            root.Children.Add(scroller);
            Dispatcher.UIThread.RunJobs();

            target.Focus();
            scroller.Offset = default;
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(0, scroller.Offset.Y);

            int requests = 0;
            target.AddHandler(Control.RequestBringIntoViewEvent, (_, _) =>
            {
                requests++;
                scroller.Offset = new Vector(0, scroller.Offset.Y + 1);   // faked progress
            });

            w.Height = Threshold - 1;
            Dispatcher.UIThread.RunJobs();

            int cap = GetMaxBringIntoViewAttempts();
            Assert.Equal(8, cap);
            Assert.Equal(cap, requests);
            Assert.False(target.IsFocused, "the target never becomes visible, so it cannot keep focus");
            Assert.True(fallbackTarget.IsFocused, "hitting the cap falls through to relocation, never to a silent stop");
        }
        finally { w.Close(); }
    }

    /// <summary>
    /// A synchronous BringIntoView handler can CLEAR focus outright.
    /// The captured element is then recovered and looks perfectly settled — attached, visible,
    /// focusable — while NOTHING at all is focused, and the recovery would return leaving the
    /// window with empty focus (keyboard and screen-reader users stranded with no focus ring
    /// and no reachable starting point). A relocation this behavior initiated must never end
    /// that way: settled-but-nothing-focused hands off through the fallback chain, so the
    /// direction target — here the RestoreFocusTarget — ends focused.
    /// </summary>
    [AvaloniaFact]
    public void BringIntoViewHandlerClearedFocus_SettledButEmpty_HandsOffToDirectionTarget()
    {
        (Window w, Grid root) = Host(Threshold - 1);
        try
        {
            var restoreTarget = new Button { Content = "restoreTarget", [Grid.RowProperty] = 1 };
            var scroller = new ScrollViewer { [Grid.RowProperty] = 2, Height = 60 };
            var stack = new StackPanel();
            for (int i = 0; i < 10; i++)
            {
                stack.Children.Add(new Button { Content = $"b{i}", Height = 30 });
            }

            scroller.Content = stack;
            root.Children.Add(restoreTarget);
            root.Children.Add(scroller);
            CompactHeightBehavior.SetRestoreFocusTarget(root, restoreTarget);
            Dispatcher.UIThread.RunJobs();

            var captured = (Button)stack.Children[^1];
            captured.Focus();
            scroller.Offset = default;             // scroll captured out of view
            Dispatcher.UIThread.RunJobs();
            Assert.True(captured.IsFocused);

            // Attached after the setup Focus() (which raises a request of its own). Fires
            // synchronously inside captured.BringIntoView(), before the scroller recovers it.
            captured.AddHandler(Control.RequestBringIntoViewEvent,
                (_, _) => TopLevel.GetTopLevel(root)!.FocusManager!.ClearFocus());

            w.Height = Threshold + 40;             // -> restore; runs the staged recovery
            Dispatcher.UIThread.RunJobs();

            Assert.True(scroller.Offset.Y > 0,
                "setup precondition: BringIntoView DID recover captured, so it reads as settled");
            Assert.True(restoreTarget.IsFocused,
                "settled with nothing focused must hand off through the chain to the direction target");
        }
        finally { w.Close(); }
    }

    // ── Shared behavior: CONTINUED, NON-TRANSITIONAL resize ────────────────

    /// <summary>
    /// The staged-focus contract must hold under CONTINUED resize, not only at the instant of a
    /// mode transition. A view-level investigation reproduced the gap in production
    /// (CreatorView): crossing the threshold IS a transition, so the staged recovery correctly
    /// fired ONCE and scrolled the focused splitter back into its band's viewport — but every
    /// SUBSEQUENT shrink step (not a transition, so <c>Evaluate</c>'s
    /// <c>if (!isTransition &amp;&amp; state.Established) return;</c> skipped the entire staged
    /// sequence) shrank that same viewport around a FROZEN scroll offset until the still-focused
    /// splitter was clipped away again, with nothing left to notice.
    /// <para>
    /// This is the BEHAVIOR's own contract test for that (the view-level regression test proves
    /// the shipped view; this one proves the shared mechanism every converted view depends on).
    /// The sequence deliberately covers both legs of the spec's DELIBERATE ASYMMETRY rider:
    /// the first step shrinks by MORE than the target's own height (leaving it ENTIRELY outside
    /// the viewport — the WCAG 2.4.11 AA line <c>IsObscured</c> encodes) and the steps
    /// after the transition shrink by LESS than it (leaving it merely PARTIALLY clipped, which
    /// is NOT "obscured" by that definition and is covered directly by
    /// <see cref="ContinuedShrink_PartialClipOnly_IsScrolledFullyBackIntoView_FocusNeverMoves"/>).
    /// Focus must never MOVE at any step — every state here is recoverable by scrolling, and
    /// relocating a focus-holder the user can still reach would be focus theft, not a fix.
    /// </para>
    /// </summary>
    [AvaloniaFact]
    public void ContinuedShrinkPastTransition_KeepsFocusedElementFullyVisible_WithoutMovingFocus()
    {
        (Window w, Grid root) = Host(Threshold + 100);
        try
        {
            // No explicit Height: the viewport fills row 2 (star), so it SHRINKS with the window —
            // the production shape. A fixed-height scroller could never reproduce this at all.
            var scroller = new ScrollViewer { [Grid.RowProperty] = 2 };
            var stack = new StackPanel();
            for (int i = 0; i < 10; i++)
            {
                stack.Children.Add(new Button { Content = $"b{i}", Height = 30 });
            }

            scroller.Content = stack;
            root.Children.Add(scroller);
            Dispatcher.UIThread.RunJobs();

            var target = (Button)stack.Children[^1];
            target.Focus();                         // Focus() itself scrolls it into view
            Dispatcher.UIThread.RunJobs();
            Assert.True(target.IsFocused, "setup precondition: the target must genuinely take focus");
            AssertFullyVisible(target, "setup");
            Assert.True(scroller.Offset.Y > 0, "setup precondition: the content must genuinely overflow its viewport");

            // Threshold+100 -> Threshold+40: NOT a transition (still expanded), and a shrink
            // LARGER than the target's own 30 DIPs, so it lands entirely outside the viewport.
            // Then the threshold crossing itself (a real transition, which even the unfixed
            // behavior handles), then three more NON-transitional shrinks, each SMALLER than the
            // target's height so they clip it only partially.
            double[] innerSteps = [Threshold + 40, Threshold - 1, Threshold - 20, Threshold - 40, Threshold - 60];
            foreach (double inner in innerSteps)
            {
                ShrinkTo(w, root, inner);

                Assert.True(target.IsFocused,
                    $"at inner height {inner}, focus must still be on the target — a plain resize that " +
                    "MOVES focus is theft, not a recovery (every state in this sequence is scrollable-recoverable)");
                AssertFullyVisible(target, $"inner height {inner}");
            }

            Assert.Contains("compactHeight", root.Classes);   // the transition really did happen
        }
        finally { w.Close(); }
    }

    /// <summary>
    /// Isolates the PARTIAL-clip leg, which the sequence test above can only reach after its own
    /// first (entirely-obscured) step already failed — so on unfixed code that test proves nothing
    /// about this half. Here the ONLY shrink is deliberately SMALLER than the target's own height,
    /// and the resulting state is proven — not assumed — to be one the behavior's own
    /// <c>IsObscured</c> calls NOT obscured (it still intersects the viewport), by
    /// reproducing exactly that geometry through the scroll offset first and asking the private
    /// predicate directly. A fix that only handled entire obscurement would therefore leave this
    /// state untouched, with a focused control hanging past its viewport for the rest of the drag.
    /// The spec's own rider covers precisely this: "C's Tab walk lets BringIntoView resolve
    /// partial clipping first; the relocation threshold only catches what scrolling cannot
    /// recover" — so partial clipping is scrolled away, and focus is NEVER relocated for it.
    /// </summary>
    [AvaloniaFact]
    public void ContinuedShrink_PartialClipOnly_IsScrolledFullyBackIntoView_FocusNeverMoves()
    {
        (Window w, Grid root) = Host(Threshold + 100);
        try
        {
            var scroller = new ScrollViewer { [Grid.RowProperty] = 2 };
            var stack = new StackPanel();
            for (int i = 0; i < 10; i++)
            {
                stack.Children.Add(new Button { Content = $"b{i}", Height = 30 });
            }

            scroller.Content = stack;
            var fallbackCandidate = new Button { Content = "fallback", [Grid.RowProperty] = 1 };
            root.Children.Add(fallbackCandidate);
            root.Children.Add(scroller);
            Dispatcher.UIThread.RunJobs();

            var target = (Button)stack.Children[^1];
            target.Focus();
            Dispatcher.UIThread.RunJobs();
            Assert.True(target.IsFocused);
            AssertFullyVisible(target, "setup");

            const double ShrinkBy = 10;
            Assert.True(ShrinkBy < target.Bounds.Height,
                $"setup precondition: the shrink ({ShrinkBy}) must be smaller than the target's own height " +
                $"({target.Bounds.Height}), so the resulting state is PARTIALLY clipped, not entirely obscured");

            // Reproduce that exact partial state through the offset (which nothing in the behavior
            // watches, so no evaluation runs) and ask the behavior's OWN predicate: it is not
            // "obscured". Proven, not assumed — this is what makes the stricter rule necessary.
            double settled = scroller.Offset.Y;
            scroller.Offset = new Vector(0, settled - ShrinkBy);
            Dispatcher.UIThread.RunJobs();
            Assert.False(InvokeIsObscured(target),
                "a target hanging past its viewport by less than its own height still INTERSECTS it — " +
                "IsObscured (the AA line) says not obscured, so only the stricter fully-visible rule can catch it");
            scroller.Offset = new Vector(0, settled);
            Dispatcher.UIThread.RunJobs();
            AssertFullyVisible(target, "after restoring the settled offset");

            ShrinkTo(w, root, Threshold + 100 - ShrinkBy);

            Assert.DoesNotContain("compactHeight", root.Classes); // no mode change: purely a within-mode resize
            Assert.True(target.IsFocused,
                "a merely partially-clipped focus-holder is still reachable — it must be scrolled back " +
                "into view, never relocated");
            Assert.False(fallbackCandidate.IsFocused, "the fallback chain must not run for a partial clip");
            AssertFullyVisible(target, "after a shrink smaller than the target's own height");
            Assert.True(scroller.Offset.Y > settled, "the recovery must have scrolled, not merely re-checked");
        }
        finally { w.Close(); }
    }

    /// <summary>
    /// The no-focus-theft precondition applies to the resize recheck exactly as it
    /// does to transitions: with focus OUTSIDE this root, a resize must cost nothing and can never
    /// pull focus in. Made discriminating by giving the outside element a genuinely broken state —
    /// permanently clipped by a plain ClipToBounds Border, so nothing answers BringIntoView — that
    /// a scope-blind recheck (one asking only "is the focused element obscured?") would try to
    /// recover and, failing, would hand to the fallback chain, dragging focus into this view.
    /// </summary>
    [AvaloniaFact]
    public void NonTransitionalResize_FocusOutsideTheRoot_IsNeverPulledIn()
    {
        (Window w, Grid root) = Host(Threshold + 100);
        try
        {
            var outsideClipper = new Border { Height = 20, ClipToBounds = true };
            var outsideHost = new StackPanel();
            var outside = new Button { Content = "shell", Height = 30, Margin = new Thickness(0, 50, 0, 0) };
            outsideHost.Children.Add(outside);
            outsideClipper.Child = outsideHost;
            DockPanel.SetDock(outsideClipper, Dock.Top);

            var insideCandidate = new Button { Content = "inside", [Grid.RowProperty] = 1 };
            root.Children.Add(insideCandidate);

            var shell = new DockPanel();
            w.Content = null;
            shell.Children.Add(outsideClipper);
            shell.Children.Add(root);              // fill child: the root's height stays window-driven
            w.Content = shell;
            Dispatcher.UIThread.RunJobs();

            outside.Focus();
            Dispatcher.UIThread.RunJobs();
            Assert.True(outside.IsFocused);
            Assert.True(InvokeIsObscured(outside),
                "setup precondition: the outside focus-holder must be genuinely broken, so a scope-blind " +
                "recheck would have something to (fail to) recover and would fall through to relocation");

            ShrinkTo(w, root, Threshold + 60);

            Assert.DoesNotContain("compactHeight", root.Classes); // no transition: the recheck path is what ran
            Assert.True(outside.IsFocused, "a resize must never pull focus out of the shell and into the view");
            Assert.False(insideCandidate.IsFocused);
            Assert.False(root.IsFocused);
            Assert.False(root.Focusable, "the chain's root terminal must never have been reached");
        }
        finally { w.Close(); }
    }

    /// <summary>
    /// Frozen-generation discipline on the NEW path, mirroring
    /// <see cref="FrozenGeneration_CallbackBuiltBeforeLaterTransitions_NoOps"/>: the resize
    /// recheck's callback factory freezes <c>state.Generation</c> at creation time, so a recheck
    /// posted before a later transition must reject itself rather than do stale work on top of a
    /// newer apply. The positive control at the end — the same scenario with a freshly built
    /// callback whose frozen generation IS current — proves the no-op came from the guard and not
    /// from a scenario that could never have recovered anything.
    /// </summary>
    [AvaloniaFact]
    public void ResizeRecheck_FrozenGeneration_CallbackBuiltBeforeALaterTransition_NoOps()
    {
        (Window w, Grid root) = Host(Threshold + 100);
        try
        {
            var scroller = new ScrollViewer { [Grid.RowProperty] = 2 };
            var stack = new StackPanel();
            for (int i = 0; i < 10; i++)
            {
                stack.Children.Add(new Button { Content = $"b{i}", Height = 30 });
            }

            scroller.Content = stack;
            root.Children.Add(scroller);
            Dispatcher.UIThread.RunJobs();

            var target = (Button)stack.Children[^1];
            target.Focus();
            scroller.Offset = default;             // scroll the focused target entirely out of view
            Dispatcher.UIThread.RunJobs();
            Assert.True(target.IsFocused);
            Assert.True(InvokeIsObscured(target), "setup precondition: the target must genuinely need recovery");

            object state = GetPrivateState(root);
            Action stale = InvokeCreateResizeRecheckCallback(root, target, state);
            SetGeneration(state, GetGeneration(state) + 3);   // three later transitions' worth

            stale();
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(0, scroller.Offset.Y);
            Assert.True(InvokeIsObscured(target),
                "the callback froze the pre-bump generation, so it must reject itself outright — a LIVE " +
                "read of state.Generation would equal itself here and do the work anyway");

            InvokeCreateResizeRecheckCallback(root, target, state)();
            Dispatcher.UIThread.RunJobs();
            Assert.True(scroller.Offset.Y > 0,
                "positive control: with a matching generation the very same scenario DOES recover");
            Assert.True(target.IsFocused);
        }
        finally { w.Close(); }
    }

    // ── The phantom root Tab stop, both remaining orderings ─────────────────────

    /// <summary>
    /// A DEFERRED staged recovery that runs after its root has already left
    /// the tree. <see cref="RootTransientFocusability_IsRevertedOnDetach"/> covers grant-THEN-detach
    /// (the reset fires because the root genuinely loses focus). This is the complementary
    /// ordering — detach-THEN-run — and the reset cannot save it: the pass walks a dead tree, finds
    /// every candidate unusable (detached reads as obscured), reaches the guaranteed terminal, and
    /// grants <c>Focusable</c> to a root that CANNOT take focus. <c>Focus()</c> returns false,
    /// nothing is ever focused, so no LostFocus ever arrives to undo the grant — and the next time
    /// the view is attached it carries a Tab stop it never authored.
    /// <para>
    /// Constructed end-to-end, not through reflection: <c>SetHelpExpander</c> on an established,
    /// already-compact root posts a staged recovery SYNCHRONOUSLY, which is the one production path
    /// that lets a detach land between the post and the run. (Through <c>Evaluate</c> it is not
    /// constructible at all — that posts at Loaded from Default, and Loaded drains first.)
    /// </para>
    /// </summary>
    [AvaloniaFact]
    public void DeferredRecovery_RootDetachedBeforeThePassRuns_LeavesNoPhantomRootTabStop()
    {
        (Window w, Grid root) = Host(Threshold - 1);   // compact AND established
        try
        {
            var expander = new Expander { IsExpanded = true, [Grid.RowProperty] = 0 };
            var bodyButton = new Button { Content = "body" };
            expander.Content = bodyButton;
            root.Children.Add(expander);
            Dispatcher.UIThread.RunJobs();

            bodyButton.Focus();
            Dispatcher.UIThread.RunJobs();
            Assert.True(bodyButton.IsFocused);
            Assert.False(root.Focusable, "setup precondition: the root starts with no Tab stop of its own");

            // Posts the staged recovery synchronously — a mode transition does it, where the
            // Help expander attach used to.
            w.Height = Threshold - 1;

            // ...and the view goes away before the dispatcher ever gets to it (tab switch, close).
            w.Content = null;
            Dispatcher.UIThread.RunJobs();

            Assert.False(root.Focusable,
                "a pass whose root has already left the tree must do nothing at all — granting the " +
                "terminal's transient focusability to a root that cannot take focus leaves a Tab " +
                "stop with nothing to undo it");
            Assert.False(root.IsFocused);
        }
        finally { w.Close(); }
    }

    /// <summary>
    /// The third ordering: the root is torn down DURING the pass, so the
    /// entry check cannot help — it was attached when the pass began. A handler on the captured
    /// element's own bring-into-view request detaches the view mid-recovery (synchronously, as such
    /// handlers run); the pass then continues down the fallback chain to the terminal, whose
    /// <c>Focus()</c> now cannot succeed. The grant exists only for a hand-off, so a hand-off that
    /// did not happen must not leave it behind.
    /// <para>
    /// Note the ordering makes the detach-time reset irrelevant here even if one existed: the
    /// detach happens BEFORE the grant, so only the terminal itself can undo it.
    /// </para>
    /// </summary>
    [AvaloniaFact]
    public void DeferredRecovery_RootTornDownDuringThePass_LeavesNoPhantomRootTabStop()
    {
        (Window w, Grid root) = Host(Threshold + 50);
        try
        {
            var clipper = new Border { [Grid.RowProperty] = 2, Height = 20, ClipToBounds = true };
            var clippedHost = new StackPanel();
            var captured = new Button { Content = "captured", Height = 30, Margin = new Thickness(0, 50, 0, 0) };
            clippedHost.Children.Add(captured);
            clipper.Child = clippedHost;
            root.Children.Add(clipper);
            Dispatcher.UIThread.RunJobs();

            captured.Focus();
            Dispatcher.UIThread.RunJobs();
            Assert.True(captured.IsFocused);
            Assert.False(root.Focusable);

            // Attached after the setup Focus(), which raises a request of its own. Fires
            // synchronously inside the recovery's own BringIntoView.
            captured.AddHandler(Control.RequestBringIntoViewEvent, (_, _) => w.Content = null);

            w.Height = Threshold - 1;   // transition → staged recovery → teardown mid-pass
            Dispatcher.UIThread.RunJobs();

            Assert.False(root.Focusable,
                "the terminal grants focusability ONLY for a hand-off; when the hand-off cannot " +
                "happen the grant must be undone rather than left as a permanent Tab stop");
            Assert.False(root.IsFocused);
        }
        finally { w.Close(); }
    }

    // ── Shared behavior: the budget, and the resolver's softer bar ─────────────────

    /// <summary>
    /// The shared-budget POLICY was implemented only as far as the
    /// wrapper's own leg. A pass that reached the obscured leg called
    /// <c>RelocateFocusIfNeeded</c>, which starts its own counter at zero — so a pass could
    /// spend wrapper budget PLUS a whole fresh allowance, more than the 8 requests the policy
    /// promises. Now the resize pass threads its remaining budget straight into the staged
    /// recovery, while <c>RelocateFocusIfNeeded</c> — the TRANSITION path's entry point —
    /// still opens a fresh allowance of its own, so
    /// <see cref="FakedProgressForever_StopsAtTheCap_AndRelocates"/> keeps pinning that contract
    /// unchanged.
    /// <para>
    /// Constructed to cross both legs in ONE pass: the first holder is permanently partially
    /// clipped and fakes progress, so the wrapper's own leg spends a request; its handler then
    /// moves focus to a permanently OBSCURED element that also fakes progress, so the hand-over
    /// lands in the obscured leg. Before this fix, the wrapper's own request plus a fresh
    /// 8-request allowance for the obscured leg totaled 9; the shared budget now covers the whole
    /// pass, so the total is exactly 8 however it is split between the legs.
    /// </para>
    /// </summary>
    [AvaloniaFact]
    public void OneResizePass_CrossingBothLegs_NeverExceedsTheSharedBringIntoViewBudget()
    {
        (Window w, Grid root) = Host(Threshold + 100);
        try
        {
            // Added FIRST, so it is first in tree order and the fallback chain (which the obscured
            // leg reaches once the budget runs out) lands here — on something fully visible and
            // outside the scroller. Otherwise the chain picks the partially-clipped target inside
            // the scroller, whose own handler fires again and re-enters the scenario, and the
            // request count stops being attributable to the budget at all (MEASURED: 11 rather
            // than a clean 9).
            var landing = new Button { Content = "landing", [Grid.RowProperty] = 1 };
            root.Children.Add(landing);

            (ScrollViewer scroller, Button partial) = BuildPermanentlyPartialTarget(root);

            // Permanently obscured, and — critically — obscured by its OWN 20-DIP band while sitting
            // WITHIN the scroller's viewport, exactly as FakedProgressForever_StopsAtTheCap_AndRelocates
            // arranges its target. MEASURED that the obvious alternative (park it far below the
            // viewport) does not work: the real BringIntoView then genuinely scrolls to it, the
            // presenter keeps restoring that same offset, and the fingerprint stops changing after
            // two requests, so the loop exhausts the target long before any budget is reached. With
            // the clipper already on screen the real request can never improve anything, which
            // leaves the handler's own monotone nudge as the sole source of "progress".
            var hiddenClipper = new Border { Height = 20, ClipToBounds = true };
            var hiddenHost = new StackPanel();
            var obscured = new Button { Content = "obscured", Height = 30, Margin = new Thickness(0, 25, 0, 0) };
            hiddenHost.Children.Add(obscured);
            hiddenClipper.Child = hiddenHost;
            ((StackPanel)scroller.Content!).Children.Insert(1, hiddenClipper);

            Dispatcher.UIThread.RunJobs();

            partial.Focus();
            Dispatcher.UIThread.RunJobs();
            Assert.True(partial.IsFocused);
            Assert.Equal("PartiallyClipped", DescribeClipVisibility(partial));
            Assert.Equal("Obscured", DescribeClipVisibility(obscured));

            static void FakeProgress(ScrollViewer s) => s.Offset = new Vector(0, s.Offset.Y + 1);

            int requests = 0;
            bool handedOver = false;
            partial.AddHandler(Control.RequestBringIntoViewEvent, (_, _) =>
            {
                ++requests;
                if (handedOver)
                {
                    return;
                }

                handedOver = true;
                obscured.Focus();                       // hand the pass into the obscured leg

                // Counted only from here on, so Focus()'s OWN request above — which is not the
                // pass's spending — never lands in the budget this test is measuring.
                obscured.AddHandler(Control.RequestBringIntoViewEvent, (_, _) =>
                {
                    ++requests;
                    FakeProgress(scroller);
                });
                FakeProgress(scroller);
            });

            ShrinkTo(w, root, Threshold + 60);

            int cap = GetMaxBringIntoViewAttempts();
            Assert.Equal(8, cap);
            Assert.Equal(cap, requests);
        }
        finally { w.Close(); }
    }

    /// <summary>
    /// A correction to the reasoning above: an earlier version
    /// returned straight out of the obscured leg, arguing that
    /// <c>RelocateFocusIfNeeded</c> already hands over internally and a second loop would
    /// only duplicate it. That was wrong, and this is the case that proves it: the two hand over
    /// against DIFFERENT bars. The inner resolver yields to a newer focus it judges USABLE, and
    /// usable is the AA line — focusable, enabled, not ENTIRELY hidden — which a merely PARTIALLY
    /// clipped element satisfies. This pass's bar is full visibility. So the recovery can finish,
    /// perfectly correctly by its own contract, having left the live focus-holder half off-screen,
    /// displaced by its own in-flight request; and the earlier version declared the pass complete.
    /// <para>
    /// Geometry, in the scroller's content space (viewport 205 when the pass runs): spacer to 20,
    /// neighbour [20,50], spacer to 215, target [215,245], trailing scroll room. At offset 0 the
    /// target is ENTIRELY below the viewport (so the obscured leg runs) and the neighbour is fully
    /// visible. Request 1's handler moves focus to the neighbour and lets the request bubble on;
    /// the presenter scrolls 40 DIPs to reveal the target, which leaves the neighbour [20,50]
    /// showing only [40,50] — 10 of its 30 DIPs, PARTIALLY clipped. The resolver calls that usable
    /// and stops; the wrapper must not.
    /// </para>
    /// </summary>
    [AvaloniaFact]
    public void ObscuredRecovery_StaleRequestLeftTheNewHolderPartiallyClipped_HandsOverAndScrollsItBack()
    {
        (Window w, Grid root) = Host(Threshold + 100);
        try
        {
            var scroller = new ScrollViewer { [Grid.RowProperty] = 2 };
            var stack = new StackPanel();
            stack.Children.Add(new Border { Height = 20 });
            var neighbour = new Button { Content = "neighbour", Height = 30 };
            stack.Children.Add(neighbour);
            stack.Children.Add(new Border { Height = 165 });
            var target = new Button { Content = "target", Height = 30 };
            stack.Children.Add(target);
            stack.Children.Add(new Border { Height = 200 });
            scroller.Content = stack;
            root.Children.Add(scroller);
            Dispatcher.UIThread.RunJobs();

            target.Focus();
            scroller.Offset = default;   // Focus() reveals it; put it back so the obscured leg runs
            Dispatcher.UIThread.RunJobs();
            Assert.True(target.IsFocused);
            Assert.Equal("Obscured", DescribeClipVisibility(target));
            AssertFullyVisible(neighbour, "setup: the neighbour starts fully visible");

            int neighbourRequests = 0;
            bool moved = false;
            target.AddHandler(Control.RequestBringIntoViewEvent, (_, _) =>
            {
                if (moved)
                {
                    return;
                }

                moved = true;
                // The race, during RELOCATION this time: focus moves and the request bubbles on,
                // so the presenter still scrolls for the target and half-clips the new holder.
                neighbour.Focus();
                neighbour.AddHandler(Control.RequestBringIntoViewEvent, (_, _) => ++neighbourRequests);
            });

            ShrinkTo(w, root, Threshold + 95);

            Assert.True(neighbour.IsFocused, "the focus move must stand");
            AssertFullyVisible(neighbour,
                "the staged recovery yielded to the neighbour because it judged it USABLE — but " +
                "usable tolerates partial clipping, and this pass's bar is full visibility, so the " +
                "pass owes it the scroll-back its own request made necessary");
            Assert.True(neighbourRequests >= 1,
                "and it must have got there by ACTING on the new holder — zero requests would mean " +
                "the scenario was inert and the assertion above proved nothing");
        }
        finally { w.Close(); }
    }

    // ── Shared behavior: the stale in-flight request ────────────────────────────

    /// <summary>
    /// Stopping attempts 2–8 was never the whole duty. When a handler moves focus
    /// during request 1, that request is ALREADY IN FLIGHT — it keeps bubbling past the handler to
    /// the <c>ScrollContentPresenter</c>, which then scrolls on behalf of the OLD target. If the
    /// element that just took focus lives in that same scroller, the behavior's own request is what
    /// pushes it out of view — and unconditional abandonment then walked away from a
    /// stranding it had itself created. Rescheduling on the next bounds change is no answer: a drag
    /// has a LAST step, and after it there is no next bounds change.
    /// <para>
    /// The pass must instead HAND OVER: once the stale request has settled, run the same
    /// visibility recheck for whoever actually holds focus now. The no-theft precondition still
    /// binds — the new holder must genuinely hold focus and be in-root — and empty focus still
    /// means full abandon (unchanged from before).
    /// </para>
    /// <para>
    /// Geometry, all in the scroller's own content space (viewport 210 at the hosted size, 205
    /// after the shrink that triggers the pass): neighbour [0,15], spacer to 200, target [200,230],
    /// trailing spacer for scroll room. At offset 0 the neighbour is fully visible and the target
    /// hangs 25 DIPs past the viewport — PARTIALLY clipped (asserted through the behavior's own
    /// verdict, not assumed), so the partial leg is what runs. Request 1's handler moves focus to
    /// the neighbour and lets the request bubble on; the presenter scrolls to offset 25 to reveal
    /// the target in full, which puts the neighbour [0,15] ENTIRELY outside the viewport. The
    /// earlier version abandons there, leaving the focused neighbour invisible; the handover recovers it.
    /// </para>
    /// </summary>
    [AvaloniaFact]
    public void PartialRecovery_StaleRequestDisplacedTheNewHolder_HandsOverAndRecoversIt()
    {
        (Window w, Grid root) = Host(Threshold + 100);
        try
        {
            var scroller = new ScrollViewer { [Grid.RowProperty] = 2 };
            var stack = new StackPanel();
            var neighbour = new Button { Content = "neighbour", Height = 15 };
            stack.Children.Add(neighbour);
            stack.Children.Add(new Border { Height = 185 });
            var target = new Button { Content = "target", Height = 30 };
            stack.Children.Add(target);
            stack.Children.Add(new Border { Height = 200 });   // scroll room below the target
            scroller.Content = stack;
            root.Children.Add(scroller);
            Dispatcher.UIThread.RunJobs();

            target.Focus();
            scroller.Offset = default;   // Focus() scrolls it into view; put it back so it hangs out
            Dispatcher.UIThread.RunJobs();
            Assert.True(target.IsFocused);
            Assert.Equal("PartiallyClipped", DescribeClipVisibility(target));
            AssertFullyVisible(neighbour, "setup: the neighbour starts fully visible");

            int targetRequests = 0;
            int neighbourRequests = 0;
            target.AddHandler(Control.RequestBringIntoViewEvent, (_, _) =>
            {
                ++targetRequests;
                if (targetRequests > 1)
                {
                    return;
                }

                // The race: focus moves mid-call, and this request KEEPS BUBBLING afterwards —
                // e.Handled is deliberately left alone — so the presenter still scrolls for the
                // target and displaces the neighbour that just took focus.
                neighbour.Focus();

                // Installed only now, so it counts what the PASS does from here on and never the
                // request Focus() itself just raised.
                neighbour.AddHandler(Control.RequestBringIntoViewEvent, (_, _) => ++neighbourRequests);
            });

            ShrinkTo(w, root, Threshold + 95);

            Assert.Equal(1, targetRequests);
            Assert.True(neighbour.IsFocused,
                "the focus move must stand — handing over is not the same as fighting it");

            // The load-bearing claim, asserted FIRST so a failure reports the stranded geometry
            // itself rather than a proxy for it.
            AssertFullyVisible(neighbour,
                "after the pass settles: the behavior's own in-flight request displaced the new " +
                "focus-holder, so the behavior must repair it rather than walk away");
            Assert.True(neighbourRequests >= 1,
                "and it must have repaired it by genuinely ACTING on the new holder — zero requests " +
                "would mean the scenario was inert and the visibility assertion above proved nothing");
        }
        finally { w.Close(); }
    }

    // ── Shared behavior: races inside the new path ──────────────────────────────

    /// <summary>
    /// WCAG 2.4.7/2.4.11: the partial-clip leg took an ACTION —
    /// <c>BringIntoView</c>, which runs handlers SYNCHRONOUSLY and can re-enter layout and focus —
    /// and then re-checked only GEOMETRY before acting again. The staged transaction's discipline
    /// (revalidate after every action, not just before the first) applies to any action this
    /// behavior takes. Here a handler moves focus to a DIFFERENT in-root element during request 1;
    /// the leg must stop spending requests on an element that no longer holds focus.
    /// <para>
    /// WHAT THIS ONE PINS, as distinct from
    /// <see cref="PartialRecovery_StaleRequestDisplacedTheNewHolder_HandsOverAndRecoversIt"/>: the
    /// new focus-holder here sits OUTSIDE the scroller, so the stale in-flight request cannot have
    /// displaced it, and the pass must therefore find it already fine and STOP — no further request
    /// against the old target, and no work invented for the new holder either. The companion test
    /// covers the opposite topology (new holder inside the same scroller, displaced by that very
    /// request), where stopping is not enough and the pass must hand over and repair. Together they
    /// pin both halves: never spend on the wrong element, never abandon a mess of one's own making.
    /// </para>
    /// <para>
    /// Discriminating by CONSTRUCTION, not timing: the target is permanently clipped by a plain
    /// <c>ClipToBounds</c> Border (nothing can ever recover it) while the handler FAKES progress by
    /// nudging the enclosing scroller 1 DIP per request — the same rig
    /// <see cref="FakedProgressForever_StopsAtTheCap_AndRelocates"/> uses. An earlier version of
    /// this leg therefore spun to the full <c>MaxBringIntoViewAttempts</c> cap (8 requests) after focus had already
    /// moved; the fixed leg issues exactly ONE.
    /// </para>
    /// </summary>
    [AvaloniaFact]
    public void PartialRecovery_FocusMovedInsideBringIntoView_AbandonsInsteadOfActingStale()
    {
        (Window w, Grid root) = Host(Threshold + 100);
        try
        {
            (ScrollViewer scroller, Button target) = BuildPermanentlyPartialTarget(root);
            var elsewhere = new Button { Content = "elsewhere", [Grid.RowProperty] = 1 };
            root.Children.Add(elsewhere);
            Dispatcher.UIThread.RunJobs();

            target.Focus();
            Dispatcher.UIThread.RunJobs();
            Assert.True(target.IsFocused);
            Assert.Equal("PartiallyClipped", DescribeClipVisibility(target));

            // Attached only after the setup Focus(), which raises a request of its own.
            int requests = 0;
            target.AddHandler(Control.RequestBringIntoViewEvent, (_, _) =>
            {
                ++requests;
                elsewhere.Focus();                                          // the race: focus moves mid-call
                scroller.Offset = new Vector(0, scroller.Offset.Y + 1);      // faked progress
            });

            ShrinkTo(w, root, Threshold + 60);

            Assert.Equal(1, requests);
            Assert.True(elsewhere.IsFocused,
                "the focus move that happened during the call must stand — the leg must not fight it");
            Assert.True(target.Bounds.Height > 0);   // the target still exists; it simply stopped being recovered
        }
        finally { w.Close(); }
    }

    /// <summary>
    /// The generation half of the fix above: a real transition landing during the leg's own
    /// synchronous <c>BringIntoView</c> must abandon it, exactly as <c>IsSuperseded</c>
    /// rejects a stale deferred job. Same faked-progress rig as above, so an earlier version of
    /// this leg spun to the cap while superseded and the fixed one issues exactly ONE request.
    /// <para>
    /// The bump is injected directly (the established technique in this file — see
    /// <see cref="FrozenGeneration_CallbackBuiltBeforeLaterTransitions_NoOps"/>'s own three-way
    /// proof that a genuine in-window transition is not constructible through the public API: a
    /// transition's <c>Evaluate</c> runs at Default priority, so it cannot possibly execute inside
    /// a synchronous call made from an already-running Loaded-priority job).
    /// </para>
    /// </summary>
    [AvaloniaFact]
    public void PartialRecovery_GenerationBumpedInsideBringIntoView_AbandonsInsteadOfActingStale()
    {
        (Window w, Grid root) = Host(Threshold + 100);
        try
        {
            (ScrollViewer scroller, Button target) = BuildPermanentlyPartialTarget(root);
            Dispatcher.UIThread.RunJobs();

            target.Focus();
            Dispatcher.UIThread.RunJobs();
            Assert.True(target.IsFocused);
            Assert.Equal("PartiallyClipped", DescribeClipVisibility(target));

            object state = GetPrivateState(root);
            int requests = 0;
            target.AddHandler(Control.RequestBringIntoViewEvent, (_, _) =>
            {
                ++requests;
                SetGeneration(state, GetGeneration(state) + 1);              // the race: a transition lands mid-call
                scroller.Offset = new Vector(0, scroller.Offset.Y + 1);      // faked progress
            });

            ShrinkTo(w, root, Threshold + 60);

            Assert.Equal(1, requests);
            Assert.True(target.IsFocused, "abandoning must not move focus either — it stops, it does not act");
        }
        finally { w.Close(); }
    }

    /// <summary>
    /// WCAG 2.4.3: when the posted pass runs and the FocusManager reports
    /// NOTHING focused, that is not the pass's business. Empty focus means the user (or a close, or
    /// a detach) cleared it; reviving the element that happened to be focused when the pass was
    /// SCHEDULED is focus theft, and the fallback chain's root terminal can leave a phantom Tab
    /// stop behind. An earlier version inherited <c>ResolveRecoveryTarget</c>'s "nothing focused means
    /// recover the capture" rule — correct for a TRANSITION, which cleared focus itself by hiding
    /// the element, and wrong for a resize, which did no such thing.
    /// <para>
    /// The pass is invoked through its own factory rather than through a real resize because the
    /// interleave cannot be constructed through the dispatcher: the pass is posted at Loaded from
    /// an Evaluate running at Default, and Loaded drains ahead of Default, so no job of any
    /// priority can be made to run between the two (the same structural finding
    /// <see cref="StaleGeneration_DirectlyInjected_CausesTheDeferredJobToNoOp"/> documents for the
    /// transition path's ABA race).
    /// </para>
    /// </summary>
    [AvaloniaFact]
    public void ResizeRecheck_FocusClearedBeforeThePassRuns_NeverRevivesTheStaleCapture()
    {
        (Window w, Grid root) = Host(Threshold + 100);
        try
        {
            var scroller = new ScrollViewer { [Grid.RowProperty] = 2 };
            var stack = new StackPanel();
            for (int i = 0; i < 10; i++)
            {
                stack.Children.Add(new Button { Content = $"b{i}", Height = 30 });
            }

            scroller.Content = stack;
            root.Children.Add(scroller);
            Dispatcher.UIThread.RunJobs();

            var captured = (Button)stack.Children[^1];
            captured.Focus();
            scroller.Offset = default;             // scroll it entirely out of view: recoverable IF acted on
            Dispatcher.UIThread.RunJobs();
            Assert.True(captured.IsFocused);
            Assert.True(InvokeIsObscured(captured), "setup precondition: acting on this capture would visibly scroll");

            object state = GetPrivateState(root);
            Action pass = InvokeCreateResizeRecheckCallback(root, captured, state);

            // The user clears focus (clicked the desktop, closed a popup, tabbed to another window).
            TopLevel.GetTopLevel(root)!.FocusManager!.ClearFocus();
            Dispatcher.UIThread.RunJobs();
            Assert.Null(w.FocusManager?.GetFocusedElement());

            pass();
            Dispatcher.UIThread.RunJobs();

            Assert.True(w.FocusManager?.GetFocusedElement() is null,
                "focus the user cleared must stay cleared — reviving the scheduling-time capture is theft");
            Assert.Equal(0, scroller.Offset.Y);
            Assert.False(root.Focusable,
                "and the chain's root terminal must never have run, so no phantom Tab stop is left behind");
        }
        finally { w.Close(); }
    }

    /// <summary>
    /// The no-theft precondition end-to-end, not merely at scheduling.
    /// <see cref="NonTransitionalResize_FocusOutsideTheRoot_IsNeverPulledIn"/> covers focus that was
    /// ALREADY outside when the pass was scheduled (so nothing is ever posted); this covers focus
    /// that was legitimately IN-ROOT at scheduling and left before the pass ran. A guard rather
    /// than a gap test — an earlier version already declined this case, via a different route
    /// (<c>ResolveRecoveryTarget</c>'s own out-of-scope null) than the live-holder rule that
    /// replaced it — so it is here to keep BOTH routes honest, and it passes both before and
    /// after this change rather than pinning a specific fix.
    /// </summary>
    [AvaloniaFact]
    public void ResizeRecheck_FocusMovedOutsideBeforeThePassRuns_NeverRecovers()
    {
        (Window w, Grid root) = Host(Threshold + 100);
        try
        {
            var outside = new Button { Content = "shell" };
            DockPanel.SetDock(outside, Dock.Top);

            var scroller = new ScrollViewer { [Grid.RowProperty] = 2 };
            var stack = new StackPanel();
            for (int i = 0; i < 10; i++)
            {
                stack.Children.Add(new Button { Content = $"b{i}", Height = 30 });
            }

            scroller.Content = stack;
            root.Children.Add(scroller);

            var shell = new DockPanel();
            w.Content = null;
            shell.Children.Add(outside);
            shell.Children.Add(root);              // fill child: the root's height stays window-driven
            w.Content = shell;
            Dispatcher.UIThread.RunJobs();

            var captured = (Button)stack.Children[^1];
            captured.Focus();
            scroller.Offset = default;
            Dispatcher.UIThread.RunJobs();
            Assert.True(captured.IsFocused);
            Assert.True(InvokeIsObscured(captured));

            object state = GetPrivateState(root);
            Action pass = InvokeCreateResizeRecheckCallback(root, captured, state);

            outside.Focus();                       // focus leaves the view before the pass runs
            Dispatcher.UIThread.RunJobs();

            pass();
            Dispatcher.UIThread.RunJobs();

            Assert.True(outside.IsFocused, "focus that left the view is never pulled back in");
            Assert.Equal(0, scroller.Offset.Y);
            Assert.False(root.Focusable);
        }
        finally { w.Close(); }
    }

    /// <summary>
    /// The root's TRANSIENT focusability — granted only for
    /// the fallback chain's hand-off — must not survive the root leaving the tree, or the view
    /// carries a phantom Tab stop into its next attachment.
    /// <see cref="ChainTerminal_RootGetsTransientFocusability"/> proves the LostFocus reset covers
    /// the ordinary path (focus moves on to another control); this covers the DETACHMENT path,
    /// where there may be no such focus move to observe at all.
    /// </summary>
    [AvaloniaFact]
    public void RootTransientFocusability_IsRevertedOnDetach()
    {
        (Window w, Grid root) = Host(Threshold + 50);
        try
        {
            var collapsing = new Button { Content = "only", [Grid.RowProperty] = 0 };
            root.Children.Add(collapsing);
            root.Classes.CollectionChanged += (_, _) =>
                collapsing.IsVisible = !root.Classes.Contains("compactHeight");
            Dispatcher.UIThread.RunJobs();
            collapsing.Focus();
            Dispatcher.UIThread.RunJobs();

            w.Height = Threshold - 1;              // → compact; the ONLY focusable hides
            Dispatcher.UIThread.RunJobs();
            Assert.True(root.IsFocused, "setup precondition: the chain must have reached its root terminal");
            Assert.True(root.Focusable);

            w.Content = null;                      // detach while the root still holds the transient grant
            Dispatcher.UIThread.RunJobs();

            Assert.False(root.Focusable,
                "a detached root must not keep the transient focusability the hand-off granted it — " +
                "reattaching would add a permanent Tab stop the view never authored");
        }
        finally { w.Close(); }
    }

    /// <summary>
    /// A target PERMANENTLY partially clipped: a plain <c>ClipToBounds</c> Border 20 DIPs tall
    /// hosting a 30-DIP button offset 10 down, so the button spans [10,40] against a [0,20] band —
    /// it overlaps, so it is not "obscured" by the AA line, and nothing can ever scroll a plain
    /// Border, so no genuine progress is possible. Wrapped in a real ScrollViewer (filling row 2,
    /// so its viewport shrinks with the window) purely so a handler has something whose offset it
    /// can nudge to FAKE progress.
    /// </summary>
    private static (ScrollViewer Scroller, Button Target) BuildPermanentlyPartialTarget(Grid root)
    {
        var clipper = new Border { Height = 20, ClipToBounds = true };
        var clippedHost = new StackPanel();
        var target = new Button { Content = "target", Height = 30, Margin = new Thickness(0, 10, 0, 0) };
        clippedHost.Children.Add(target);
        clipper.Child = clippedHost;

        var scroller = new ScrollViewer { [Grid.RowProperty] = 2 };
        var scrollerStack = new StackPanel();
        scrollerStack.Children.Add(clipper);
        scrollerStack.Children.Add(new Border { Height = 500 });   // genuine scroll room to fake progress into
        scroller.Content = scrollerStack;
        root.Children.Add(scroller);
        return (scroller, target);
    }

    /// <summary>Resizes the window so the behavior's own root lands at <paramref name="inner"/>
    /// DIPs, then drains layout AND the Loaded-priority staged jobs the behavior defers to.</summary>
    private static void ShrinkTo(Window w, Control root, double inner)
    {
        double overhead = w.Height - root.Bounds.Height;
        w.Height = inner + overhead;
        for (int i = 0; i < 5; i++)
        {
            Dispatcher.UIThread.RunJobs();
        }
    }

    /// <summary>
    /// Clip-aware FULL visibility (criterion C's own line, and the same helper shape every
    /// converted view's test file carries): a control that merely INTERSECTS the cumulative
    /// intersection of its clipping ancestors' viewports is not enough — every edge must be
    /// inside it. Effective visibility and a positive size are asserted first, since a degenerate
    /// control translates to a single point and would trivially satisfy any containment check.
    /// </summary>
    private static void AssertFullyVisible(Control control, string context)
    {
        Assert.True(control.IsEffectivelyVisible, $"[{context}] {control.GetType().Name} is not effectively visible.");
        Assert.True(control.Bounds is { Width: > 0, Height: > 0 },
            $"[{context}] {control.GetType().Name} has a non-positive size ({control.Bounds.Width:F1}x{control.Bounds.Height:F1}).");

        Visual visualRoot = control.GetVisualRoot() as Visual
            ?? throw new InvalidOperationException($"[{context}] the control is not attached to a visual root.");
        Rect controlInRoot = TransformRect(control, new Rect(control.Bounds.Size), visualRoot)
            ?? throw new InvalidOperationException($"[{context}] the control could not be translated into root coordinates.");

        Rect visible = new(visualRoot.Bounds.Size);
        foreach (Visual ancestor in control.GetVisualAncestors())
        {
            if (ancestor is not Control clipper || !clipper.ClipToBounds)
            {
                continue;
            }

            Rect clipperInRoot = TransformRect(clipper, new Rect(clipper.Bounds.Size), visualRoot)
                ?? throw new InvalidOperationException($"[{context}] a clipping ancestor could not be translated.");
            visible = visible.Intersect(clipperInRoot);
        }

        const double Slack = 0.5;
        Assert.True(
            controlInRoot.X >= visible.X - Slack && controlInRoot.Y >= visible.Y - Slack &&
            controlInRoot.Right <= visible.Right + Slack && controlInRoot.Bottom <= visible.Bottom + Slack,
            $"[{context}] {control.GetType().Name} bounds ({controlInRoot}) are not fully inside the clip-aware " +
            $"visible region {visible} — a focused control the user cannot fully see.");
    }

    private static Rect? TransformRect(Visual from, Rect localRect, Visual to)
    {
        Point? topLeft = from.TranslatePoint(new Point(localRect.X, localRect.Y), to);
        Point? bottomRight = from.TranslatePoint(new Point(localRect.Right, localRect.Bottom), to);
        return topLeft is { } tl && bottomRight is { } br ? new Rect(tl, br) : null;
    }

    private static bool InvokeIsObscured(Control element) =>
        (bool)typeof(CompactHeightBehavior)
            .GetMethod("IsObscured", BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, [element])!;

    /// <summary>The behavior's own three-way clip verdict, by name — so a test can state which
    /// leg of the recheck its geometry actually exercises instead of assuming it.</summary>
    private static string DescribeClipVisibility(Control element) =>
        typeof(CompactHeightBehavior)
            .GetMethod("GetClipVisibility", BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, [element])!
            .ToString()!;

    private static Action InvokeCreateResizeRecheckCallback(Control root, Control captured, object state)
    {
        MethodInfo method = typeof(CompactHeightBehavior).GetMethod("CreateResizeRecheckCallback", BindingFlags.NonPublic | BindingFlags.Static)!;
        return (Action)method.Invoke(null, [root, captured, state])!;
    }

    private static object GetPrivateState(Control control)
    {
        FieldInfo statesField = typeof(CompactHeightBehavior).GetField("_states", BindingFlags.NonPublic | BindingFlags.Static)!;
        object statesTable = statesField.GetValue(null)!;
        MethodInfo tryGetValue = statesTable.GetType().GetMethod("TryGetValue")!;
        object?[] args = [control, null];
        bool found = (bool)tryGetValue.Invoke(statesTable, args)!;
        Assert.True(found, "state must already exist for a control with Threshold set");
        return args[1]!;
    }

    private static int GetGeneration(object state) =>
        (int)state.GetType().GetProperty("Generation")!.GetValue(state)!;

    private static int GetMaxBringIntoViewAttempts() =>
        (int)typeof(CompactHeightBehavior)
            .GetField("MaxBringIntoViewAttempts", BindingFlags.NonPublic | BindingFlags.Static)!
            .GetValue(null)!;

    private static void SetGeneration(object state, int value) =>
        state.GetType().GetProperty("Generation")!.SetValue(state, value);

    private static Action InvokeCreateRecoveryCallback(Control root, Control captured, bool enteringCompact, object state)
    {
        MethodInfo method = typeof(CompactHeightBehavior).GetMethod("CreateRecoveryCallback", BindingFlags.NonPublic | BindingFlags.Static)!;
        return (Action)method.Invoke(null, [root, captured, enteringCompact, state])!;
    }

    private static void InvokeRelocateFocusIfNeeded(Control root, Control captured, bool enteringCompact, int generation, object state)
    {
        MethodInfo method = typeof(CompactHeightBehavior).GetMethod("RelocateFocusIfNeeded", BindingFlags.NonPublic | BindingFlags.Static)!;
        method.Invoke(null, [root, captured, enteringCompact, generation, state]);
    }
}
