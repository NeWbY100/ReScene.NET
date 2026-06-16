using System.Diagnostics;
using System.Windows;
using System.Windows.Navigation;
using ReScene.NET.Helpers;

namespace ReScene.NET.Views;

public partial class AboutWindow : Window
{
    public AboutWindow(string appVersion)
    {
        InitializeComponent();
        DataContext = new AboutInfo(appVersion);
        SourceInitialized += (_, _) => DarkTitleBar.Enable(this);
    }

    private void OnHyperlinkRequestNavigate(object _, RequestNavigateEventArgs e)
    {
        Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
        e.Handled = true;
    }

    private sealed record AboutInfo(string AppVersion);
}
