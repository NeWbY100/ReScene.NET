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

    // Select the tree item under the mouse on right-click so the context menu
    // operates on the right-clicked item, not the previously selected one.
    private void OnTreeViewPreviewMouseRightButtonDown(object _, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject source)
        {
            var treeViewItem = FindVisualParent<TreeViewItem>(source);
            if (treeViewItem is not null)
            {
                treeViewItem.IsSelected = true;
                treeViewItem.Focus();
                e.Handled = true;
            }
        }
    }

    private static T? FindVisualParent<T>(DependencyObject child) where T : DependencyObject
    {
        var current = child;
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
