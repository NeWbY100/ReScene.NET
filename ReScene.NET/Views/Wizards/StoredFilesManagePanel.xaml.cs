using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using ReScene.NET.Models;
using ReScene.NET.ViewModels;

namespace ReScene.NET.Views.Wizards;

/// <summary>
/// The stored-file management UI (grid + toolbar) shared by the Edit-an-SRR and Create-an-SRR
/// wizards. Its <see cref="FrameworkElement.DataContext"/> is a <see cref="SrrEditorViewModel"/>.
/// </summary>
public partial class StoredFilesManagePanel : UserControl
{
    public StoredFilesManagePanel() => InitializeComponent();

    // DataGrid.SelectedItems isn't bindable, so the view forwards the multi-selection to the VM.
    private void StoredFilesGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is DataGrid grid && DataContext is SrrEditorViewModel vm)
        {
            vm.SetSelection(grid.SelectedItems.OfType<StoredFileInfo>().ToList());
        }
    }

    // Left-clicking empty space in the grid (not a row, header, or scrollbar) clears the selection.
    // Right/middle clicks are left alone so they don't fight a future context menu.
    private void StoredFilesGrid_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not DataGrid grid || e.ChangedButton != MouseButton.Left)
        {
            return;
        }

        DependencyObject? source = e.OriginalSource as DependencyObject;
        if (FindAncestor<DataGridRow>(source) is not null
            || FindAncestor<ScrollBar>(source) is not null
            || FindAncestor<DataGridColumnHeader>(source) is not null)
        {
            return;
        }

        grid.UnselectAll();
    }

    private static T? FindAncestor<T>(DependencyObject? current) where T : DependencyObject
    {
        while (current is not null and not T)
        {
            // Visuals ascend the visual tree; ContentElements (e.g. a text Run) have no visual
            // parent, so fall back to the logical tree to keep climbing toward the owning row.
            current = current is Visual
                ? VisualTreeHelper.GetParent(current)
                : LogicalTreeHelper.GetParent(current);
        }

        return current as T;
    }
}
