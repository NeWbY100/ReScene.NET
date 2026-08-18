using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace ReScene.Manager.Behaviors;

/// <summary>
/// Toggles the <c>compactHeight</c> style class on a view's inner layout root from its own
/// bounds height: compact when height &lt; the effective threshold, restore at ≥ threshold+12
/// (restore-only hysteresis — a fresh instance at threshold+1 starts expanded). The threshold is
/// DERIVED from the view's own measured expanded floor (see <see cref="EffectiveThreshold"/> and
/// <see cref="MeasureExpandedFloor"/>) rather than written down per view, so a platform whose font
/// metrics need more room gets a switch point that reflects that instead of a constant calibrated
/// somewhere else; <see cref="ThresholdProperty"/> survives as an optional MINIMUM. Applies
/// per-view <see cref="CompactRowSize"/> values on the root AND on descendant grids
/// carrying their own RowSizes attachment (collected at each apply), caps the always-visible
/// Help body's height in compact mode, runs the staged
/// focus algorithm across transitions, and keeps a still-focused element visible across
/// CONTINUED resizing between transitions (see <see cref="RecheckFocusAfterResize"/>).
/// </summary>
internal static class CompactHeightBehavior
{
    private const string ClassName = "compactHeight";
    private const double RestoreSlack = 12;

    /// <summary>
    /// Headroom between a view's measured expanded floor and the height at which it switches to
    /// compact, so the switch happens slightly BEFORE expanded content stops fitting rather than
    /// at the exact pixel it does. Absorbs the fractional-DIP differences that composed transforms
    /// produce at 125/150% scaling.
    /// </summary>
    private const double ThresholdMargin = 20;

    /// <summary>
    /// Backstop on a single recovery pass's BringIntoView requests (both
    /// <see cref="RelocateFocusIfNeeded"/>'s loop and <see cref="ScrollFullyIntoView"/>'s).
    /// It is NOT the mechanism that
    /// normally ends recovery: the progress rule is (see
    /// <see cref="RelocateFocusIfNeeded"/>) — each request either moves a scroller, in which
    /// case the next one starts strictly closer, or moves nothing, in which case that target
    /// is exhausted immediately. A well-formed tree therefore terminates after at most one
    /// request per nested scroller, comfortably under this number. The cap only catches the
    /// pathological case where a handler fakes progress on every request while the target
    /// stays obscured; reaching it is not a silent stop — recovery falls through to the
    /// fallback chain.
    /// </summary>
    private const int MaxBringIntoViewAttempts = 8;

    /// <summary>
    /// Attaches the behavior to a view root that names no switch height of its own, leaving the
    /// derivation to do all the work. This is how the shipped views opt in: setting an attached
    /// property is what hooks the lifecycle, and <see cref="ThresholdProperty"/> cannot serve that
    /// purpose for a view with no opinion, because its default already IS NaN and assigning NaN
    /// raises no change. Derivation itself is not gated on this — see
    /// <see cref="EffectiveThreshold"/>.
    /// </summary>
    public static readonly AttachedProperty<bool> EnabledProperty =
        AvaloniaProperty.RegisterAttached<Control, bool>("Enabled", typeof(CompactHeightBehavior));

    /// <summary>
    /// An optional MINIMUM switch height, not the switch height itself. The effective threshold is
    /// the larger of this and the view's own measured expanded floor plus
    /// <see cref="ThresholdMargin"/>, so an explicit value can only make a view go compact EARLIER,
    /// never later than its content requires. Setting it also attaches the behavior.
    /// </summary>
    public static readonly AttachedProperty<double> ThresholdProperty =
        AvaloniaProperty.RegisterAttached<Control, double>("Threshold", typeof(CompactHeightBehavior), double.NaN);
    public static readonly AttachedProperty<IReadOnlyList<CompactRowSize>?> RowSizesProperty =
        AvaloniaProperty.RegisterAttached<Control, IReadOnlyList<CompactRowSize>?>("RowSizes", typeof(CompactHeightBehavior));
    /// <summary>
    /// The Help body scroller, so compact mode can cap its height via
    /// <see cref="HelpBodyMaxHeightProperty"/>.
    /// </summary>
    /// <remarks>
    /// Replaces the former <c>HelpExpander</c>. Help used to be an <see cref="Expander"/> that
    /// compact mode force-collapsed to reclaim its height, which had a side effect nobody wanted:
    /// the collapse toggle only appeared in compact mode, so whichever view went compact first at
    /// a given window height — the tallest one — was the only tab where Help had to be clicked
    /// open, while every other tab showed the same text plainly. Help is now a flat section in
    /// every mode; compact still caps its height, it just no longer takes it away.
    /// </remarks>
    public static readonly AttachedProperty<Control?> HelpBodyProperty =
        AvaloniaProperty.RegisterAttached<Control, Control?>("HelpBody", typeof(CompactHeightBehavior));
    public static readonly AttachedProperty<Control?> RestoreFocusTargetProperty =
        AvaloniaProperty.RegisterAttached<Control, Control?>("RestoreFocusTarget", typeof(CompactHeightBehavior));
    public static readonly AttachedProperty<double> HelpBodyMaxHeightProperty =
        AvaloniaProperty.RegisterAttached<Control, double>("HelpBodyMaxHeight", typeof(CompactHeightBehavior), double.NaN);

    public static bool GetEnabled(Control obj) => obj.GetValue(EnabledProperty);

    public static void SetEnabled(Control obj, bool value) => obj.SetValue(EnabledProperty, value);

    /// <summary>
    /// The height this control actually switches at, as the behavior itself computes it — the
    /// larger of any explicit <see cref="ThresholdProperty"/> minimum and the control's own
    /// measured expanded floor plus <see cref="ThresholdMargin"/>. NaN before the control has ever
    /// been evaluated and while it has no opinion at all (see <see cref="EffectiveThreshold"/>).
    /// <para>
    /// Read-only, and exposed for callers that need to reason about the switch point without
    /// restating it: a test deriving the heights either side of it, most of all. A per-view
    /// constant written down a second time somewhere else is a constant that can drift from the
    /// one the behavior uses; asking the behavior cannot.
    /// </para>
    /// </summary>
    public static double GetEffectiveThreshold(Control obj) =>
        _states.TryGetValue(obj, out State? state) ? EffectiveThreshold(obj, state) : GetThreshold(obj);

    public static double GetThreshold(Control obj) => obj.GetValue(ThresholdProperty);

    public static void SetThreshold(Control obj, double value) => obj.SetValue(ThresholdProperty, value);

    public static IReadOnlyList<CompactRowSize>? GetRowSizes(Control obj) => obj.GetValue(RowSizesProperty);

    public static void SetRowSizes(Control obj, IReadOnlyList<CompactRowSize>? value) => obj.SetValue(RowSizesProperty, value);

    public static Control? GetHelpBody(Control obj) => obj.GetValue(HelpBodyProperty);

    public static void SetHelpBody(Control obj, Control? value) => obj.SetValue(HelpBodyProperty, value);

    public static Control? GetRestoreFocusTarget(Control obj) => obj.GetValue(RestoreFocusTargetProperty);

    public static void SetRestoreFocusTarget(Control obj, Control? value) => obj.SetValue(RestoreFocusTargetProperty, value);

    public static double GetHelpBodyMaxHeight(Control obj) => obj.GetValue(HelpBodyMaxHeightProperty);

    public static void SetHelpBodyMaxHeight(Control obj, double value) => obj.SetValue(HelpBodyMaxHeightProperty, value);

    // Per-control state, held weakly so it dies with the control — no leak, no explicit
    // unhook (same rationale as ListBoxAutoScroll's handler table). Captured row values are
    // stored here (keyed by the owning Grid, root OR descendant) rather than on the state's
    // owner, because a descendant grid never gets its own entry — it is only ever reached
    // by walking the root's visual tree at apply time.
    private static readonly ConditionalWeakTable<Control, State> _states = [];

