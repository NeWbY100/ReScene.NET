using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using ReScene.NET.ViewModels;

namespace ReScene.NET.Views;

public partial class InspectorView : UserControl
{
    public InspectorView()
    {
        InitializeComponent();
    }

    // WPF TreeView doesn't support two-way binding on SelectedItem,
    // so we handle it in code-behind.
    private void TreeView_SelectedItemChanged(object _, RoutedPropertyChangedEventArgs<object> e)
    {
        if (DataContext is InspectorViewModel vm)
        {
            vm.SelectedTreeNode = e.NewValue as TreeNodeViewModel;
        }
    }

    // Double-clicking an image stored-file node opens the preview, mirroring the header button.
    private void OnTreeViewMouseDoubleClick(object _, MouseButtonEventArgs e)
    {
        if (DataContext is InspectorViewModel vm && vm.PreviewStoredImageCommand.CanExecute(null))
        {
            vm.PreviewStoredImageCommand.Execute(null);
            e.Handled = true;
        }
    }

    // Select the tree item under the mouse on right-click so the context menu
    // operates on the right-clicked item, not the previously selected one.
    private void OnTreeViewPreviewMouseRightButtonDown(object _, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject source)
        {
            TreeViewItem? treeViewItem = FindVisualParent<TreeViewItem>(source);
            if (treeViewItem is not null)
            {
                treeViewItem.IsSelected = true;
                treeViewItem.Focus();
                e.Handled = true;
            }
        }
    }

    private void OnHexSearchBarVisibleChanged(object _, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is true)
        {
            Dispatcher.BeginInvoke(() =>
            {
                HexSearchBox.Focus();
                HexSearchBox.SelectAll();
            });
        }
    }

    private void OnCopyPropertyNameClick(object _, RoutedEventArgs e)
    {
        if (PropertiesGrid.SelectedItem is PropertyItem item)
        {
            Clipboard.SetText(item.Name);
        }
    }

    private void OnCopyPropertyValueClick(object _, RoutedEventArgs e)
    {
        if (PropertiesGrid.SelectedItem is PropertyItem item)
        {
            Clipboard.SetText(item.Value);
        }
    }

    private void OnCopyPropertyClick(object _, RoutedEventArgs e)
    {
        if (PropertiesGrid.SelectedItem is PropertyItem item)
        {
            Clipboard.SetText($"{item.Name}: {item.Value}");
        }
    }

    private static T? FindVisualParent<T>(DependencyObject child) where T : DependencyObject
    {
        DependencyObject? current = child;
        while (current is not null)
        {
            if (current is T match)
            {
                return match;
            }

            current = VisualTreeHelper.GetParent(current);
        }

        return null;
    }
}
