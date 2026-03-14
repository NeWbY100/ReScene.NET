using System.Windows;
using System.Windows.Controls;
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
            vm.SelectedTreeNode = e.NewValue as TreeNodeViewModel;
    }
}
