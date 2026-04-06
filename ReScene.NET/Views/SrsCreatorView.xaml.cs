using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using ReScene.NET.Helpers;
using ReScene.NET.ViewModels;

namespace ReScene.NET.Views;

public partial class SrsCreatorView : UserControl
{
    private IsoProgressWindow? _isoWindow;

    public SrsCreatorView()
    {
        InitializeComponent();

        DataContextChanged += OnDataContextChanged;
        Loaded += OnLoaded;
    }

    private void OnLoaded(object _, RoutedEventArgs e)
    {
        if (DataContext is not SrsCreatorViewModel vm)
        {
            return;
        }

        TextBoxDropHelper.SetupFileDrop(InputTextBox, path => vm.InputPath = path);
        TextBoxDropHelper.SetupFileDrop(OutputTextBox, path => vm.OutputPath = path);
    }

    private void OnDataContextChanged(object _, DependencyPropertyChangedEventArgs e)
    {
        if (e.OldValue is SrsCreatorViewModel oldVm)
        {
            oldVm.PropertyChanged -= OnVmPropertyChanged;
        }

        if (e.NewValue is SrsCreatorViewModel newVm)
        {
            newVm.PropertyChanged += OnVmPropertyChanged;
        }
    }

    private void OnBatchDragOver(object _, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            e.Effects = DragDropEffects.Copy;
        }
        else
        {
            e.Effects = DragDropEffects.None;
        }

        e.Handled = true;
    }

    private void OnBatchDrop(object _, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] files || files.Length == 0)
        {
            return;
        }

        if (DataContext is SrsCreatorViewModel vm)
        {
            vm.AddBatchFilePaths(files);
        }

        e.Handled = true;
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(SrsCreatorViewModel.IsoProcessing))
        {
            return;
        }

        if (sender is not SrsCreatorViewModel vm)
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
                    if (vm.IsoProcessing)
                    {
                        vm.CancelCreationCommand.Execute(null);
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
