using System.Windows;
using ReScene.NET.Helpers;

namespace ReScene.NET.Views;

public partial class ISOProgressWindow : Window
{
    public ISOProgressWindow()
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