    static CompactHeightBehavior()
    {
        EnabledProperty.Changed.AddClassHandler<Control>(OnThresholdChanged);
        ThresholdProperty.Changed.AddClassHandler<Control>(OnThresholdChanged);
    }

    private static State GetOrCreateState(Control control) => _states.GetValue(control, static _ => new State());

    // ── Lifecycle wiring ─────────────────────────────────────────────

    private static void OnThresholdChanged(Control control, AvaloniaPropertyChangedEventArgs e)
    {
        State state = GetOrCreateState(control);
        if (!state.LifecycleHooked)
        {
            state.LifecycleHooked = true;
            control.AttachedToVisualTree += OnControlAttachedToVisualTree;
            control.DetachedFromVisualTree += OnControlDetachedFromVisualTree;
            control.LostFocus += OnControlLostFocus;
            if (control.IsAttachedToVisualTree())
            {
                HookBounds(control, state);
            }
        }
        else if (control.IsAttachedToVisualTree())
        {
            // Threshold value changed at runtime on an already-live control: reassess now.
            QueueEvaluate(control, state);
        }
    }

    private static void OnControlAttachedToVisualTree(object? sender, VisualTreeAttachmentEventArgs e)
    {
        var control = (Control)sender!;
        HookBounds(control, GetOrCreateState(control));
    }

    // Deliberately does NOT reset the root's transient Focusable, because every ordering that could
    // strand it is already closed at its own source, and a reset here would be unreachable code
    // pretending to be a safety net: granted-then-detached is undone by OnControlLostFocus (the
    // root genuinely loses focus on the way out — pinned by
    // RootTransientFocusability_IsRevertedOnDetach); detached-then-recovered never grants at all
    // (RunStagedRecovery's attachment check); and torn-down-mid-pass grants, fails to hand off, and
    // undoes itself (FocusFallbackChain's terminal). A reset here could not catch that last one
    // anyway — the detach precedes the grant.
    private static void OnControlDetachedFromVisualTree(object? sender, VisualTreeAttachmentEventArgs e)
    {
        var control = (Control)sender!;
        if (!_states.TryGetValue(control, out State? state))
        {
            return;
        }

        if (state.BoundsHandler is { } handler)
        {
            control.PropertyChanged -= handler;
            state.BoundsHandler = null;
        }

        if (state.LayoutHandler is { } layoutHandler)
        {
            control.LayoutUpdated -= layoutHandler;
            state.LayoutHandler = null;
        }
    }

    // LostFocus bubbles: a descendant losing focus (e.g. because the root terminal is about to
    // steal it away via Focus()) raises the SAME event, which arrives here with sender=root
    // just like root's own direct loss of focus does. Resetting on every bubbled occurrence
    // would clear the just-granted transient Focusable mid-hand-off (root.Focus() stealing
    // focus from a still-focused captured element fires the captured element's OWN LostFocus,
    // which bubbles through root before the grant even settles). Only e.Source == root — the
    // event genuinely originating on root itself — means root is the one that lost focus.
    private static void OnControlLostFocus(object? sender, RoutedEventArgs e)
    {
        if (ReferenceEquals(sender, e.Source))
        {
            ((Control)sender!).Focusable = false;
        }
    }

    // Re-subscribing (rather than subscribing once for the control's lifetime), plus the
    // explicit QueueEvaluate below, means every (re)hook forces one evaluation attempt against
    // the CURRENT bounds, even if the numeric value happens to match whatever it was before
    // detaching — "reattach re-evaluates" is a guarantee, not an accident of the value having
    // changed.
    private static void HookBounds(Control control, State state)
    {
        if (state.BoundsHandler is { } previous)
        {
            control.PropertyChanged -= previous;
        }

        if (state.LayoutHandler is { } previousLayout)
        {
            control.LayoutUpdated -= previousLayout;
        }

        // Every (re)attachment re-arms the in-line decision below: whatever height this attachment
        // turns out to have, the verdict for it must be part of the first frame drawn at that
        // height rather than arriving after it.
        state.AwaitingFirstBounds = true;

        void Handler(object? _, AvaloniaPropertyChangedEventArgs args)
        {
            if (args.Property != Visual.BoundsProperty)
            {
                return;
            }

            // IN LINE, not posted — the one case where the coalescing that serves every other
            // bounds change would be actively wrong. See State.AwaitingFirstBounds.
            if (state.AwaitingFirstBounds && control.Bounds.Height > 0)
            {
                Evaluate(control, state);
                return;
            }

            QueueEvaluate(control, state);
        }

        control.PropertyChanged += Handler;
        state.BoundsHandler = Handler;

        void layoutHandler(object? _1, EventArgs _2) => RecaptureFloorAfterLayout(control, state);
        control.LayoutUpdated += layoutHandler;
        state.LayoutHandler = layoutHandler;

        QueueEvaluate(control, state);
    }

    /// <summary>
    /// Keeps the captured expanded floor TRUE, rather than true as of the last time the view's own
    /// bounds happened to change.
    /// <para>
    /// A floor grows for reasons that have nothing to do with the window: a conditional row
    /// appearing, a status line arriving, prose rewrapping, the Help body realizing its content
    /// after flat mode forces it open on the very first evaluation. None of those resize the root —
    /// its height is the window's to decide — so none of them raise a bounds change, and an
    /// evaluation-only capture would keep quoting a floor the layout has already outgrown. Layout
    /// completion is the event that actually corresponds to "the numbers may have moved", so that
    /// is what this listens to.
    /// </para>
    /// <para>
    /// Only while EXPANDED, for the same reason <see cref="Evaluate"/>'s own capture is: compact
    /// mode has already replaced the values being read. And only a GROWN floor can change the
    /// verdict from here — the view is expanded, so a floor that shrank leaves it comfortably
    /// expanded still — which is why an evaluation is queued for that case alone instead of on
    /// every layout pass. The ordinary pass costs one row walk and stops.
    /// </para>
    /// </summary>
    private static void RecaptureFloorAfterLayout(Control control, State state)
    {
        if (state.IsCompact)
        {
            return;
        }

        double before = state.ExpandedFloor;
        CaptureExpandedFloor(control, state);

        if (state.ExpandedFloor > before && control.Bounds.Height < EffectiveThreshold(control, state))
        {
            QueueEvaluate(control, state);
        }
    }

    private static void QueueEvaluate(Control control, State state)
    {
        if (state.UpdateQueued)
        {
            return;
        }

        state.UpdateQueued = true;
        Dispatcher.UIThread.Post(() =>
        {
            state.UpdateQueued = false;
            Evaluate(control, state);
        });
    }

    // ── Threshold evaluation ─────────────────────────────────────────

