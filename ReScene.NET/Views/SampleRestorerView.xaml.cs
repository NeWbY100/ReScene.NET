using System.Windows.Controls;
using ReScene.NET.Helpers;
using ReScene.NET.ViewModels;

namespace ReScene.NET.Views;

public partial class SampleRestorerView : UserControl
{
    public SampleRestorerView()
    {
        InitializeComponent();

        Loaded += OnLoaded;
    }

    private void OnLoaded(object _, System.Windows.RoutedEventArgs e)
    {
        if (DataContext is not SampleRestorerViewModel vm)
        {
            return;
        }

        TextBoxDropHelper.SetupFileDrop(SrrFileTextBox, path => vm.SrrFilePath = path);
        TextBoxDropHelper.SetupFolderDrop(MediaDirTextBox, path => vm.MediaDirectoryPath = path);
        TextBoxDropHelper.SetupFolderDrop(OutputDirTextBox, path => vm.OutputDirectoryPath = path);
    }
}
