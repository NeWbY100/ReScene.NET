using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using ReScene.App.Core.Services;
using ReScene.App.Core.ViewModels;
using ReScene.Core.Comparison;
using ReScene.Hex;
using ReScene.Manager.Controls;
using ReScene.Manager.Converters;
using ReScene.Manager.Services;
using ReScene.Manager.Views;
using ReScene.RAR;

namespace ReScene.Manager.Tests;

/// <summary>
/// Headless render tests for the ported <see cref="FileCompareView"/> (Compare tab — the app's most
/// complex view: two symmetric panels, each a structure <see cref="TreeView"/> + a properties
/// <c>DataGrid</c> + an embedded <see cref="HexView"/>). The central gate is <b>zero binding
/// errors</b> (via <see cref="BindingErrorSink"/>): both empty and with seeded properties/tree nodes
/// (including diff/indent rows) so the diff/indent converter bindings realize. The compare pipeline,
/// file dialogs and hex-diff computer are inert fakes — only the view wiring is exercised; a live
/// compare, drag-drop and clipboard copy are the controller's launch-smoke.
/// </summary>
public class FileCompareViewTests
{
    // ── Inert service doubles (the view test never runs a compare) ──

    private sealed class InertFileCompareService : IFileCompareService
    {
        public object? LoadFileData(string filePath) => null;

        public IReadOnlyList<RARDetailedBlock>? ParseDetailedBlocks(string filePath) => null;

        public CompareResult Compare(object? leftData, object? rightData,
            IReadOnlyList<RARDetailedBlock>? leftBlocks = null, IReadOnlyList<RARDetailedBlock>? rightBlocks = null,
            IHexDataSource? leftSource = null, IHexDataSource? rightSource = null) => new();
    }

    private sealed class InertHexDiffComputer : IHexDiffComputer
    {
        public Task<HexDiffResult> ComputeAsync(
            IHexDataSource leftSource, long leftOffset, long leftLength,
            IHexDataSource rightSource, long rightOffset, long rightLength,
            IProgress<HexDiffProgress>? progress,
            CancellationToken ct) => Task.FromResult(new HexDiffResult([], []));
    }

    private sealed class InlineUiDispatcher : IUiDispatcher
    {
        public void Invoke(Action action) => action();
        public void Post(Action action) => action();
        public void Post(Action action, UiDispatcherPriority priority) => action();
        public bool CheckAccess() => true;
    }

    private static FileCompareViewModel CreateViewModel() =>
        new(
            new InertFileCompareService(),
            new AvaloniaFileDialogService(static () => null), // headless: dialogs no-op, never block
            new InertHexDiffComputer(),
            new InlineUiDispatcher());

    private static (Window window, FileCompareViewModel vm) Show(FileCompareViewModel vm)
    {
        var window = new Window { Width = 1200, Height = 900, Content = new FileCompareView { DataContext = vm } };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return (window, vm);
    }