    private static void Evaluate(Control control, State state)
    {
        double height = control.Bounds.Height;
        if (height <= 0)
        {
            return;
        }

        // A real height has now been seen and judged, so the in-line path has done its job for this
        // attachment. Cleared here rather than at the call site so a POSTED evaluation that happens
        // to get there first disarms it too — otherwise the next bounds change would still be
        // handled in line, doing layout work during a layout pass for no reason.
        state.AwaitingFirstBounds = false;

        // Refresh the captured floor whenever the view is EXPANDED, which is the only state in
        // which the expanded layout can be observed: while compact the row minimums and the
        // compactHeight class have already replaced the values being measured. Every expanded pass
        // re-captures, so a floor that grows with content, with the font size, or with a width
        // change that rewraps text is picked up on the pass that follows it.
        if (!state.IsCompact)
        {
            CaptureExpandedFloor(control, state);
        }

        double threshold = EffectiveThreshold(control, state);
        if (double.IsNaN(threshold))
        {
            return;
        }

        bool wantCompact = state.IsCompact ? height < threshold + RestoreSlack : height < threshold;
        bool isTransition = wantCompact != state.IsCompact;
        bool establishing = !state.Established;

        if (!isTransition && state.Established)
        {
            // Nothing to APPLY (no mode change, and the rows/class already match this mode) — but
            // the bounds that triggered this evaluation still moved, and a viewport that shrinks
            // around a FROZEN scroll offset re-clips whatever the last transition's own recovery
            // scrolled into it. Task 6 measured exactly that in CreatorView: the compact-entry
            // transition scrolled the focused splitter back into its band (offset 0 → 22), then
            // four further, purely within-mode shrinks took that same viewport 321 → 121 DIPs with
            // the offset never re-examined, leaving the splitter clipped away while it still held
            // focus. The recheck below is the standing obligation the one-shot transition recovery
            // cannot discharge on its own.
            QueueResizeRecheck(control, state);
            return;
        }

        state.Established = true;

        // (1) CAPTURE before any change — both directions, since restoring can just as
        // easily strand focus on a hiding compact-only control (e.g. the header toggle).
        // Only meaningful for an actual transition: a first-touch establishment pass with no
        // mode change has nothing to relocate focus away from.
        Control? captured = isTransition ? CaptureFocusedElement(control) : null;

        // Entering compact captures each PixelRestore row's CURRENT (possibly user-dragged)
        // Height before it gets overwritten below. This must happen strictly before
        // state.IsCompact flips and before ApplyRowsEverywhere runs, and only on the actual
        // normal-to-compact transition edge — a later HelpOpen-triggered reapplication while
        // already compact must never recapture (it would capture the just-applied compact
        // pixel value instead of the user's drag).
        if (isTransition && wantCompact)
        {
            CaptureDragHeights(control, state);
        }

        // Every real transition bumps the generation — regardless of whether anything was
        // focused to capture — so "a newer transition has happened since" is detectable even
        // when the transition that made it stale had nothing of its own to relocate.
        if (isTransition)
        {
            ++state.Generation;
        }

        // (2) apply styles/rows.
        state.IsCompact = wantCompact;
        QueueApplyHelpBodyMaxHeight(control, state);
        ApplyRowsEverywhere(control, state);
        ToggleClass(control, wantCompact);

        // (3)-(6) staged: run only after a layout pass reflects the just-applied class/row/
        // visibility changes (Loaded is lower priority than the layout-driving priorities,
        // so the dispatcher services any pending layout before this posted job runs).
        if (captured is not null)
        {
            Dispatcher.UIThread.Post(
                CreateRecoveryCallback(control, captured, wantCompact, state),
                DispatcherPriority.Loaded);
        }

        // A pass that leaves the view EXPANDED has just changed the very layout its floor is read
        // from — a restore rebuilds the expanded rows, and a first evaluation at normal height
        // forces the Help body open — while the capture at the top of this pass necessarily ran
        // BEFORE those changes, and therefore under-reports. So re-evaluate once layout has caught
        // up: that pass re-captures against the settled expanded tree and drops straight back to
        // compact if expanded genuinely no longer fits.
        //
        // POSTED LAST, AND THAT ORDER IS LOAD-BEARING — do not move this above the recovery post.
        // Both jobs sit at the same priority, so they run in the order they were queued, and this
        // one can re-compact the view. Queued FIRST it would run FIRST, and a re-compaction ahead
        // of the restore's own recovery is destructive in three compounding ways: it hides the
        // compact-only controls the restore had just revealed (and vice versa), it bumps the
        // generation so the restore's queued recovery rejects itself as stale, and its own capture
        // finds whatever focus the half-finished restore left behind — which, for a restore that
        // hid the focused compact-only header toggle, is NOTHING. The behavior would then have
        // cleared focus itself and left it cleared, with the one job that could have repaired it
        // already invalidated. Queued last, the recovery has run to completion first, so nothing
        // stale is superseded and this pass captures a settled, genuinely-focused element and
        // stages its own recovery in the ordinary way.
        //
        // Posted rather than left to RecaptureFloorAfterLayout, which covers a DIFFERENT case and
        // cannot cover this one: that handler runs when layout runs, and the changes made above do
        // not always invalidate layout at all (a class no style in this view keys on, row values
        // that resolve to what they already were). Re-validating a restore is not something to make
        // conditional on the layout system having had an opinion about it. The two are
        // complementary — this one guarantees a check after every change the BEHAVIOR makes, that
        // one catches the changes it does not make: content arriving, prose rewrapping, a font
        // growing, none of which resize the root and none of which would otherwise be noticed.
        //
        // It cannot oscillate. A restore that fails raises the threshold above the very height
        // that produced it (the new floor is what made it fail), so restoring again would need a
        // height strictly greater than the one already rejected. One flip, then rest — and this
        // settling pass does not re-post, being neither a transition nor an establishment, so it
        // takes the "nothing to apply" early return above.
        if (!wantCompact && (isTransition || establishing))
        {
            Dispatcher.UIThread.Post(() => Evaluate(control, state), DispatcherPriority.Loaded);
        }
    }

    /// <summary>
    /// Builds the deferred focus-recovery callback, FREEZING the generation into a local at
    /// creation time. That freeze is the entire point of this factory: written inline as
    /// <c>() =&gt; RelocateFocusIfNeeded(..., state.Generation, state)</c>, the field would
    /// instead be read when the dispatcher finally RUNS the job — by which time it holds
    /// whatever the newest transition left behind, so <see cref="IsSuperseded"/> would
    /// forever compare the live value against itself and could never detect staleness at all.
    /// </summary>
    private static Action CreateRecoveryCallback(Control control, Control captured, bool enteringCompact, State state)
    {
        int generation = state.Generation;
        return () => RelocateFocusIfNeeded(control, captured, enteringCompact, generation, state);
    }

    /// <summary>
    /// Schedules one <see cref="RecheckFocusAfterResize"/> pass for a bounds change that is NOT a
    /// mode transition, or returns having done nothing at all.
    /// <para>
    /// TWO gates keep this off the hot path of a live resize-drag. The FIRST is here: the
    /// no-focus-theft precondition, evaluated BEFORE anything is scheduled — with focus in the
    /// shell, the tab strip, another view, another window, or nowhere, this costs one
    /// FocusManager query and nothing is posted, so a resize can never pull focus into this view.
    /// The SECOND is inside the posted pass: it acts only on an element that is ACTUALLY not fully
    /// visible, which is a read-only geometry test with no side effects. Together they mean the
    /// ordinary case — dragging a window edge with a perfectly visible focused control — issues
    /// ZERO BringIntoView requests, and requests are spent only while the focused element is
    /// genuinely drifting out of view, which is precisely when they are the point. No debounce
    /// timer and no size-delta threshold: both would be heuristics standing in for the condition
    /// the contract actually cares about, and both would let a drag END in a stranded state
    /// whenever the last step fell under the heuristic's own bar.
    /// </para>
    /// <para>
    /// Coalesced through <see cref="State.RecheckQueued"/> the same way <see cref="QueueEvaluate"/>
    /// coalesces evaluations, so a burst of bounds changes cannot pile up passes. Coalescing is
    /// safe despite each pass freezing a captured element, because the pass RE-RESOLVES what is
    /// focused when it runs (<see cref="ResolveRecoveryTarget"/>); a superseded capture is
    /// yielded to, never overwritten.
    /// </para>
    /// </summary>
    private static void QueueResizeRecheck(Control control, State state)
    {
        if (state.RecheckQueued || CaptureFocusedElement(control) is not { } focused)
        {
            return;
        }

        Action recheck = CreateResizeRecheckCallback(control, focused, state);
        state.RecheckQueued = true;

        // Loaded, exactly as the transition path's own recovery: lower than the layout-driving
        // priorities, so the bounds change that triggered this has been fully laid out by the time
        // the pass measures anything.
        Dispatcher.UIThread.Post(
            () =>
            {
                state.RecheckQueued = false;
                recheck();
            },
            DispatcherPriority.Loaded);
    }

