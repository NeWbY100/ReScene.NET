using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace ReScene.Manager.Views;

/// <summary>
/// The advanced-mode shell: an 8-tab <see cref="TabControl"/> whose selected index is bound to
/// <c>MainWindowViewModel.SelectedTabIndex</c>. Every tab hosts its real view (Home, Inspector,
/// SRR Creator, SRS Creator, Reconstructor, SRS Reconstructor, Sample Restorer, Compare), each
/// bound to its child ViewModel.
/// </summary>
public partial class AdvancedShellView : UserControl
{
    public AdvancedShellView()
    {
        AvaloniaXamlLoader.Load(this);
    }
}
