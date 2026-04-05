using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using ReScene.NET.Helpers;
using ReScene.NET.ViewModels;

namespace ReScene.NET.Views;

public partial class SrsReconstructorView : UserControl
{
    private IsoProgressWindow? _isoWindow;

    public SrsReconstructorView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        Loaded += OnLoaded;
    }

    private void OnLoaded(object _, RoutedEventArgs e)
    {
        if (DataContext is not SrsReconstructorViewModel vm)
        {
            return;
        }

        TextBoxDropHelper.SetupFileDrop(SrsFileTextBox, path => vm.SrsFilePath = path);
        TextBoxDropHelper.SetupFileDrop(MediaFileTextBox, path => vm.MediaFilePath = path);
        TextBoxDropHelper.SetupFileDrop(OutputTextBox, path => vm.OutputPath = path);
    }

    private void OnDataContextChanged(object _, DependencyPropertyChangedEventArgs e)
    {
        if (e.OldValue is SrsReconstructorViewModel oldVm)
        {
            oldVm.PropertyChanged -= OnVmPropertyChanged;
        }

        if (e.NewValue is SrsReconstructorViewModel newVm)
        {
            newVm.PropertyChanged += OnVmPropertyChanged;
        }
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(SrsReconstructorViewModel.IsoProcessing))
        {
            return;
        }

        if (sender is not SrsReconstructorViewModel vm)
        {
            return;
        }

        if (vm.IsoProcessing)
        {
            Dispatcher.BeginInvoke(DispatcherPriority.Normal, () =>
            {
                _isoWindow = new IsoProgressWindow
                {
                    Owner = Window.GetWindow(this),
                    DataContext = DataContext
                };

                _isoWindow.Closed += (_, _) =>
                {
                    // If the window was cancelled (not closed by code), cancel the operation
                    if (vm.IsoProcessing)
                    {
                        vm.CancelRebuildCommand.Execute(null);
                    }

                    _isoWindow = null;
                };

                _isoWindow.ShowDialog();
            });
        }
        else
        {
            Dispatcher.BeginInvoke(() =>
            {
                _isoWindow?.Close();
                _isoWindow = null;
            });
        }
    }
}