    /// <summary>
    /// Builds the deferred resize-recheck pass, FREEZING both the generation and the mode into
    /// locals at creation time — the same discipline, and for the same reason, as
    /// <see cref="CreateRecoveryCallback"/>: read live at run time they would compare against
    /// themselves and could never detect that a real transition has landed in between, which
    /// would let a stale pass do focus work on top of a newer apply.
    /// </summary>
    private static Action CreateResizeRecheckCallback(Control control, Control captured, State state)
    {
        int generation = state.Generation;
        bool compact = state.IsCompact;
        return () => RecheckFocusAfterResize(control, captured, compact, generation, state);
    }

    /// <summary>
    /// The within-mode half of the staged-focus contract: keep a still-focused element visible as
    /// the layout keeps changing size around it.
    /// <para>
    /// Each turn of the loop runs only while <see cref="IsPassStillValid"/> holds — the element the
    /// pass is currently serving must BE the live focus-holder in this root. That is what makes the
    /// verdict below safe to read off it directly: it has just been confirmed to be the focused
    /// element, so there is nothing else the pass could be talking about. Empty focus, focus that
    /// left the root, or a newer transition all end the pass outright.
    /// </para>
    /// <para>
    /// The loop exists for HAND-OVER, and only for it: an action this pass takes can itself displace
    /// focus (see <see cref="ScrollFullyIntoView"/>), and the behavior may not leave a control
    /// stranded that its own request pushed out of view. Two bounds keep that from becoming an
    /// argument with the user or with a hostile handler: every element is served at most ONCE per
    /// pass (<c>served</c>), and the BringIntoView budget is shared across the whole pass rather
    /// than refreshed per hand-over.
    /// </para>
    /// Splits strictly along the spec's own DELIBERATE ASYMMETRY rider ("BringIntoView resolve[s]
    /// partial clipping first; the relocation threshold only catches what scrolling cannot
    /// recover"):
    /// <list type="bullet">
    /// <item>FULLY VISIBLE — nothing to do, and nothing is touched. Note this pass judges GEOMETRY
    /// only: an element that is fully visible but has lost <c>Focusable</c>/enablement is stranded
    /// by something a resize did not cause (those are class-driven, i.e. transitions), and stays
    /// the transition path's business.</item>
    /// <item>PARTIALLY CLIPPED — scroll it back into view and STOP. Never the fallback chain: the
    /// user can still see and reach it, so moving focus off it would be theft in exchange for
    /// nothing. This is also the leg a coarse "entirely obscured" test cannot see at all — a
    /// live drag clips a control a few pixels at a time, and each of those states intersects its
    /// viewport.</item>
    /// <item>ENTIRELY OBSCURED — the same staged transaction a transition runs
    /// (<see cref="RelocateFocusIfNeeded"/>), which re-resolves the target, retries BringIntoView
    /// on progress, and relocates through the fallback chain only when scrolling cannot reach it.</item>
    /// </list>
    /// </summary>
    private static void RecheckFocusAfterResize(Control root, Control captured, bool compact, int generation, State state)
    {
        Control holder = captured;
        HashSet<Control> served = [];
        int attempts = 0;

        while (IsPassStillValid(root, holder, compact, generation, state) && served.Add(holder))
        {
            switch (GetClipVisibility(holder))
            {
                case ClipVisibility.FullyVisible:
                    return;

                case ClipVisibility.Obscured:
                    RunStagedRecovery(root, holder, compact, generation, state, ref attempts);
                    break;

                case ClipVisibility.PartiallyClipped:
                    ScrollFullyIntoView(root, holder, compact, generation, state, ref attempts);
                    break;
            }

            // HAND-OVER (the only way back round this loop). Both legs above also return with the
            // holder unchanged when it is settled, unreachable or superseded — nothing further to
            // do. What is left is the case that matters: focus moved to another in-root element
            // DURING one of the requests, which means that request went on bubbling and may have
            // scrolled the new holder out of view on the old one's behalf. The behavior does not get
            // to walk away from a stranding it caused itself, and waiting for the next bounds change
            // is no answer — a drag has a last step, and after it no further bounds change arrives.
            //
            // This applies to the OBSCURED leg every bit as much as the partial one, which is why it
            // breaks rather than returns. RunStagedRecovery yields to a newer focus it judges
            // USABLE — and usable is the AA line (focusable, enabled, not ENTIRELY hidden), which a
            // partially clipped element passes. So it can hand back a holder that is perfectly
            // "usable" and still not fully visible, which is this pass's bar, not its. Only the
            // three-way verdict below settles that.
            if (CaptureFocusedElement(root) is not { } live || ReferenceEquals(live, holder))
            {
                return;
            }

            holder = live;
        }
    }

    /// <summary>
    /// The resize pass's own precondition — asserted before its first action and again after every
    /// action it takes: not superseded, AND the element it was scheduled for is STILL the live
    /// focus-holder inside this root.
    /// <para>
    /// Deliberately stricter than <see cref="ResolveRecoveryTarget"/>, and NOT a substitute for it:
    /// that resolver treats "nothing focused" as meaning recover the capture, which is right for a
    /// TRANSITION (the transition itself cleared focus, by hiding the very element it captured —
    /// that is the situation it exists to repair) and wrong for a resize, which hides nothing. On
    /// this path, empty focus means something else entirely — the user clicked away, a popup
    /// closed, the view detached — and reviving the scheduling-time capture would take focus the
    /// user had let go of, with the fallback chain's root terminal able to leave a Tab stop behind
    /// on a view nobody is looking at. No live in-root holder, no pass.
    /// </para>
    /// </summary>
    private static bool IsPassStillValid(Control root, Control captured, bool compact, int generation, State state) =>
        !IsSuperseded(compact, generation, state) &&
        ReferenceEquals(CaptureFocusedElement(root), captured);

    /// <summary>
    /// Scroll-only recovery for a partially clipped target, under the SAME retry-on-progress rule
    /// and attempt budget as <see cref="RelocateFocusIfNeeded"/>'s own loop: a scroller that only
    /// partially satisfies a request still consumes it, so another request may be needed to carry
    /// the recovery one clipper further out — but a request that moves nothing at all proves the
    /// target is beyond BringIntoView's reach, and there is nothing further to try. Unlike that
    /// loop, exhausting the attempts here is simply the end: a partially clipped element is never
    /// relocated.
    /// <para>
    /// AND, like that loop, it revalidates after EVERY request rather than only before the first.
    /// <c>BringIntoView</c> is not a passive measurement: it raises an event whose handlers run
    /// SYNCHRONOUSLY and can re-enter layout, move focus, or let a whole transition land before it
    /// returns. Judging only the geometry afterwards — which is what this leg did when it was
    /// introduced — keeps scrolling an ancestor on behalf of an element that may no longer hold
    /// focus at all, which can drag the element that DOES hold it out of view: the exact
    /// stranding this behavior exists to prevent, self-inflicted. So the pass's own precondition
    /// (<see cref="IsPassStillValid"/>) is re-asserted between attempts, and a failed one stops this
    /// target immediately — no further request, and emphatically no relocation. Stopping is not the
    /// end of the pass, though: <see cref="RecheckFocusAfterResize"/> decides whether the reason was
    /// a hand-over.
    /// </para>
    /// <para>
    /// <paramref name="attempts"/> is the WHOLE pass's budget, threaded by reference rather than
    /// restarted per target. A hand-over is not evidence that more work is warranted — quite the
    /// opposite, a handler that displaces focus on every request is precisely the pathology
    /// <see cref="MaxBringIntoViewAttempts"/> exists to bound, and handing it a fresh budget each
    /// time it fires would let one pass issue attempts without limit. One pass therefore costs at
    /// most <see cref="MaxBringIntoViewAttempts"/> requests no matter how focus moves inside it,
    /// which is the same number, and the same promise, as before hand-over existed.
    /// </para>
    /// </summary>
    private static void ScrollFullyIntoView(Control root, Control target, bool compact, int generation, State state, ref int attempts)
    {
        while (attempts < MaxBringIntoViewAttempts)
        {
            Vector[] before = CaptureScrollOffsets(target);
            target.BringIntoView();
            target.UpdateLayout();
            ++attempts;

            if (!IsPassStillValid(root, target, compact, generation, state))
            {
                return;
            }

            if (CaptureScrollOffsets(target).SequenceEqual(before) ||
                GetClipVisibility(target) == ClipVisibility.FullyVisible)
            {
                return;
            }
        }
    }

