using System.Windows;
using ReScene.NET.Helpers;
using ReScene.NET.ViewModels;

namespace ReScene.NET.Views;

public partial class SettingsWindow : Window
{
    public SettingsWindow()
    {
        InitializeComponent();
        SourceInitialized += (_, _) => DarkTitleBar.Enable(this);
    }

    private void OnSaveClick(object _, RoutedEventArgs e)
    {
        if (DataContext is SettingsViewModel vm && vm.DialogResult)
        {
            DialogResult = true;
            Close();
        }
    }

    private void OnCancelClick(object _, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
