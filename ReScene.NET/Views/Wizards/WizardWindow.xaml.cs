using System.Windows;
using ReScene.NET.Helpers;
using ReScene.NET.ViewModels.Wizards;

namespace ReScene.NET.Views.Wizards;

public partial class WizardWindow : Window
{
    public WizardWindow(WizardViewModel viewModel, FrameworkElement body)
    {
        InitializeComponent();
        DataContext = viewModel;
        body.DataContext = viewModel.Content; // step fields bind to the task VM
        BodyHost.Content = body;
        SourceInitialized += (_, _) => DarkTitleBar.Enable(this);
        Closed += (_, _) => (DataContext as IDisposable)?.Dispose();
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();
}
