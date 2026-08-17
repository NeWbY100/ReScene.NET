using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using ReScene.App.Core.ViewModels;

namespace ReScene.Manager.Views;

/// <summary>
/// The SRR Creator tab, ported from the WPF <c>ReScene.NET.Views.CreatorView</c>. Bound to a
/// <see cref="CreatorViewModel"/> (supplied by the shell via <c>DataContext="{Binding Creator}"</c>).
/// This code-behind carries the two non-MVVM behaviors the WPF view kept in code-behind: dropping
/// files onto the Stored Files grid to add them, and the inline-edit dedup guard on the editable
/// "Stored As" column. Input/Output TextBox file-drop is declarative via
/// <c>behaviors:TextBoxDropBehavior.DropMode="File"</c> in the XAML.
/// </summary>
public partial class CreatorView : UserControl
{
    // The "Stored As" value before an inline edit, so a duplicate edit can be reverted.
    private string? _storedNameBeforeEdit;

    // The trailing log row's own XAML MinHeight (RootGrid row 3) — kept as a literal constant here
    // (not read back from the RowDefinition) because in COMPACT mode CompactHeightBehavior's own
    // AutoToStar handling for row 1 doesn't touch row 3 at all, so RowDefinitions[3].MinHeight is
    // always this authored value regardless of mode. Mirrors SampleRestorerView's own identical
    // constant/rationale.
    private const double LogRowMinHeight = 80;

    // BORROWED, not independently measured for THIS view: this is SampleRestorerView's
    // own ArrangeRoundingSlack VALUE, reused verbatim because the underlying MECHANISM is
    // identical in kind — CompactInvariantRig.MeasureFloor's bare Measure(Infinity) call reports
    // each Auto row's UNCONSTRAINED desired height, while a REAL Grid arrange pass additionally
    // shrinks Auto rows when the total genuinely exceeds available space — but the exact minimum
    // slack Creator itself needs was never separately derived from Creator's own
    // Measure-vs-Arrange gap the way SampleRestorer's original figure was. Empirically validated
    // for Creator's own case by Invariant_ExpandedMode_NeverClipsAcrossUnsafeHeightRange's
    // six-point real-render safe-range test (721 through 1400), which passes with this value —
    // but that is evidence the value is SUFFICIENT here, not that 10 is Creator's own measured
    // minimum.
    private const double ArrangeRoundingSlack = 10;

    private readonly Grid _root;
    private readonly Control _chromeRow;
    private readonly ScrollViewer _configScroller;
    private readonly Control _pinnedRow;

