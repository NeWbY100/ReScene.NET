using System.Windows;
using System.Windows.Controls;
using ReScene.NET.ViewModels;
using ReScene.NET.Views.Wizards;

namespace ReScene.NET.Views;

public partial class BeginnerShellView : UserControl
{
    public BeginnerShellView() => InitializeComponent();

    private void OnCardClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.Tag is not BeginnerCard card)
        {
            return;
        }

        if (DataContext is not BeginnerShellViewModel shell)
        {
            return;
        }

        (var wizardVm, var body) = BeginnerWizardFactory.Create(card, shell);
        var window = new WizardWindow(wizardVm, body) { Owner = Window.GetWindow(this) };
        window.ShowDialog();
    }
}
