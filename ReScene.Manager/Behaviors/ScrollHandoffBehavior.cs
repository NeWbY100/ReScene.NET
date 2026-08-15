using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace ReScene.Manager.Behaviors;

/// <summary>
/// Chains keyboard/focus navigation in an inner <c>DataGrid</c> — whose own scrolling is
/// entirely self-contained virtualization, oblivious to any ancestor — out to the nearest ANCESTOR
/// <see cref="ScrollViewer"/> once the current row moves outside the outer's own viewport (a
/// small-window config band scrolls its DataGrid host, so a user gesture that
/// would otherwise dead-end at the grid's own edge must continue past it). Confirmed by decompiling
/// every <c>Focus()</c> call site in <c>Avalonia.Controls.DataGrid</c> 11.3.13's own package:
/// ordinary (non-edit) arrow-key browsing NEVER focuses a specific cell or row —
/// <c>ProcessDataGridKey</c> ends by focusing the GRID ITSELF unconditionally, and the only
/// per-cell <c>Focus()</c> calls are inside cell-EDIT entry. Consequently
/// <c>Control.BringIntoView()</c> — the sole way <c>RequestBringIntoView</c> is ever raised in
/// Avalonia (also confirmed by decompilation: it is a plain extension method, never invoked
/// automatically by any focus-change machinery) — is never called by the grid's own arrow-key
/// handling either; it only moves the grid's OWN internal virtualized offset
/// (<c>ScrollSlotIntoView</c>) to keep the new current row within ITS OWN viewport. This behavior
/// calls <c>BringIntoView()</c> itself on the newly-current row (found via the public
/// <c>DataGrid.CurrentCellChanged</c> event and <c>DataGrid.SelectedIndex</c>),
/// which is genuinely necessary — nothing else in the framework ever performs it for this control.
/// It keys off <c>DataGrid.CurrentCellChanged</c> at the ROW level, not the individual cell:
/// <c>DataGridRow</c> exposes no public per-column cell lookup, and a row's bounds are a
/// strict superset of every cell within it, so bringing the row fully into view is sufficient to
/// satisfy "the current cell ends fully visible" without depending on <c>DataGridCell</c>
/// internals that are not part of the public API.
/// <para>
/// NO WHEEL MECHANISM (removed): an earlier version of this behavior also chained
/// <c>PointerWheelChanged</c> at the grid's own
/// scroll extent. It was removed, not merely left undocumented, for two independent reasons.
/// First, redundancy: <c>DataGrid</c>'s own <c>OnPointerWheelChanged</c> class handler
/// already leaves the event unhandled whenever it cannot consume the gesture internally (it
/// computes <c>UpdateScroll(...)</c>, and when that reports no movement, sets
/// <c>e.Handled = e.Handled || !ScrollViewer.GetIsScrollChainingEnabled(this)</c>), and
/// <c>IsScrollChainingEnabled</c> defaults to <c>true</c> and is never overridden anywhere in this
/// app — so Avalonia's own bubble-to-ancestor-<see cref="ScrollViewer"/> chaining already produces
/// the identical externally-observable result with zero custom code; re-verified comprehensively
/// (not just one scenario) by temporarily removing the wheel registration and confirming all four
/// dedicated wheel tests plus the real, production-wired view's own handoff test still passed
/// unchanged. Second, and decisively: the removed handler's own claimed "insurance against a
/// future style disabling chaining" was ineffective even in that hypothetical case. Avalonia's
/// routed-event pipeline runs CLASS handlers (how <c>DataGrid</c>'s own override is wired)
/// before INSTANCE handlers added via <c>Interactive.AddHandler</c> for the same
/// element/phase; the removed handler was a plain (non-handledEventsToo) instance handler, which
/// is skipped entirely once <c>e.Handled</c> is already <c>true</c>. If chaining were ever
/// disabled, <c>DataGrid</c>'s own class handler would set <c>e.Handled = true</c> BEFORE
/// the removed handler could run — so it could never have fired in precisely the scenario it was
/// meant to guard against. There is no configuration, current or hypothetical, in which the wheel
/// path did anything a plain <c>IsScrollChainingEnabled="True"</c> default does not already do; if
/// the platform default is ever overridden, that is a deliberate, visible style change, not
/// something this behavior can meaningfully insure against. Full history and the original
/// per-mechanism reasoning are preserved in git (this file's own history around commits
/// 6c1bad3/153cbb2), not reproduced here.
/// </para>
/// </summary>
internal static class ScrollHandoffBehavior
{
    public static readonly AttachedProperty<bool> HandoffProperty =
        AvaloniaProperty.RegisterAttached<Control, bool>("Handoff", typeof(ScrollHandoffBehavior));