    /// <summary>
    /// The height below which this view switches to compact: the larger of the optional explicit
    /// minimum and the view's own measured expanded floor plus <see cref="ThresholdMargin"/>.
    /// NaN — meaning "no opinion, do nothing" — only when neither is available, which is a root
    /// that is not a Grid and carries no explicit value.
    /// <para>
    /// Taking the LARGER is what makes the derived model safe: an explicit minimum can pull the
    /// switch earlier for a view that wants it, but can never hold a view in expanded mode below
    /// the height its own content actually needs. That is the whole invariant — there must be no
    /// band of window heights in which expanded mode is showing clipped content. Derivation is
    /// therefore NOT opt-in: a view that names a minimum is protected by its own measurements just
    /// the same, because an invariant a caller can decline is not one.
    /// </para>
    /// <para>
    /// Help does not enter into it. <see cref="CompactRowSize.CompactMinHeight"/> is a compact-mode
    /// value and never reaches this floor. Expanded mode renders the Help body flat and
    /// unconstrained, which is the LARGEST it ever is, and the floor already carries that cost as
    /// measured chrome — so the expanded floor stays conservative without a second set of minimums.
    /// </para>
    /// </summary>
    private static double EffectiveThreshold(Control control, State state)
    {
        double explicitMinimum = GetThreshold(control);
        if (state.ExpandedFloor <= 0)
        {
            return explicitMinimum;
        }

        double derived = state.ExpandedFloor + ThresholdMargin;
        return double.IsNaN(explicitMinimum) ? derived : Math.Max(explicitMinimum, derived);
    }

    private static void CaptureExpandedFloor(Control control, State state)
    {
        if (control is Grid grid && MeasureExpandedFloor(grid, GetRowSizes(grid)) is > 0 and double floor)
        {
            state.ExpandedFloor = floor;
        }
    }

    /// <summary>
    /// What this layout needs, in DIPs, for its expanded content to fit — Σ per RowDefinition,
    /// splitting every row into one of two kinds:
    /// <list type="bullet">
    /// <item>GIVABLE — the row's content can scroll, so the floor owes it only the minimum the
    /// design insists on seeing, never its content height. Two ways a row qualifies: a Star row,
    /// which gives by construction and is owed its <c>MinHeight</c>; or a row whose
    /// <see cref="CompactRowSize"/> declares an <see cref="CompactRowSize.ExpandedMinHeight"/>,
    /// which is owed exactly that. A declaration wins over the row's own kind — it is the more
    /// specific statement, and the only one available for the three-band views' config band, which
    /// is a plain Auto row at expanded size.</item>
    /// <item>FIXED — everything else: chrome that has to be shown whole or not at all. A pixel row
    /// contributes its height, an Auto row the tallest desired height among its children including
    /// margins. This is the part that MUST be measured rather than written down, because it is
    /// exactly the part that moves with the platform's font metrics.</item>
    /// </list>
    /// <para>
    /// That split is the whole point of the derived model: measure what varies by platform, author
    /// what is design intent. Counting a scrollable band at its CONTENT height instead would make
    /// the floor say the view cannot fit in space the view can in fact scroll — and for a band the
    /// view caps to the room left over (CreatorView, SampleRestorerView), that content height is a
    /// function of the current window height, so the floor would chase the very height it is being
    /// compared against and no window would ever be tall enough.
    /// </para>
    /// <para>
    /// Reads the desired sizes the last real layout pass already produced rather than re-measuring:
    /// a Grid measures its Auto rows unconstrained in the dimension they size to, so those values
    /// are the natural content heights regardless of how short the window currently is, and reading
    /// them cannot invalidate the layout that produced them. A naive <c>Measure(∞)</c> would report
    /// CONTENT height for the givable rows too, and would dirty the live layout to ask.
    /// </para>
    /// </summary>
    private static double MeasureExpandedFloor(Grid grid, IReadOnlyList<CompactRowSize>? rows)
    {
        double total = 0;
        for (int i = 0; i < grid.RowDefinitions.Count; i++)
        {
            RowDefinition row = grid.RowDefinitions[i];
            if (AuthoredExpandedMinimum(rows, i) is { } authored)
            {
                total += authored;
                continue;
            }

            if (row.Height.IsAbsolute)
            {
                total += Math.Max(row.Height.Value, row.MinHeight);
                continue;
            }

            if (row.Height.IsStar)
            {
                total += row.MinHeight;
                continue;
            }

            double rowDesired = 0;
            foreach (Control child in grid.Children.OfType<Control>())
            {
                if (Grid.GetRow(child) == i)
                {
                    // DesiredSize ALREADY includes the child's margin — Avalonia's Layoutable adds
                    // it in MeasureCore. Adding Margin.Top/Bottom again counted every margined
                    // child's margin twice, inflating the floor and collapsing the view to compact
                    // mode while the expanded content still had room.
                    rowDesired = Math.Max(rowDesired, child.DesiredSize.Height);
                }
            }

            total += Math.Max(rowDesired, row.MinHeight);
        }

        // Row spacing sits BETWEEN rows and is real height the grid will demand, so a floor that
        // omits it sits below the true content floor. No view in this app sets Grid.RowSpacing
        // today — it defaults to 0, so this term is currently always zero and fixes no observed
        // bug. It is here so the floor stays correct if one ever does, NOT because anything was
        // seen to clip.
        if (grid.RowDefinitions.Count > 1)
        {
            total += grid.RowSpacing * (grid.RowDefinitions.Count - 1);
        }

        return total;
    }

    /// <summary>
    /// The expanded minimum a view has declared for one of its rows, or null if it has declared
    /// none — which is also what a row carrying the NaN default means, so "givable" is opt-in per
    /// row rather than implied by merely having a <see cref="CompactRowSize"/> entry at all (the
    /// Reconstructor's TabControl row has one for its compact minimums while remaining a Star row
    /// the floor already handles).
    /// </summary>
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

    private static void ToggleClass(Control control, bool compact)
    {
        if (compact)
        {
            control.Classes.Add(ClassName);
        }
        else
        {
            control.Classes.Remove(ClassName);
        }
    }

    // ── Row application ──────────────────────────────────────────────

    private static void CaptureDragHeights(Control root, State state)
    {
        CaptureDragHeightForGrid(root, state);
        foreach (Visual descendant in root.GetVisualDescendants())
        {
            if (descendant is Grid grid)
            {
                CaptureDragHeightForGrid(grid, state);
            }
        }
    }

    private static void CaptureDragHeightForGrid(Control control, State state)
    {
        if (control is not Grid grid || GetRowSizes(grid) is not { } rows)
        {
            return;
        }

        foreach (CompactRowSize rowSize in rows)
        {
            if (rowSize.Mode != CompactRowMode.PixelRestore || rowSize.RowIndex >= grid.RowDefinitions.Count)
            {
                continue;
            }

            GridLength currentHeight = grid.RowDefinitions[rowSize.RowIndex].Height;
            if (currentHeight.IsAbsolute)
            {
                state.CapturedDragHeight[(grid, rowSize.RowIndex)] = currentHeight.Value;
            }
        }
    }

