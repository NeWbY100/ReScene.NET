using System.ComponentModel;
using System.Windows;
using ReScene.NET.Helpers;
using ReScene.NET.ViewModels;

namespace ReScene.NET.Views;

public partial class CrcValidationProgressWindow : Window
{
    public CrcValidationProgressWindow()
    {
        InitializeComponent();
        SourceInitialized += (_, _) => DarkTitleBar.Enable(this);
        Loaded += (_, _) =>
        {
            if (DataContext is ReconstructorViewModel vm)
                vm.PropertyChanged += OnVmPropertyChanged;
        };
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(ReconstructorViewModel.IsVerifying))
            return;

        if (sender is ReconstructorViewModel { IsVerifying: false })
        {
            if (DataContext is ReconstructorViewModel vmCleanup)
                vmCleanup.PropertyChanged -= OnVmPropertyChanged;
            Close();
        }
    }

    private void OnCancelClick(object _, RoutedEventArgs e)
    {
        if (DataContext is ReconstructorViewModel vm)
        {
            vm.StopCommand.Execute(null);
            btnCancel.IsEnabled = false;
            btnCancel.Content = "Cancelling...";
        }
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (DataContext is ReconstructorViewModel { IsVerifying: true } vm)
        {
            e.Cancel = true;
            vm.StopCommand.Execute(null);
            btnCancel.IsEnabled = false;
            btnCancel.Content = "Cancelling...";
            return;
        }

        if (DataContext is ReconstructorViewModel vmCleanup)
            vmCleanup.PropertyChanged -= OnVmPropertyChanged;

        base.OnClosing(e);
    }
}
