using System.Windows;
using ReScene.NET.Helpers;
using ReScene.NET.ViewModels;

namespace ReScene.NET.Views;

public partial class FilePreviewWindow : Window
{
    public FilePreviewWindow(FilePreviewViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        SourceInitialized += (_, _) => DarkTitleBar.Enable(this);
    }
}
