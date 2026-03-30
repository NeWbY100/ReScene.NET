using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using ReScene.NET.ViewModels;

namespace ReScene.NET.Views;

public partial class ReconstructorView : UserControl
{
    public ReconstructorView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object _, DependencyPropertyChangedEventArgs e)
    {
        if (e.OldValue is ReconstructorViewModel oldVm)
        {
            oldVm.PropertyChanged -= OnVmPropertyChanged;
        }

        if (e.NewValue is ReconstructorViewModel newVm)
        {
            newVm.PropertyChanged += OnVmPropertyChanged;
        }
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(ReconstructorViewModel.IsRunning))
        {
            return;
        }

        if (sender is ReconstructorViewModel { IsRunning: true })
        {
            // Defer ShowDialog so StartAsync can reach its await point first.
            // ShowDialog blocks the UI thread, so opening it synchronously from
            // the PropertyChanged handler would prevent the brute force from starting.
            // Use Normal priority so the window opens before progress events are processed.
            Dispatcher.BeginInvoke(DispatcherPriority.Normal, () =>
            {
                var window = new BruteForceProgressWindow
                {
                    Owner = Window.GetWindow(this),
                    DataContext = DataContext
                };
                window.ShowDialog();
            });
        }
    }
}
