using System.Windows;
using System.Windows.Controls;
using ReScene.NET.ViewModels;

namespace ReScene.NET.Views;

public partial class FileCompareView : UserControl
{
    public FileCompareView()
    {
        InitializeComponent();
    }

    private void LeftTree_SelectedItemChanged(object _, RoutedPropertyChangedEventArgs<object> e)
    {
        if (DataContext is FileCompareViewModel vm)
        {
            vm.SelectedLeftTreeNode = e.NewValue as TreeNodeViewModel;
        }
    }

    private void RightTree_SelectedItemChanged(object _, RoutedPropertyChangedEventArgs<object> e)
    {
        if (DataContext is FileCompareViewModel vm)
        {
            vm.SelectedRightTreeNode = e.NewValue as TreeNodeViewModel;
        }
    }
}