    public static bool GetHandoff(Control obj) => obj.GetValue(HandoffProperty);

    public static void SetHandoff(Control obj, bool value) => obj.SetValue(HandoffProperty, value);

    // Weakly keyed so a grid's state dies with the grid — no leak, no explicit unhook required on
    // the caller's part (same rationale as ListBoxAutoScroll's / ScrollViewerHomeEndKeys' own handler tables).
    private static readonly ConditionalWeakTable<DataGrid, State> _states = [];

    static ScrollHandoffBehavior()
    {
        HandoffProperty.Changed.AddClassHandler<Control>(OnHandoffChanged);
    }

    private static void OnHandoffChanged(Control control, AvaloniaPropertyChangedEventArgs e)
    {
        if (control is not DataGrid grid)
        {
            return;
        }

        State state = _states.GetValue(grid, static _ => new State());

        if ((bool)e.NewValue!)
        {
            if (state.LifecycleHooked)
            {
                return;
            }

            state.LifecycleHooked = true;
            grid.AttachedToVisualTree += OnGridAttachedToVisualTree;
            grid.DetachedFromVisualTree += OnGridDetachedFromVisualTree;
            if (grid.IsAttachedToVisualTree())
            {
                Attach(grid, state);
            }
        }
        else
        {
            Detach(grid, state);
        }
    }

    private static void OnGridAttachedToVisualTree(object? sender, VisualTreeAttachmentEventArgs e)
    {
        var grid = (DataGrid)sender!;
        Attach(grid, _states.GetValue(grid, static _ => new State()));
    }

    private static void OnGridDetachedFromVisualTree(object? sender, VisualTreeAttachmentEventArgs e)
    {
        var grid = (DataGrid)sender!;
        if (_states.TryGetValue(grid, out State? state))
        {
            Detach(grid, state);
        }
    }

    /// <summary>
    /// Re-resolves the outer <see cref="ScrollViewer"/> and re-wires the mechanism every time the
    /// grid (re)joins the visual tree — not just once — mirroring <c>CompactHeightBehavior</c>'s own
    /// "reattach re-evaluates" rule: a tab-hosted view is detached/reattached on every tab switch in
    /// this app (only the selected TabItem's content stays in the live visual tree), and the
    /// ancestor chain is only walkable while attached.
    /// </summary>
    private static void Attach(DataGrid grid, State state)
    {
        if (state.Outer is not null)
        {
            return;
        }

        if (grid.GetVisualAncestors().OfType<ScrollViewer>().FirstOrDefault() is not { } outer)
        {
            return; // nothing to hand off to — leave the grid's own (unaffected) scrolling as-is
        }

        state.Outer = outer;

        // Posted at Loaded priority (mirrors CompactHeightBehavior's own identical deferral
        // rationale for post-transition focus recovery) rather than called synchronously here:
        // MEASURED that DataGrid's own internal virtualization scroll
        // (ScrollSlotIntoView, run as part of the SAME currency-update call chain that raises
        // CurrentCellChanged) has not always finished repositioning the new current row's OWN
        // DataGridRow by the time this event fires — calling BringIntoView() synchronously here
        // could act on a STALE row position, undershooting the outer ScrollViewer's own offset
        // adjustment. Loaded priority runs after the dispatcher has serviced the pending layout
        // the internal scroll just triggered, so the row's Bounds are settled by the time this runs.
        void OnCurrentCellChanged(object? _, EventArgs __) =>
            Dispatcher.UIThread.Post(() => BringCurrentRowIntoView(grid), DispatcherPriority.Loaded);

        grid.CurrentCellChanged += OnCurrentCellChanged;

        state.CurrentCellHandler = OnCurrentCellChanged;
    }

    private static void Detach(DataGrid grid, State state)
    {
        if (state.CurrentCellHandler is { } currentCellHandler)
        {
            grid.CurrentCellChanged -= currentCellHandler;
            state.CurrentCellHandler = null;
        }

        state.Outer = null;
    }

    /// <summary>
    /// The current row is looked up fresh on every call (never cached) — DataGridRow instances are
    /// recycled/re-realized by the grid's own virtualization as it scrolls, so a cached reference
    /// would go stale silently.
    /// </summary>
    private static void BringCurrentRowIntoView(DataGrid grid)
    {
        if (grid.SelectedIndex < 0)
        {
            return;
        }

        grid.GetVisualDescendants().OfType<DataGridRow>()
            .FirstOrDefault(row => row.Index == grid.SelectedIndex)
            ?.BringIntoView();
    }

    private sealed class State
    {
        public bool LifecycleHooked { get; set; }

        public ScrollViewer? Outer { get; set; }

        public EventHandler<EventArgs>? CurrentCellHandler { get; set; }
    }
}