    public CreatorView()
    {
        AvaloniaXamlLoader.Load(this);

        // Avalonia has no WPF PreviewDragOver/PreviewDrop tunnel and no XAML AllowDrop property, so
        // the grid's file-drop is opted in and wired here (mirroring the shell window's drag-drop).
        DataGrid grid = this.FindControl<DataGrid>("StoredFilesGrid")!;
        DragDrop.SetAllowDrop(grid, true);
        grid.AddHandler(DragDrop.DragOverEvent, OnStoredFilesDragOver);
        grid.AddHandler(DragDrop.DropEvent, OnStoredFilesDrop);

        // Small-window layout degradation: the switch height is DERIVED from this view's own
        // measured expanded floor, not named here — the largest converted view, and the one that
        // is therefore compact in most real windows. x:CompileBindings="False" means x:Name
        // elements are NOT wired to auto-generated fields (same as every other ported view in this
        // project) — resolved once via FindControl instead.
        //
        // The config band declares ExpandedMinHeight 500: like SampleRestorerView, this view's
        // config band genuinely GIVES at expanded size, because the cap installed below makes its
        // ScrollViewer scroll rather than push the trailing bands off-screen. 500 is the share of
        // the previous hand-calibrated 720 constant that the design attributed to this band —
        // (720 − 20 margin) − 47 chrome − 68 pinned − 80 log ≈ 505, measured on Windows and
        // rounded down — and is what keeps the Input section, the Stored Files grid at its
        // authored 150, the Output row and the Options stack visible together before the view
        // would rather be compact.
        var root = (Grid)Content!;
        Grid configGrid = this.FindControl<Grid>("ConfigGrid")!;
        ScrollViewer helpBody = this.FindControl<ScrollViewer>("HelpBody")!;
        TextBox outputTextBox = this.FindControl<TextBox>("OutputTextBox")!;
        Behaviors.CompactHeightBehavior.SetEnabled(root, true);
        Behaviors.CompactHeightBehavior.SetRowSizes(root,
            [new Behaviors.CompactRowSize(RowIndex: 1, NormalHeight: double.NaN,
                CompactMinHeight: 80, Mode: Behaviors.CompactRowMode.AutoToStar,
                ExpandedMinHeight: 500)]);
        // DESCENDANT row: the Stored Files grid row lives on ConfigGrid, not root — a fixed-pixel
        // row (150 normal) so the splitter drags exactly as today, restoring to a user's dragged
        // height (not just back to 150) across a compact round-trip via PixelRestore's own capture.
        Behaviors.CompactHeightBehavior.SetRowSizes(configGrid,
            [new Behaviors.CompactRowSize(RowIndex: 3, NormalHeight: 150,
                CompactMinHeight: 80, Mode: Behaviors.CompactRowMode.PixelRestore)]);
        Behaviors.CompactHeightBehavior.SetHelpBody(root, helpBody);
        Behaviors.CompactHeightBehavior.SetHelpBodyMaxHeight(root, 40);

        // RestoreFocusTarget is OutputTextBox, NOT InputTextBox. It was originally chosen to keep
        // resize-triggered focus recovery out of the Input row's keyboard trap; that trap is now
        // fixed (KeyboardNavigation.TabNavigation="Local" scopes both path rows' TabIndex pins), so
        // safety no longer forces the choice. It is RETAINED rather than re-derived: OutputTextBox
        // is a named, always-present field partway down the form, so recovery lands there instead
        // of resetting the user to the very first row. Note the Options checkboxes and the App-name
        // field sit between it and the Create SRR button — it is not the last field before the
        // action, and nothing here claims a landing better than "not the top".
        // CreatorCompactTests.RestoreFocusTarget_PrefersTheOutputFieldOverTheTopOfTheForm pins the
        // choice so a future retarget is a decision rather than a drift.
        Behaviors.CompactHeightBehavior.SetRestoreFocusTarget(root, outputTextBox);

        // EXPANDED-mode safety cap (the same categorical issue SampleRestorerView's own ctor
        // remarks flagged as the most likely second consumer — this view's own StoredFilesGrid
        // could face the identical problem). MEASURED directly: with the worst case forced
        // (12 detected sets capped at 96, 8 stored files, both
        // FieldStatusLines non-None, Cancel+ProgressMessage+ProgressBar visible), this view's
        // config content — Input, Stored Files header/grid/splitter, Output, and all 7 Options
        // checkboxes plus the App name row, none of which scroll independently in EXPANDED mode —
        // sums to ~883 DIPs of natural height (CompactInvariantRig.MeasureFloor), far exceeding the
        // 721-DIP window this view is expanded at (Threshold+1). EXPANDED mode's row 1 is plain
        // Auto (only CompactRowMode.AutoToStar's own COMPACT branch bounds it) and nothing else in
        // this view scrolls at the page level, so — exactly as SampleRestorerView found — a plain
        // Auto row here would push the pinned action band and the entire log translated fully below
        // the window's own bottom edge across a wide expanded-height range.
        //
        // Mechanism identical to SampleRestorerView's own (not promoted to the shared behavior —
        // that promotion is a decision for a THIRD consumer): on every layout pass, cap the config
        // ScrollViewer's own MaxHeight to whatever remains of the root's actual available height
        // after the chrome row (0) and the pinned band (2) take their own current, real space,
        // minus the log row's (3) own reserved MinHeight floor. Never binds for small/typical
        // content (pixel parity holds); once content genuinely exceeds it, the ScrollViewer's own
        // existing VerticalScrollBarVisibility="Auto" engages exactly like compact mode's own
        // scrolling story, just triggered by content overflow instead of a window-height threshold.
        // Reset to unconstrained (PositiveInfinity) while compact: that mode's own Star-sized row 1
        // (CompactHeightBehavior's AutoToStar) must not be second-guessed by this unrelated cap.
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

        // Guard against re-triggering LayoutUpdated with a value it would already report next time
        // (both the "already converged" case and the "both sides are infinity" case, which
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

    private void OnStoredFilesDragOver(object? sender, DragEventArgs e)
    {
        e.DragEffects = e.DataTransfer.Contains(DataFormat.File) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnStoredFilesDrop(object? sender, DragEventArgs e)
    {
        e.Handled = true;

        if (DataContext is not CreatorViewModel vm)
        {
            return;
        }

        IStorageItem[]? files = e.DataTransfer.TryGetFiles();
        if (files is null)
        {
            return;
        }

        var paths = files
            .Select(file => file.TryGetLocalPath())
            .Where(path => !string.IsNullOrEmpty(path))
            .Select(path => path!)
            .ToList();

        if (paths.Count > 0)
        {
            vm.AddStoredFiles(paths);
        }
    }

    private void OnStoredNameBeginningEdit(object? sender, DataGridBeginningEditEventArgs e)
        => _storedNameBeforeEdit = (e.Row.DataContext as CreatorViewModel.StoredFileItem)?.StoredName;

    private void OnStoredNameCellEditEnding(object? sender, DataGridCellEditEndingEventArgs e)
    {
        if (e.EditAction != DataGridEditAction.Commit
            || DataContext is not CreatorViewModel vm
            || e.Row.DataContext is not CreatorViewModel.StoredFileItem item
            || e.EditingElement is not TextBox editor)
        {
            return;
        }

        string newName = (editor.Text ?? string.Empty).Replace('\\', '/').Trim();

        // Reject a rename onto a name another stored file already uses; otherwise normalize the
        // committed value to the SRR's key space (forward slashes). Avalonia's DataGridTextColumn
        // commits on edit-end, so set both the editor text (which the commit writes back) and the
        // model value, matching the WPF original.
        if (!newName.Equals(_storedNameBeforeEdit, StringComparison.OrdinalIgnoreCase)
            && vm.IsStoredNameTaken(newName, item))
        {
            editor.Text = _storedNameBeforeEdit;
            item.StoredName = _storedNameBeforeEdit ?? item.StoredName;
            vm.WarnDuplicateStoredName(newName);
        }
        else
        {
            editor.Text = newName;
            item.StoredName = newName;
        }
    }
}
