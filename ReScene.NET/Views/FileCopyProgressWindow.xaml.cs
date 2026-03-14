using System.ComponentModel;
using System.Windows;
using ReScene.NET.Helpers;
using ReScene.NET.ViewModels;

namespace ReScene.NET.Views;

public partial class FileCopyProgressWindow : Window
{
    public FileCopyProgressWindow()
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
        if (e.PropertyName != nameof(ReconstructorViewModel.IsCopying))
            return;

        if (sender is ReconstructorViewModel { IsCopying: false })
        {
            // Copy finished — auto-close
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
        if (DataContext is ReconstructorViewModel { IsCopying: true } vm)
        {
            // Don't close while copy is in progress — cancel instead
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