    /// <summary>
    /// Applies RowSizes on the root AND every descendant grid carrying its own RowSizes
    /// attachment. Descendants are collected fresh on every call (a cheap visual-tree walk
    /// that only runs on mode/help changes) rather than cached at attach time, so attachment
    /// order and late tree construction can never leave a descendant grid stuck on stale
    /// values.
    /// </summary>
    private static void ApplyRowsEverywhere(Control root, State state)
    {
        bool isCompact = state.IsCompact;

        ApplyGridRows(root, isCompact, state);
        foreach (Visual descendant in root.GetVisualDescendants())
        {
            if (descendant is Grid grid)
            {
                ApplyGridRows(grid, isCompact, state);
            }
        }
    }

    private static void ApplyGridRows(Control control, bool isCompact, State state)
    {
        if (control is not Grid grid || GetRowSizes(grid) is not { } rows)
        {
            return;
        }

        foreach (CompactRowSize rowSize in rows)
        {
            ApplyOneRow(grid, rowSize, isCompact, state);
        }
    }

    private static void ApplyOneRow(Grid grid, CompactRowSize rowSize, bool isCompact, State state)
    {
        if (rowSize.RowIndex >= grid.RowDefinitions.Count)
        {
            return;
        }

        RowDefinition rowDef = grid.RowDefinitions[rowSize.RowIndex];

        // The XAML-authored MinHeight, captured the first time this row is ever touched
        // (before any mutation below) — never re-captured afterwards, so it survives any
        // number of later compact/restore round-trips.
        (Control Grid, int RowIndex) minKey = (grid, rowSize.RowIndex);
        if (!state.CapturedMinHeight.TryGetValue(minKey, out double originalMinHeight))
        {
            originalMinHeight = rowDef.MinHeight;
            state.CapturedMinHeight[minKey] = originalMinHeight;
        }

        // One compact minimum, not two. Rows used to carry a second, smaller value that applied
        // only while the Help body was open — Help is now always open, so that value IS the
        // compact minimum and the choice disappeared with the state it depended on.
        double compactValue = rowSize.CompactMinHeight;

        switch (rowSize.Mode)
        {
            case CompactRowMode.MinOnly:
                rowDef.MinHeight = isCompact ? compactValue : originalMinHeight;
                break;

            case CompactRowMode.PixelRestore:
                if (isCompact)
                {
                    rowDef.Height = new GridLength(compactValue, GridUnitType.Pixel);
                    rowDef.MinHeight = compactValue;
                }
                else
                {
                    double restoreHeight = state.CapturedDragHeight.TryGetValue((grid, rowSize.RowIndex), out double dragHeight)
                        ? dragHeight
                        : rowSize.NormalHeight;
                    rowDef.Height = new GridLength(restoreHeight, GridUnitType.Pixel);
                    rowDef.MinHeight = originalMinHeight;
                }
                break;

            case CompactRowMode.AutoToStar:
                rowDef.Height = isCompact ? new GridLength(1, GridUnitType.Star) : GridLength.Auto;
                rowDef.MinHeight = isCompact ? compactValue : 0;
                break;
        }
    }

    // Queued rather than applied inline: the class/row changes that precede it drive a layout
    // pass, and the body's own measurement is only settled after it. Loaded is lower priority
    // than the layout-driving priorities, so by the time this runs the body has had a chance to
    // realize at its natural size and the cap lands on a real measurement.
    // The body's ContentPresenter realizes its child lazily, tied to layout: while IsExpanded
    // is false the wrapping content area is never measured, so the ScrollViewer never attaches
    // to the visual tree at all. IsExpanded has already been SET by the time ApplyHelpBodyMaxHeight
    // is queued, but the resulting visibility/layout consequences are not settled yet at that
    // point — Loaded is lower priority than the layout-driving priorities, so by the time this
    // runs, the content area (if now expanded) has had a chance to actually realize.
    private static void QueueApplyHelpBodyMaxHeight(Control control, State state) =>
        Dispatcher.UIThread.Post(() => ApplyHelpBodyMaxHeight(control, state), DispatcherPriority.Loaded);

    private static void ApplyHelpBodyMaxHeight(Control control, State state)
    {
        if (GetHelpBody(control) is not { } body)
        {
            return;
        }

        // Capped whenever compact, with no Help-open condition: the body is always showing now,
        // so compact mode bounds its height rather than reclaiming it wholesale.
        //
        // NaN is the property's default and means "this caller declared no cap" — it is NOT a
        // height. Assigning it raises ArgumentException from MaxHeight's own validation, which
        // previously could not happen because the cap was only ever applied to a control that had
        // gone through the Help expander wiring; attaching a body without also declaring a cap now
        // reaches here directly.
        double cap = GetHelpBodyMaxHeight(control);
        body.MaxHeight = state.IsCompact && !double.IsNaN(cap) ? cap : double.PositiveInfinity;
    }

    // ── Staged focus ──────────────────────────────────────────────────

    private static Control? CurrentFocusedElement(Control root) =>
        TopLevel.GetTopLevel(root)?.FocusManager?.GetFocusedElement() as Control;

    /// <summary>
    /// The currently-focused element, but ONLY if it is focused AND a descendant of
    /// <paramref name="root"/> — otherwise null, so a resize while
    /// focus sits in the shell menu, the tab strip, another window, or nowhere can never
    /// pull focus into this view.
    /// </summary>
    private static Control? CaptureFocusedElement(Control root)
    {
        if (CurrentFocusedElement(root) is not { } focused)
        {
            return null;
        }

        return ReferenceEquals(focused, root) || root.IsVisualAncestorOf(focused) ? focused : null;
    }

    /// <summary>
    /// Runs the post-layout obscurement check and, if needed, the fallback chain — but only if
    /// this job is still current, and on whatever element actually still needs recovering.
    /// Rejected outright if a NEWER transition has superseded this one (generation) or the mode
    /// has since changed away from the direction this job was queued for. Otherwise,
    /// <see cref="ResolveRecoveryTarget"/> decides what (if anything) to act on: it can differ
    /// from <paramref name="captured"/> — see its own doc for why.
    /// <para>
    /// Every BringIntoView attempt re-runs the WHOLE resolution, and strictly in this order:
    /// supersession, then re-resolve what is focused NOW, and only THEN settledness.
    /// BringIntoView runs handlers synchronously and can leave focus somewhere else entirely,
    /// so judging settledness first would let the one case where BringIntoView SUCCEEDS —
    /// "the element we captured is perfectly visible now" — return happily while the element
    /// focus actually landed on sits obscured and unreachable.
    /// </para>
    /// </summary>
    private static void RelocateFocusIfNeeded(Control root, Control captured, bool enteringCompact, int generation, State state)
    {
        // The TRANSITION path's entry point, and its budget: one transition's recovery gets its own
        // full allowance of requests, exactly as it always has. The resize pass calls
        // <see cref="RunStagedRecovery"/> directly instead, threading the budget it has already
        // partly spent, so that one pass cannot cost more than one allowance in total.
        int attempts = 0;
        RunStagedRecovery(root, captured, enteringCompact, generation, state, ref attempts);
    }

