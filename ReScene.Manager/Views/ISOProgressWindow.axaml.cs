using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;

namespace ReScene.Manager.Views;

/// <summary>
/// Modal ISO-processing progress dialog shared by the SRS Creator and SRS Reconstructor tabs, ported
/// from the WPF <c>ReScene.NET.Views.ISOProgressWindow</c>. Its <see cref="Avalonia.StyledElement.DataContext"/> is
/// the same SRS view model as the owning tab, so every binding here reads that VM's <c>ISO*</c>
/// progress properties directly. Opened/closed by <see cref="Helpers.IsoProgressWindowController"/>;
/// the WPF <c>DarkTitleBar.Enable</c> call is dropped since the app-wide dark theme already covers
/// this window's chrome.
/// </summary>
public partial class ISOProgressWindow : Window
{
    public ISOProgressWindow()
    {
        AvaloniaXamlLoader.Load(this);
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e) => Close();
}
