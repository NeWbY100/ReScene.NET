using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using ReScene.NET.Helpers;
using ReScene.NET.ViewModels;

namespace ReScene.NET.Views;

public partial class SRSCreatorView : UserControl
{
    private IsoProgressWindowController? _isoController;

    public SRSCreatorView()
    {
        InitializeComponent();

        DataContextChanged += OnDataContextChanged;
        Loaded += OnLoaded;
    }

    private void OnLoaded(object _, RoutedEventArgs e)
    {
        if (DataContext is not SRSCreatorViewModel vm)
        {
            return;
        }

        TextBoxDropHelper.SetupFileDrop(InputTextBox, path => vm.InputPath = path);
        TextBoxDropHelper.SetupFileDrop(OutputTextBox, path => vm.OutputPath = path);
    }

    private void OnDataContextChanged(object _, DependencyPropertyChangedEventArgs e)
    {
        if (e.OldValue is SRSCreatorViewModel oldVm)
        {
            oldVm.PropertyChanged -= OnVmPropertyChanged;
        }

        _isoController = null;

        if (e.NewValue is SRSCreatorViewModel newVm)
        {
            // Forward cancellation to the existing generated CancelCreationCommand.
            _isoController = new IsoProgressWindowController(
                this, () => newVm.ISOProcessing, () => newVm.CancelCreationCommand.Execute(null));
            newVm.PropertyChanged += OnVmPropertyChanged;
        }
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(SRSCreatorViewModel.ISOProcessing))
        {
            return;
        }

        if (sender is SRSCreatorViewModel vm)
        {
            _isoController?.OnProcessingChanged(vm.ISOProcessing);
        }
    }
}
