using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using ReScene.NET.Helpers;
using ReScene.NET.ViewModels;

namespace ReScene.NET.Views;

public partial class BruteForceProgressWindow : Window
{
    private bool _isCompleted;

    public BruteForceProgressWindow()
    {
        InitializeComponent();
        SourceInitialized += (_, _) => DarkTitleBar.Enable(this);
        Loaded += (_, _) =>
        {
            if (DataContext is ReconstructorViewModel vm)
            {
                vm.PropertyChanged += OnVmPropertyChanged;

                // Catch up with state that changed before we loaded
                if (vm.IsCopying)
                    ShowCopyWindow();
                else if (vm.IsVerifying)
                    ShowVerifyWindow();
            }
        };
    }

    private void ShowCopyWindow()
    {
        // Defer so we don't open a modal inside a PropertyChanged / Loaded handler
        Dispatcher.BeginInvoke(() =>
        {
            if (DataContext is not ReconstructorViewModel { IsCopying: true })
                return;

            var copyWindow = new FileCopyProgressWindow
            {
                Owner = this,
                DataContext = DataContext,
            };
            copyWindow.ShowDialog();
        });
    }

    private void ShowVerifyWindow()
    {
        Dispatcher.BeginInvoke(() =>
        {
            if (DataContext is not ReconstructorViewModel { IsVerifying: true })
                return;

            var verifyWindow = new CrcValidationProgressWindow
            {
                Owner = this,
                DataContext = DataContext,
            };
            verifyWindow.ShowDialog();
        });
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ReconstructorViewModel.IsCopying))
        {
            if (sender is ReconstructorViewModel { IsCopying: true })
                ShowCopyWindow();
            return;
        }

        if (e.PropertyName == nameof(ReconstructorViewModel.IsVerifying))
        {
            if (sender is ReconstructorViewModel { IsVerifying: true })
                ShowVerifyWindow();
            return;
        }

        if (e.PropertyName != nameof(ReconstructorViewModel.IsRunning))
            return;

        if (sender is ReconstructorViewModel { IsRunning: false })
        {
            _isCompleted = true;
            btnStopClose.Content = "Close";
            btnStopClose.IsEnabled = true;
            btnStopClose.Style = (Style)FindResource("PrimaryButton");
        }
    }

    private void OnStopCloseClick(object _, RoutedEventArgs e)
    {
        if (_isCompleted)
        {
            Close();
            return;
        }

        if (DataContext is ReconstructorViewModel vm)
        {
            vm.StopCommand.Execute(null);
            btnStopClose.IsEnabled = false;
            btnStopClose.Content = "Stopping...";
        }
    }

    private void OnCopyArgumentsClick(object sender, RoutedEventArgs _)
    {
        if (sender is MenuItem menuItem
            && menuItem.Parent is ContextMenu contextMenu
            && contextMenu.PlacementTarget is DataGrid dataGrid
            && dataGrid.SelectedItem is ReconstructorViewModel.VersionEntry entry
            && !string.IsNullOrEmpty(entry.Arguments))
        {
            Clipboard.SetText(entry.Arguments);
        }
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_isCompleted)
        {
            e.Cancel = true;
            if (DataContext is ReconstructorViewModel vm)
            {
                vm.StopCommand.Execute(null);
                btnStopClose.IsEnabled = false;
                btnStopClose.Content = "Stopping...";
            }
            return;
        }

        if (DataContext is ReconstructorViewModel vmCleanup)
            vmCleanup.PropertyChanged -= OnVmPropertyChanged;

        base.OnClosing(e);
    }
}
