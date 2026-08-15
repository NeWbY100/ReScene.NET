using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace ReScene.Manager.Views;

/// <summary>
/// The SRS Restorer tab, ported from the WPF <c>ReScene.NET.Views.SampleRestorerView</c>. Bound to a
/// <see cref="ReScene.App.Core.ViewModels.SampleRestorerViewModel"/> (supplied by the shell via
/// <c>DataContext="{Binding SampleRestorer}"</c>): an SRR-file row, a Media Directory row, an Output
/// Directory row, an editable <c>DataGrid</c> of the SRR's embedded SRS entries (a
/// <c>DataGridCheckBoxColumn</c> toggling which samples to restore, plus an editable Media File
/// column for entries the automatic match missed), a Restore All/Cancel action row with progress, and
/// a log. Path TextBox file/folder drop is declarative via
/// <c>behaviors:TextBoxDropBehavior.DropMode</c> in the XAML (the WPF original wired it imperatively
/// in <c>Loaded</c> via <c>TextBoxDropHelper</c>, which had no such attached property). Unlike the SRR
/// Creator's "Stored As" column, the grid's editable Media File column needs no inline-edit dedup
/// guard, so no <c>BeginningEdit</c>/<c>CellEditEnding</c> code-behind is required here.
/// </summary>
public partial class SampleRestorerView : UserControl
{
    // The trailing log row's own XAML MinHeight (RowDefinitions[3]) — kept as a literal constant
    // here (not read back from the RowDefinition) because in COMPACT mode CompactHeightBehavior's
    // own AutoToStar handling for row 1 doesn't touch row 3 at all, so RowDefinitions[3].MinHeight
    // is always this authored value regardless of mode; duplicating the literal is simpler and
    // just as correct as reading it back through an extra indirection.
    private const double LogRowMinHeight = 80;

    // MEASURED slack: CompactInvariantRig.MeasureFloor's own bare
    // Measure(InnerWidth, Infinity) call reports each Auto row's UNCONSTRAINED desired height,
    // while a REAL Grid arrange pass (this behavior's own actual target) additionally shrinks
    // Auto rows below that when the total genuinely exceeds available space — e.g. measured
    // directly: the chrome row's own real arranged height was 35 DIPs at the smallest expanded
    // case, but 41 DIPs is what a fresh, unconstrained Measure(Infinity) call reports for the
    // exact same content. Reserving this extra margin keeps MeasureFloor's own (stricter, static)
    // figure additionally covered, on top of the real-arrange safety this mechanism already
    // guarantees on its own (see Invariant_ExpandedMode_NeverClipsAcrossUnsafeHeightRange, which
    // does not depend on this constant at all).
    private const double ArrangeRoundingSlack = 10;

    private readonly Grid _root;
    private readonly Control _chromeRow;
    private readonly ScrollViewer _configScroller;
    private readonly Control _pinnedRow;