    /// <inheritdoc cref="RelocateFocusIfNeeded"/>
    private static void RunStagedRecovery(Control root, Control captured, bool enteringCompact, int generation, State state, ref int attempts)
    {
        // A job posted while the view was live can run after it has left the tree — a tab switch or
        // a window close between the post and the dispatcher servicing it. There is nothing to
        // recover in a detached tree (no focus to hold, nothing visible to scroll into view) and,
        // worse, walking it reaches the chain's guaranteed terminal, which would grant the root
        // focusability it can no longer hand off — see FocusFallbackChain's own note. Neither the
        // generation nor the mode need have changed, so IsSuperseded cannot see this.
        if (!root.IsAttachedToVisualTree())
        {
            return;
        }

        Control candidate = captured;
        HashSet<Control> exhausted = [];

        while (true)
        {
            if (IsSuperseded(enteringCompact, generation, state))
            {
                return;
            }

            if (ResolveRecoveryTarget(root, candidate) is not { } target)
            {
                return;
            }

            bool obscured = IsObscured(target);
            if (IsSettled(target, obscured))
            {
                // A synchronous BringIntoView handler can CLEAR focus outright. The target then
                // reads as perfectly settled — attached, visible, focusable — while NOTHING is
                // focused at all, and returning here would leave the window with no focus ring
                // and no reachable starting point. A relocation this behavior initiated never
                // ends in empty focus: hand off through the chain instead.
                if (CurrentFocusedElement(root) is null)
                {
                    FocusFallbackChain(root, target, enteringCompact);
                }

                return;
            }

            // Scrollable ancestors may recover it — merely-clipped focus is never relocated
            // without giving them their chance first, and that holds for an element retargeted
            // mid-recovery just as much as for the originally captured one.
            if (obscured && attempts < MaxBringIntoViewAttempts && !exhausted.Contains(target))
            {
                Vector[] before = CaptureScrollOffsets(target);
                target.BringIntoView();
                target.UpdateLayout();
                ++attempts;

                // A scroller that only PARTIALLY satisfies a request still CONSUMES it
                // (ScrollContentPresenter sets e.Handled to "I moved"), so the next scroller
                // outward never saw this one. As long as something moved, a fresh request
                // starts from a strictly better position and can carry the recovery one
                // clipper further out; only an attempt that moved nothing at all proves this
                // target is beyond BringIntoView's reach and may be given up on.
                if (CaptureScrollOffsets(target).SequenceEqual(before))
                {
                    exhausted.Add(target);
                }

                candidate = target;
                continue;
            }

            FocusFallbackChain(root, target, enteringCompact);
            return;
        }
    }

    /// <summary>
    /// The scroll offsets of every scrollable ancestor of <paramref name="element"/> — the
    /// fingerprint that tells a BringIntoView attempt which ACHIEVED something from one that
    /// could not, so the loop above knows whether another request is worth issuing.
    /// <see cref="ScrollContentPresenter"/> rather than <see cref="ScrollViewer"/> because the
    /// presenter is where the offset actually lives (a ScrollViewer merely mirrors its own),
    /// and it is the same element whose bounds <see cref="IsObscured"/> already treats as the
    /// clipping viewport.
    /// </summary>
    private static Vector[] CaptureScrollOffsets(Control element) =>
        [.. element.GetVisualAncestors().OfType<ScrollContentPresenter>().Select(static presenter => presenter.Offset)];

    private static bool IsSuperseded(bool enteringCompact, int generation, State state) =>
        state.Generation != generation || state.IsCompact != enteringCompact;

    /// <summary>
    /// What this job should actually recover, if anything — not necessarily
    /// <paramref name="captured"/> itself. Nothing currently focused is the ordinary,
    /// expected transient state the instant <paramref name="captured"/> becomes invisible or
    /// unfocusable (precisely what this job exists to recover from, not evidence that some
    /// unrelated action has taken over), so that case, and the "still the same element" case,
    /// both mean: recover <paramref name="captured"/>. Focus that moved to something OUTSIDE
    /// this root entirely is never this job's business (re-checked here since scope can change
    /// between capture and this job running) — return null. Focus
    /// that moved to a DIFFERENT, USABLE element still inside this root means somebody else
    /// already decided where focus belongs — respect it, return null. Focus that moved to a
    /// DIFFERENT element inside this root that is ITSELF unusable means the same transition (or
    /// something concurrent with it) stranded THAT element instead of the one originally
    /// captured — recovery must now target it, or this job would report `captured` as
    /// "settled" while the REAL, currently-focused element sits broken.
    /// </summary>
    private static Control? ResolveRecoveryTarget(Control root, Control captured)
    {
        Control? current = CurrentFocusedElement(root);
        if (current is null || ReferenceEquals(current, captured))
        {
            return captured;
        }

        bool inScope = ReferenceEquals(current, root) || root.IsVisualAncestorOf(current);
        if (!inScope)
        {
            return null;
        }

        return IsUsable(current) ? null : current;
    }

    private static bool IsSettled(Control captured, bool obscured) =>
        !obscured && captured.Focusable && captured.IsEffectivelyEnabled;

    /// <summary>
    /// True if <paramref name="element"/> is entirely unseeable: detached, invisible anywhere in
    /// its ancestor chain, or clipped out completely — the WCAG 2.4.11 AA line, and the ONLY
    /// condition the spec permits focus RELOCATION to trigger on. Merely partial clipping is
    /// deliberately not obscurement here (spec's asymmetry rider); see
    /// <see cref="GetClipVisibility"/> for the finer verdict and who consumes it.
    /// </summary>
    private static bool IsObscured(Control element) => GetClipVisibility(element) == ClipVisibility.Obscured;

    /// <summary>
    /// How much of <paramref name="element"/> survives the CUMULATIVE intersection of every
    /// clipping ancestor's viewport. <c>IsEffectivelyVisible</c> alone does not see clipping at
    /// all (a scrolled-away row stays "visible"), and testing each clipper INDEPENDENTLY is not
    /// equivalent to testing against their intersection: an element can overlap clipper A in one
    /// part of itself and clipper B in a disjoint part, passing both checks separately, while A∩B
    /// (what is actually visible through both at once) doesn't overlap it at all. So every
    /// clipper's own viewport is transformed into ONE common space (the visual root) and
    /// progressively intersected together FIRST, and the element is tested against that single
    /// combined rect.
    /// <para>
    /// ONE walk answers both questions the behavior asks — "is any of it visible" (relocation's
    /// trigger) and "is ALL of it visible" (the resize recheck's) — so the two can never disagree
    /// about the same tree, and the finer verdict costs nothing over the coarse one.
    /// </para>
    /// </summary>
    private static ClipVisibility GetClipVisibility(Control element)
    {
        if (!element.IsAttachedToVisualTree() || !element.IsEffectivelyVisible)
        {
            return ClipVisibility.Obscured;
        }

        if (element.GetVisualRoot() is not Visual root)
        {
            return ClipVisibility.Obscured;
        }

        if (TransformRect(element, new Rect(element.Bounds.Size), root) is not { } elementInRoot)
        {
            return ClipVisibility.Obscured; // no common coordinate space with its own root
        }

        Rect? visible = null;
        foreach (Visual ancestor in element.GetVisualAncestors())
        {
            if (ancestor is not Control clipper || !clipper.ClipToBounds)
            {
                continue;
            }

            if (TransformRect(clipper, new Rect(clipper.Bounds.Size), root) is not { } clipperInRoot)
            {
                return ClipVisibility.Obscured; // no common coordinate space with the clipper
            }

            visible = visible is { } current ? current.Intersect(clipperInRoot) : clipperInRoot;
        }

        if (visible is not { } combined)
        {
            return ClipVisibility.FullyVisible; // nothing clips it at all
        }

        if (!elementInRoot.Intersects(combined))
        {
            return ClipVisibility.Obscured;
        }

        return Contains(combined, elementInRoot) ? ClipVisibility.FullyVisible : ClipVisibility.PartiallyClipped;
    }

    // Half a DIP of slack, the same allowance the views' own clip-aware test helpers use: at
    // 125/150% scaling the composed transforms land edges fractionally past a viewport they
    // exactly fill, and chasing that with BringIntoView would be work with nothing to show for it.
    private static bool Contains(Rect outer, Rect inner)
    {
        const double Slack = 0.5;
        return inner.X >= outer.X - Slack && inner.Y >= outer.Y - Slack &&
               inner.Right <= outer.Right + Slack && inner.Bottom <= outer.Bottom + Slack;
    }

    private static Rect? TransformRect(Visual from, Rect localRect, Visual to)
    {
        Point? topLeft = from.TranslatePoint(new Point(localRect.X, localRect.Y), to);
        Point? bottomRight = from.TranslatePoint(new Point(localRect.Right, localRect.Bottom), to);
        return topLeft is { } tl && bottomRight is { } br ? new Rect(tl, br) : null;
    }

