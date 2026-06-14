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
        // WPF raises Click before executing a bound Command, so the save must be driven from
        // here — checking a command-set flag on first click would still see the old value.
        if (DataContext is SettingsViewModel vm)
        {
            vm.SaveCommand.Execute(null);
            if (vm.DialogResult)
            {
                DialogResult = true;
            }
        }
    }
}
