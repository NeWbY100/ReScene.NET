using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using ReScene.NET.Helpers;
using ReScene.NET.ViewModels;

namespace ReScene.NET.Views;

public partial class SRSCreatorView : UserControl
{
    private ISOProgressWindow? _iSOWindow;

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

        if (e.NewValue is SRSCreatorViewModel newVm)
        {
            newVm.PropertyChanged += OnVmPropertyChanged;
        }
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(SRSCreatorViewModel.ISOProcessing))
        {
            return;
        }

        if (sender is not SRSCreatorViewModel vm)
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
                    if (vm.ISOProcessing)
                    {
                        vm.CancelCreationCommand.Execute(null);
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
