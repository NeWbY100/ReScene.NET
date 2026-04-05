using System.Windows;
using ReScene.NET.Helpers;

namespace ReScene.NET.Views;

public partial class IsoProgressWindow : Window
{
    public IsoProgressWindow()
    {
        InitializeComponent();
        SourceInitialized += (_, _) => DarkTitleBar.Enable(this);
    }

    private void OnCancelClick(object _, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
