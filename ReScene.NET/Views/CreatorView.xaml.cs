using System.Windows;
using System.Windows.Controls;
using ReScene.NET.Helpers;
using ReScene.NET.ViewModels;

namespace ReScene.NET.Views;

public partial class CreatorView : UserControl
{
    public CreatorView()
    {
        InitializeComponent();

        Loaded += OnLoaded;
    }

    private void OnLoaded(object _, RoutedEventArgs e)
    {
        if (DataContext is not CreatorViewModel vm)
        {
            return;
        }

        TextBoxDropHelper.SetupFileDrop(InputTextBox, path => vm.InputPath = path);
        TextBoxDropHelper.SetupFileDrop(OutputTextBox, path => vm.OutputPath = path);
    }

    private void OnStoredFilesDragOver(object _, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            e.Effects = DragDropEffects.Copy;
        }
        else
        {
            e.Effects = DragDropEffects.None;
        }

        e.Handled = true;
    }

    private void OnStoredFilesDrop(object _, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] files || files.Length == 0)
        {
            return;
        }

        if (DataContext is CreatorViewModel vm)
        {
            vm.AddStoredFiles(files);
        }

        e.Handled = true;
    }
}