    /// <summary>
    /// Both Browse buttons announced the bare word "Browse", and this view is the sharpest case for
    /// why that matters: the two are IDENTICAL in every visible respect except which side of the
    /// window they sit on, so a screen-reader user had no way at all to tell the left picker from
    /// the right. They now say which side, matching the vocabulary the sibling Close buttons'
    /// tooltips already use ("Close left file").
    /// <para>
    /// Literal expected names; each button resolved by its bound command, never by the name under
    /// test. Label-in-Name (WCAG 2.5.3) is asserted alongside — the visible label is the bare
    /// "Browse", which both names contain.
    /// </para>
    /// </summary>
    [AvaloniaFact]
    public void BrowseButtons_DistinguishLeftFromRight_AndContainTheirVisibleLabel()
    {
        FileCompareViewModel vm = CreateViewModel();
        (Window window, _) = Show(vm);
        try
        {
            Button left = window.GetVisualDescendants().OfType<Button>()
                .Single(b => ReferenceEquals(b.Command, vm.BrowseLeftCommand));
            Button right = window.GetVisualDescendants().OfType<Button>()
                .Single(b => ReferenceEquals(b.Command, vm.BrowseRightCommand));

            Assert.Equal("Browse", left.Content as string);
            Assert.Equal("Browse", right.Content as string);

            string leftName = Avalonia.Automation.Peers.ControlAutomationPeer.CreatePeerForElement(left).GetName();
            string rightName = Avalonia.Automation.Peers.ControlAutomationPeer.CreatePeerForElement(right).GetName();

            Assert.Equal("Browse for left file", leftName);
            Assert.Equal("Browse for right file", rightName);
            Assert.NotEqual(leftName, rightName);
            Assert.Contains("Browse", leftName, StringComparison.Ordinal);
            Assert.Contains("Browse", rightName, StringComparison.Ordinal);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void EmptyView_NoFilesLoaded_NoBindingErrors()
    {
        FileCompareViewModel vm = CreateViewModel();

        using var sink = new BindingErrorSink();
        (Window window, _) = Show(vm);

        // The two symmetric panels realize.
        Assert.Equal(2, window.GetVisualDescendants().OfType<TreeView>().Count());
        Assert.Equal(2, window.GetVisualDescendants().OfType<DataGrid>().Count());

        Assert.Empty(sink.Messages);
    }

    [AvaloniaFact]
    public void SymmetricPanels_HaveGridsColumnsTreesHexViewsAndBytesPerRowSelectors_NoBindingErrors()
    {
        FileCompareViewModel vm = CreateViewModel();

        using var sink = new BindingErrorSink();
        (Window window, _) = Show(vm);

        // Both property DataGrids exist, each with the two expected columns (Property template + Value).
        DataGrid[] grids = [.. window.GetVisualDescendants().OfType<DataGrid>()];
        Assert.Equal(2, grids.Length);
        foreach (DataGrid grid in grids)
        {
            Assert.Equal(2, grid.Columns.Count);
            Assert.IsType<DataGridTemplateColumn>(grid.Columns[0]);
            Assert.Equal("Property", grid.Columns[0].Header);
            Assert.IsType<DataGridTextColumn>(grid.Columns[1]);
            Assert.Equal("Value", grid.Columns[1].Header);
        }

        // Both structure trees exist.
        Assert.Equal(2, window.GetVisualDescendants().OfType<TreeView>().Count());

        // Column headers carry the v1.9 panel-toned band (SurfaceBackground), not Fluent's
        // near-black default — the port originally dropped the Background/border setters.
        var headers = window.GetVisualDescendants().OfType<DataGridColumnHeader>()
            .Where(h => h.Content is string s && (s == "Property" || s == "Value")).ToList();
        Assert.Equal(4, headers.Count);
        Assert.All(headers, h =>
        {
            Assert.Equal(Color.Parse("#FF2D2D30"),
                Assert.IsAssignableFrom<ISolidColorBrush>(h.Background).Color);
            Assert.Equal(new Thickness(0, 0, 1, 1), h.BorderThickness);
        });
        // Interior separator: Fluent's template element is retinted dark (v1.9 look) — its
        // stock #66FFFFFF reads as a bright line on the panel-toned band.
        var separator = headers[0].GetVisualDescendants()
            .OfType<Avalonia.Controls.Shapes.Rectangle>().First(r => r.Name == "VerticalSeparator");
        Assert.Equal(Color.Parse("#FF333333"),
            Assert.IsAssignableFrom<ISolidColorBrush>(separator.Fill).Color);

        // Two embedded HexView composites (one per side).
        Assert.Equal(2, window.GetVisualDescendants().OfType<HexView>().Count());

        // Two ComboBox bytes/row selectors (fixed-choice preset dropdowns), both bound to the shared
        // HexBytesPerLine (default 16).
        ComboBox[] selectors = [.. window.GetVisualDescendants().OfType<ComboBox>()];
        Assert.Equal(2, selectors.Length);
        Assert.All(selectors, cb => Assert.Equal(16, cb.SelectedItem));

        // VM → both selectors reflect a changed value.
        vm.HexBytesPerLine = 32;
        Dispatcher.UIThread.RunJobs();
        Assert.All(selectors, cb => Assert.Equal(32, cb.SelectedItem));

        Assert.Empty(sink.Messages);
    }

    [AvaloniaFact]
    public void SeededPropertiesAndTreeNode_RealizeRowsAndNodes_NoBindingErrors()
    {
        FileCompareViewModel vm = CreateViewModel();

        // A normal row, a diff row (IsDifferent) and an indented row (IsIndented) exercise every
        // diff/indent converter binding on the property grid; a diff tree node exercises the tree
        // foreground converter.
        vm.LeftProperties.Add(new PropertyItem { Name = "Format", Value = "RAR4" });
        vm.LeftProperties.Add(new PropertyItem { Name = "CRC", Value = "DEADBEEF", IsDifferent = true });
        vm.LeftProperties.Add(new PropertyItem { Name = "  Method", Value = "Store", IsIndented = true });
        vm.RightProperties.Add(new PropertyItem { Name = "Format", Value = "RAR5" });
        vm.LeftTreeRoots.Add(new TreeNodeViewModel { Text = "Archive", IsDifferent = true });

        using var sink = new BindingErrorSink();
        (Window window, _) = Show(vm);

        // The left grid realized a row per seeded property.
        DataGrid leftGrid = window.GetVisualDescendants().OfType<DataGrid>()
            .Single(g => g.Name == "LeftPropertiesGrid");
        Assert.Same(vm.LeftProperties, leftGrid.ItemsSource);
        int leftRows = window.GetVisualDescendants().OfType<DataGridRow>()
            .Count(r => r.DataContext is PropertyItem p && vm.LeftProperties.Contains(p));
        Assert.Equal(vm.LeftProperties.Count, leftRows);

        // The seeded tree node realized as a TreeViewItem.
        Assert.Contains(window.GetVisualDescendants().OfType<TreeViewItem>(),
            i => i.DataContext is TreeNodeViewModel { Text: "Archive" });

        Assert.Empty(sink.Messages);
    }

    [AvaloniaFact]
    public void PropertyNameColumn_RealizesV19Foregrounds_NeverTheBlackDefault()
    {
        // The 840fb8f cell-level fix cured black-on-tinted-rows for the CELL, but the name
        // column's own TextBlock was missed: its single-key IsIndented binding returned
        // UnsetValue for non-indented rows and DataGrid content inheritance is unreliable, so
        // "Block Type"-style names rendered TextBlock's BLACK default (user screenshot,
        // ~1.3:1 on the panel). v1.9 spec: indented -> Medium; non-indented -> the row's diff
        // state (AccentError on diff rows, primary otherwise).
        FileCompareViewModel vm = CreateViewModel();
        vm.LeftProperties.Add(new PropertyItem { Name = "Block Type", Value = "File Header" });
        vm.LeftProperties.Add(new PropertyItem { Name = "Header CRC", Value = "0x1325", IsDifferent = true });
        vm.LeftProperties.Add(new PropertyItem { Name = "  Method", Value = "Store", IsIndented = true });
        vm.RightProperties.Add(new PropertyItem { Name = "Right Plain", Value = "x" }); // right grid: same template, own site

        using var sink = new BindingErrorSink();
        (Window window, _) = Show(vm);

        TextBlock NameBlock(string name) => window.GetVisualDescendants().OfType<DataGridRow>()
            .Single(r => r.DataContext is PropertyItem p && p.Name == name)
            .GetVisualDescendants().OfType<TextBlock>().First(t => t.Text == name);

        Avalonia.Media.Color Fg(TextBlock t) =>
            Assert.IsAssignableFrom<Avalonia.Media.ISolidColorBrush>(t.Foreground).Color;

        // Non-indented, non-diff: primary — the regression pin (was #FF000000).
        Assert.Equal(Avalonia.Media.Color.Parse("#FFD4D4D4"), Fg(NameBlock("Block Type")));
        // Non-indented, diff: the v1.9 row trigger made names red too.
        Assert.Equal(Avalonia.Media.Color.Parse("#FFF44747"), Fg(NameBlock("Header CRC")));
        // Indented: Medium — numerically identical to ForegroundPrimary today (#D4D4D4 both),
        // so this assert CANNOT distinguish the token routing until the two values diverge.
        Assert.Equal(Avalonia.Media.Color.Parse("#FFD4D4D4"), Fg(NameBlock("  Method")));
        // The right grid's template is a separate markup site — pin it too.
        Assert.Equal(Avalonia.Media.Color.Parse("#FFD4D4D4"), Fg(NameBlock("Right Plain")));

        // The real app REPOPULATES the grid on every tree-node click, recycling row containers.
        // A recycled name TextBlock that previously carried the indented Medium brush and gets
        // rebound to a NON-indented item is where the black default appeared in the field
        // (initial population renders fine — the user's screenshot state only occurs after a
        // selection change). Rebind indented->plain in the same container positions.
        vm.LeftProperties.Clear();
        vm.LeftProperties.Add(new PropertyItem { Name = "  Packed Size", Value = "1", IsIndented = true });
        vm.LeftProperties.Add(new PropertyItem { Name = "  Unpacked", Value = "2", IsIndented = true });
        vm.LeftProperties.Add(new PropertyItem { Name = "  OS", Value = "3", IsIndented = true });
        Dispatcher.UIThread.RunJobs();
        vm.LeftProperties.Clear();
        vm.LeftProperties.Add(new PropertyItem { Name = "Start Offset", Value = "0x14" });
        vm.LeftProperties.Add(new PropertyItem { Name = "Header Size", Value = "49 bytes" });
        vm.LeftProperties.Add(new PropertyItem { Name = "Flags", Value = "0x90C2", IsDifferent = true });
        Dispatcher.UIThread.RunJobs();

        // NOTE: these recycled-phase asserts are the LOAD-BEARING part of this test — the
        // initial-population asserts above pass even under the old single-key binding (fresh
        // binds inherit correctly). Do not "simplify" this test by dropping the repopulation.
        Assert.Equal(Avalonia.Media.Color.Parse("#FFD4D4D4"), Fg(NameBlock("Start Offset")));
        Assert.Equal(Avalonia.Media.Color.Parse("#FFD4D4D4"), Fg(NameBlock("Header Size")));
        Assert.Equal(Avalonia.Media.Color.Parse("#FFF44747"), Fg(NameBlock("Flags")));

        Assert.Empty(sink.Messages);
    }

    [AvaloniaFact]
    public void BoolToBrushConverter_MapsTrueToTokenBrush_AndFalseOrNonBoolToUnset()
    {
        var converter = new BoolToBrushConverter();

        // true + a real Tokens key → the exact token brush (locks the diff-tint contract).
        object? on = converter.Convert(true, typeof(IBrush), "AccentError", CultureInfo.InvariantCulture);
        ISolidColorBrush brush = Assert.IsAssignableFrom<ISolidColorBrush>(on);
        Assert.Equal(Color.Parse("#FFF44747"), brush.Color);

        // false, null, non-bool, and unknown keys all fall back so the target inherits its theme default.
        Assert.Equal(AvaloniaProperty.UnsetValue, converter.Convert(false, typeof(IBrush), "AccentError", CultureInfo.InvariantCulture));
        Assert.Equal(AvaloniaProperty.UnsetValue, converter.Convert(null, typeof(IBrush), "AccentError", CultureInfo.InvariantCulture));
        Assert.Equal(AvaloniaProperty.UnsetValue, converter.Convert("nope", typeof(IBrush), "AccentError", CultureInfo.InvariantCulture));
        Assert.Equal(AvaloniaProperty.UnsetValue, converter.Convert(true, typeof(IBrush), "NoSuchKey", CultureInfo.InvariantCulture));
    }

    [AvaloniaFact]
    public void BytesPerRowComboBox_ExposesFixedPresets_AndSelectionRoundTripsToVm()
    {
        FileCompareViewModel vm = CreateViewModel();

        using var sink = new BindingErrorSink();
        (Window window, _) = Show(vm);

        // The ComboBox is non-editable (Avalonia has no free-text entry, unlike WPF's NumericUpDown), so
        // the only way to change HexBytesPerLine is picking one of the fixed presets.
        ComboBox selector = window.GetVisualDescendants().OfType<ComboBox>().First();
        Assert.Equal([8, 16, 24, 32, 48, 64], selector.Items.OfType<int>());

        selector.SelectedItem = 32;
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(32, vm.HexBytesPerLine);
        Assert.InRange(vm.HexBytesPerLine, 1, 128);
        Assert.Empty(sink.Messages);
    }

    [AvaloniaFact]
    public void PropertyGridCopyValue_LeftPane_CopiesSelectedValueToClipboard()
    {
        FileCompareViewModel vm = CreateViewModel();
        var item = new PropertyItem { Name = "CRC", Value = "DEADBEEF" };
        vm.LeftProperties.Add(item);
        vm.SelectedLeftProperty = item;

        var view = new FileCompareView { DataContext = vm };
        var window = new Window { Width = 1200, Height = 900, Content = view };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        DataGrid leftGrid = window.GetVisualDescendants().OfType<DataGrid>().Single(g => g.Name == "LeftPropertiesGrid");
        var menu = Assert.IsType<ContextMenu>(leftGrid.ContextMenu);

        // Open the menu the way the framework auto-opens a DataGrid.ContextMenu: Avalonia sets the
        // POPUP's PlacementTarget but never the ContextMenu's own PlacementTarget property. The old
        // resolver read ContextMenu.PlacementTarget, which stays null → Copy did nothing.
        menu.Open(leftGrid);
        Dispatcher.UIThread.RunJobs();
        Assert.Null(menu.PlacementTarget); // documents the dead-menu root cause

        MenuItem copyValue = menu.Items.OfType<MenuItem>().Single(m => (string?)m.Header == "Copy Value");
        copyValue.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        Dispatcher.UIThread.RunJobs();

        IClipboard clipboard = TopLevel.GetTopLevel(view)!.Clipboard!;
        string? copied = clipboard.TryGetTextAsync().GetAwaiter().GetResult();
        Assert.Equal("DEADBEEF", copied);
    }

    [AvaloniaFact]
    public void PropertyGridCopyValue_RightPane_CopiesThatPanesSelection()
    {
        // Two panes share one handler set, so the resolver must read the RIGHT pane's selection when
        // the right grid's menu is used — not the left's.
        FileCompareViewModel vm = CreateViewModel();
        vm.LeftProperties.Add(new PropertyItem { Name = "CRC", Value = "LEFTVALUE" });
        vm.SelectedLeftProperty = vm.LeftProperties[0];
        var rightItem = new PropertyItem { Name = "CRC", Value = "RIGHTVALUE" };
        vm.RightProperties.Add(rightItem);
        vm.SelectedRightProperty = rightItem;

        var view = new FileCompareView { DataContext = vm };
        var window = new Window { Width = 1200, Height = 900, Content = view };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        DataGrid rightGrid = window.GetVisualDescendants().OfType<DataGrid>().Single(g => g.Name == "RightPropertiesGrid");
        var menu = Assert.IsType<ContextMenu>(rightGrid.ContextMenu);
        menu.Open(rightGrid);
        Dispatcher.UIThread.RunJobs();

        MenuItem copyValue = menu.Items.OfType<MenuItem>().Single(m => (string?)m.Header == "Copy Value");
        copyValue.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        Dispatcher.UIThread.RunJobs();

        IClipboard clipboard = TopLevel.GetTopLevel(view)!.Clipboard!;
        Assert.Equal("RIGHTVALUE", clipboard.TryGetTextAsync().GetAwaiter().GetResult());
    }

    [AvaloniaFact]
    public void LeftFilePathTextBox_ReflectsViewModel_NoBindingErrors()
    {
        FileCompareViewModel vm = CreateViewModel();
        vm.LeftFilePath = @"D:\rel\left.srr";

        using var sink = new BindingErrorSink();
        (Window window, _) = Show(vm);

        TextBox left = window.GetVisualDescendants().OfType<TextBox>()
            .Single(t => t.Name == "LeftFilePathTextBox");
        Assert.Equal(@"D:\rel\left.srr", left.Text);

        // VM → view updates flow through.
        vm.LeftFilePath = @"D:\rel\other.srr";
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(@"D:\rel\other.srr", left.Text);

        Assert.Empty(sink.Messages);
    }
}
