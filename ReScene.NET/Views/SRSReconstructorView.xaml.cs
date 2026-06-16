using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using ReScene.NET.Helpers;
using ReScene.NET.ViewModels;

namespace ReScene.NET.Views;

public partial class SRSReconstructorView : UserControl
{
    private IsoProgressWindowController? _isoController;

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

        _isoController = null;

        if (e.NewValue is SRSReconstructorViewModel newVm)
        {
            // Forward cancellation to the existing generated CancelRebuildCommand.
            _isoController = new IsoProgressWindowController(
                this, () => newVm.ISOProcessing, () => newVm.CancelRebuildCommand.Execute(null));
            newVm.PropertyChanged += OnVmPropertyChanged;
        }
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(SRSReconstructorViewModel.ISOProcessing))
        {
            return;
        }

        if (sender is SRSReconstructorViewModel vm)
        {
            _isoController?.OnProcessingChanged(vm.ISOProcessing);
        }
    }
}
