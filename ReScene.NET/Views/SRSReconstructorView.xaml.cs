using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using ReScene.NET.Helpers;
using ReScene.NET.ViewModels;

namespace ReScene.NET.Views;

public partial class SRSReconstructorView : UserControl
{
    private ISOProgressWindow? _iSOWindow;

    public SRSReconstructorView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        Loaded += OnLoaded;
    }

    private void OnLoaded(object _, RoutedEventArgs e)
    {
        if (DataContext is not SRSReconstructorViewModel vm)
        {
            return;
        }

        TextBoxDropHelper.SetupFileDrop(SRSFileTextBox, path => vm.SRSFilePath = path);
        TextBoxDropHelper.SetupFileDrop(MediaFileTextBox, path => vm.MediaFilePath = path);
        TextBoxDropHelper.SetupFileDrop(OutputTextBox, path => vm.OutputPath = path);
    }

    private void OnDataContextChanged(object _, DependencyPropertyChangedEventArgs e)
    {
        if (e.OldValue is SRSReconstructorViewModel oldVm)
        {
            oldVm.PropertyChanged -= OnVmPropertyChanged;
        }

        if (e.NewValue is SRSReconstructorViewModel newVm)
        {
            newVm.PropertyChanged += OnVmPropertyChanged;
        }
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(SRSReconstructorViewModel.ISOProcessing))
        {
            return;
        }

        if (sender is not SRSReconstructorViewModel vm)
        {
            return;
        }

        if (vm.ISOProcessing)
        {
            Dispatcher.BeginInvoke(DispatcherPriority.Normal, () =>
            {
                _iSOWindow = new ISOProgressWindow
                {
                    Owner = Window.GetWindow(this),
                    DataContext = DataContext
                };

                _iSOWindow.Closed += (_, _) =>
                {
                    // If the window was cancelled (not closed by code), cancel the operation
                    if (vm.ISOProcessing)
                    {
                        vm.CancelRebuildCommand.Execute(null);
                    }

                    _iSOWindow = null;
                };

                _iSOWindow.ShowDialog();
            });
        }
        else
        {
            Dispatcher.BeginInvoke(() =>
            {
                _iSOWindow?.Close();
                _iSOWindow = null;
            });
        }
    }
}