    public SampleRestorerView()
    {
        AvaloniaXamlLoader.Load(this);

        // Small-window layout degradation: the switch height is DERIVED from this view's own
        // measured expanded floor, not named here — the headline defect view (action row + log
        // measured 0px at 700×450 BASE state under the pre-conversion DockPanel).
        // x:CompileBindings="False" means x:Name elements are NOT wired to auto-generated fields
        // here (same as every other ported view in this project) — resolved once via FindControl
        // instead.
        //
        // The config band declares ExpandedMinHeight 320: this view is one of the two whose config
        // band genuinely GIVES at expanded size, because the cap installed below makes its
        // ScrollViewer scroll rather than push the trailing bands off-screen. 320 is the share of
        // the previous hand-calibrated 535 constant that the design attributed to this band —
        // (535 − 20 margin) − 47 chrome − 68 pinned − 80 log, measured on Windows and rounded — so
        // the derived switch point lands where the constant used to, while now moving with the
        // platform's own font metrics instead of pretending they are the Windows ones.
        var root = (Grid)Content!;
        Expander helpDisclosure = this.FindControl<Expander>("HelpDisclosure")!;
        TextBox srrFileTextBox = this.FindControl<TextBox>("SRRFileTextBox")!;
        Behaviors.CompactHeightBehavior.SetEnabled(root, true);
        Behaviors.CompactHeightBehavior.SetRowSizes(root,
            [new Behaviors.CompactRowSize(RowIndex: 1, NormalHeight: double.NaN,
                CompactMinHeight: 110, HelpOpenMinHeight: 80, Mode: Behaviors.CompactRowMode.AutoToStar,
                ExpandedMinHeight: 320)]);
        Behaviors.CompactHeightBehavior.SetHelpExpander(root, helpDisclosure);
        Behaviors.CompactHeightBehavior.SetHelpBodyMaxHeight(root, 40);
        Behaviors.CompactHeightBehavior.SetRestoreFocusTarget(root, srrFileTextBox);

        // EXPANDED-mode safety cap: a maxed, 12-row-populated
        // SRSEntriesGrid pushes row 1's own Auto-sized natural height to ~488 DIPs; MEASURED
        // directly that at inner heights from Threshold+1 (536) up to ~640, the trailing rows
        // (the pinned action band AND the entire log — not merely "some clipping") land
        // ENTIRELY below the window's own bottom edge, since EXPANDED mode's row 1 is plain Auto
        // (only CompactRowMode.AutoToStar's own COMPACT branch bounds it) and nothing else in
        // this view scrolls at the page level. Every other converted view's own worst-case
        // config content is small and fixed regardless of window size (their own
        // Invariant_ExpandedModeFloor_UnderThreshold tests already prove it always fits), so this
        // is NOT implemented as a change to the shared CompactHeightBehavior (which all four of
        // them also use) — it is scoped to this view alone, kept as plain code-behind rather than
        // a new promoted-too-early Behaviors/ abstraction (this codebase's own established rule:
        // promotion is a decision for when a SECOND consumer appears — CreatorView,
        // whose own StoredFilesGrid could face the identical problem, is the most likely one).
        //
        // Mechanism: on every layout pass, cap the config ScrollViewer's OWN MaxHeight to
        // whatever remains of the root's actual available height after the chrome row (0) and
        // the pinned band (2) take their own current, real space, minus the log row's (3) own
        // reserved MinHeight floor. This never binds for small/typical content (the computed cap
        // is comfortably larger than row 1's own natural height in that case, so nothing changes
        // — pixel parity holds), and once content genuinely exceeds it, the ScrollViewer's own
        // existing VerticalScrollBarVisibility="Auto" engages exactly like compact mode's own
        // scrolling story, just triggered by content overflow instead of a window-height
        // threshold. Reset to unconstrained (PositiveInfinity) while compact: that mode's own
        // Star-sized row 1 (CompactHeightBehavior's AutoToStar) must not be second-guessed by an
        // unrelated cap computed from this mechanism's own (compact-irrelevant) formula.
        _root = root;
        _chromeRow = root.Children.OfType<Control>().Single(c => Grid.GetRow(c) == 0);
        _configScroller = root.Children.OfType<ScrollViewer>().Single(c => Grid.GetRow(c) == 1);
        _pinnedRow = root.Children.OfType<Control>().Single(c => Grid.GetRow(c) == 2);
        root.LayoutUpdated += OnRootLayoutUpdated;
    }

    private void OnRootLayoutUpdated(object? sender, EventArgs e)
    {
        double safeMax = _root.Classes.Contains("compactHeight")
            ? double.PositiveInfinity
            : Math.Max(0, _root.Bounds.Height - _chromeRow.DesiredSize.Height - _pinnedRow.DesiredSize.Height - LogRowMinHeight - ArrangeRoundingSlack);

        // Guard against re-triggering LayoutUpdated with a value it would already report next
        // time (both the "already converged" case and the "both sides are infinity" case, which
        // Math.Abs(inf - inf) evaluates to NaN, always > any epsilon, and would otherwise reapply
        // forever).
        if (double.IsPositiveInfinity(_configScroller.MaxHeight) && double.IsPositiveInfinity(safeMax))
        {
            return;
        }

        if (Math.Abs(_configScroller.MaxHeight - safeMax) > 0.5)
        {
            _configScroller.MaxHeight = safeMax;
        }
    }
}