    /// <summary>
    /// Attempts, in order, the resolved direction target, then every other usable descendant
    /// in tree order, then the root itself (granted transient focusability) — the guaranteed
    /// terminal. Every intermediate candidate is validated (attached, unobscured, focusable,
    /// enabled) AND excludes <paramref name="captured"/> itself before <c>Focus()</c> is even
    /// attempted; a candidate whose own <c>Focus()</c> call returns false (it became unusable
    /// in the instant between validation and the call, or the framework refused it for some
    /// other reason) is skipped rather than silently ending the chain there. The terminal step
    /// is never gated behind the same usability check — it is the guaranteed last resort, and
    /// a silent no-op is forbidden at every step.
    /// </summary>
    private static void FocusFallbackChain(Control root, Control captured, bool enteringCompact)
    {
        // Entering compact, the preferred landing spot is the top of the view. That used to be the
        // Help header toggle; with Help now a flat section the body scroller is its nearest
        // equivalent, and it is focusable in compact for exactly the views that route the keyboard
        // through it. The Reconstructor's body deliberately is not focusable (its links are the
        // route), so TryFocus rejects it there and the descendant walk below takes over — the same
        // graceful degradation the toggle lookup relied on.
        Control? resolved = enteringCompact
            ? GetHelpBody(root)
            : GetRestoreFocusTarget(root);

        if (TryFocus(resolved, captured))
        {
            return;
        }

        foreach (Control candidate in root.GetVisualDescendants().OfType<Control>())
        {
            if (TryFocus(candidate, captured))
            {
                return;
            }
        }

        // Terminal: TopLevel is not focusable by default, so Focusable is granted here ONLY
        // for the hand-off; OnControlLostFocus resets it the moment focus moves on, so no
        // permanent Tab stop is added. Unconditional — never gated behind IsUsable.
        root.Focusable = true;
        if (!root.Focus())
        {
            // The hand-off did not happen. A root that is detached, or whose window is being torn
            // down, cannot take focus — Focus() reports false and nothing is focused, so the
            // LostFocus reset this grant relies on will NEVER arrive to undo it. Left set, it is a
            // permanent Tab stop the view never authored, revealed the next time the view attaches.
            // The grant lasts exactly as long as the attempt it was made for.
            root.Focusable = false;
        }
    }

    private static bool TryFocus(Control? candidate, Control captured) =>
        candidate is not null && !ReferenceEquals(candidate, captured) && IsUsable(candidate) && candidate.Focus();

    private static bool IsUsable(Control? control) =>
        control is not null && control.Focusable && control.IsEffectivelyEnabled && !IsObscured(control);

    /// <summary>
    /// How much of an element survives its clipping ancestors (see
    /// <see cref="GetClipVisibility"/>). The three-way distinction exists because the spec draws
    /// its relocation line at ENTIRELY obscured while criterion C requires FULLY visible: the
    /// middle state is real, common during a resize drag, and must be handled — by scrolling
    /// only, never by moving focus.
    /// </summary>
    private enum ClipVisibility
    {
        /// <summary>Detached, invisible in its own chain, or entirely outside the combined viewport.</summary>
        Obscured,

        /// <summary>Overlaps the combined viewport, but hangs past at least one of its edges.</summary>
        PartiallyClipped,

        /// <summary>Every edge inside the combined viewport — or nothing clips it at all.</summary>
        FullyVisible,
    }

    /// <summary>
    /// Per-control state: mode flag, the coalescing guards, the Bounds subscription, captured
    /// row values (keyed by the owning Grid — root or descendant — since a descendant grid
    /// never gets its own state entry), and the expander's IsExpanded subscription.
    /// </summary>
    private sealed class State
    {
        public bool IsCompact { get; set; }

        /// <summary>
        /// Set the first time <see cref="Evaluate"/> ever runs to completion for this control
        /// (transition or not). Distinguishes "nothing to do, already evaluated" from "nothing
        /// to do YET — this is the very first look at a fresh instance", so a fresh instance
        /// that starts (and stays) at normal height still gets one establishing pass instead of
        /// being short-circuited by the "no mode change" early-return before anything (e.g. the
        /// Help expander) is ever synchronized to that mode.
        /// </summary>
        public bool Established { get; set; }

        /// <summary>
        /// Bumped on every actual transition. A deferred focus-recovery job captures the
        /// generation at post time and rejects itself if this no longer matches when it
        /// finally runs — defense against a later transition (or, checked separately, a mode
        /// flip or an intervening focus move) invalidating the job's premise before it runs.
        /// </summary>
        public int Generation { get; set; }

        /// <summary>
        /// The view's expanded content height in DIPs, captured on every pass that runs while
        /// expanded and held across a compact session — where the expanded layout no longer exists
        /// to be measured. Zero until the first expanded pass, which is the only state in which the
        /// behavior has no opinion of its own about where the switch belongs.
        /// </summary>
        public double ExpandedFloor { get; set; }

        public bool UpdateQueued { get; set; }

        /// <summary>
        /// The same coalescing role <see cref="UpdateQueued"/> plays for evaluations, for the
        /// within-mode focus recheck: a burst of bounds changes (every frame of a resize drag)
        /// leaves at most one pass pending.
        /// </summary>
        public bool RecheckQueued { get; set; }

        /// <summary>
        /// Set on every (re)attachment, cleared by the first evaluation that sees a real height:
        /// while it holds, a bounds change is evaluated IN LINE instead of through
        /// <see cref="QueueEvaluate"/>.
        /// <para>
        /// A posted evaluation cannot decide the frame the user is about to see. Bounds arrive
        /// during the layout pass; the job posted from that notification runs after the pass has
        /// finished, by which time the frame built from it can already have been presented — in the
        /// view's DEFAULT (expanded) shape, because nothing had told it otherwise yet. That is the
        /// flash: click into a tab whose view belongs in compact, and one expanded frame appears
        /// first. Deciding in line puts the class and row values in before the pass completes, so
        /// the layout the frame is built from is the right one.
        /// </para>
        /// <para>
        /// The derived model made this structural rather than incidental. A per-view constant could
        /// be compared against the very first height available; a measured floor needs the view to
        /// have been measured — but that measurement has happened by the time bounds are being
        /// assigned, so the first bounds notification is exactly the last moment at which the
        /// verdict can still make the frame, and the first at which it can be reached.
        /// </para>
        /// <para>
        /// Deliberately one-shot per attachment. Mutating classes and row definitions from inside a
        /// layout pass costs an extra pass, which is the right trade once — to avoid a visibly
        /// wrong frame — and the wrong one for every frame of a resize drag, where coalescing is
        /// what keeps the cost bounded and no frame is wrong for long enough to see.
        /// </para>
        /// </summary>
        public bool AwaitingFirstBounds { get; set; }

        public bool LifecycleHooked { get; set; }

        public EventHandler<AvaloniaPropertyChangedEventArgs>? BoundsHandler { get; set; }

        /// <summary>
        /// The LayoutUpdated subscription that keeps <see cref="ExpandedFloor"/> current — see
        /// <see cref="RecaptureFloorAfterLayout"/>. Tracked alongside
        /// <see cref="BoundsHandler"/> and torn down with it, so the two follow the same
        /// attach/detach lifecycle and neither outlives the tree it was hooked into.
        /// </summary>
        public EventHandler? LayoutHandler { get; set; }

        public Dictionary<(Control Grid, int RowIndex), double> CapturedDragHeight { get; } = [];

        public Dictionary<(Control Grid, int RowIndex), double> CapturedMinHeight { get; } = [];

        public EventHandler<AvaloniaPropertyChangedEventArgs>? ExpanderIsExpandedHandler { get; set; }
    }
}
