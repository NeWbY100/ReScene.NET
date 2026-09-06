using System.Collections.Specialized;
using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using ReScene.App.Core.ViewModels;
using ReScene.Manager.Helpers;

namespace ReScene.Manager.Views;

/// <summary>
/// Modal brute-force progress dialog opened by the RAR Reconstructor (a later port task) while it
/// tests WinRAR versions against a release, ported from the WPF
/// <c>ReScene.NET.Views.BruteForceProgressWindow</c>. Its <see cref="Avalonia.StyledElement.DataContext"/> is the same
/// <see cref="ReconstructorViewModel"/> the owning tab uses, so every binding here reads that VM's
/// progress/version-grid state directly. Also opens/closes the nested
/// <see cref="FileCopyProgressWindow"/> and <see cref="CRCValidationProgressWindow"/> modals in step
/// with the VM's <c>IsCopying</c>/<c>IsVerifying</c> flags, one
/// <see cref="ModalProgressWindowController{TWindow}"/> per flag, mirroring
/// <see cref="IsoProgressWindowController"/>.
/// </summary>
public partial class BruteForceProgressWindow : Window
{
    // x:CompileBindings="False" (needed since DataContext is set dynamically at runtime, not
    // statically typed) means x:Name elements aren't wired to auto-generated fields, matching every
    // other ported view/window in this project — resolved once via FindControl, like CreatorView's
    // "StoredFilesGrid" and MainWindow's "VersionLink".
    private readonly DataGrid _versionGrid;
    private readonly Button _stopCloseButton;

    private bool _isCompleted;
    private ReconstructorViewModel? _subscribedVm;
    private ModalProgressWindowController<FileCopyProgressWindow>? _copyController;
    private ModalProgressWindowController<CRCValidationProgressWindow>? _verifyController;

    public BruteForceProgressWindow()
    {
        AvaloniaXamlLoader.Load(this);

        _versionGrid = this.FindControl<DataGrid>("VersionGrid")!;
        _stopCloseButton = this.FindControl<Button>("btnStopClose")!;

        _versionGrid.TemplateApplied += OnVersionGridTemplateApplied;

        // Avalonia's DataContextChanged carries no old/new values (unlike WPF's
        // DependencyPropertyChangedEventArgs), so the previously-subscribed VM is tracked in a field.
        DataContextChanged += OnDataContextChanged;
    }

    // Switch this grid's scrollbars from Fluent's OVERLAY layout to the reserved one, so the
    // horizontal bar sits below the last row instead of being drawn across it. DataGrid decides
    // overlay vs. reserved by reading two spans back off its own template parts at layout time
    // (IsHorizontalScrollBarOverCells is "headers ColumnSpan == 2", IsVerticalScrollBarOverCells is
    // "rows RowSpan == 2"); either being true makes ComputeScrollBarsLayout skip reserving that bar's
    // thickness. Both must flip together: reserving the horizontal bar's height is only computed
    // correctly when the rows presenter no longer spans its row. These are the spans Avalonia's own
    // Simple theme uses for the reserved layout. Fluent sets the header span inline on the template
    // child, which outranks a Style setter, so it has to be overwritten as a local value here.
    private static void OnVersionGridTemplateApplied(object? sender, TemplateAppliedEventArgs e)
    {
        if (e.NameScope.Find<Control>("PART_ColumnHeadersPresenter") is { } headers)
        {
            Grid.SetColumnSpan(headers, 1);
        }

        if (e.NameScope.Find<Control>("PART_RowsPresenter") is { } rows)
        {
            Grid.SetRowSpan(rows, 1);
            Grid.SetColumnSpan(rows, 2);
        }
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (_subscribedVm is not null)
        {
            _subscribedVm.PropertyChanged -= OnVmPropertyChanged;
            _subscribedVm.VersionEntries.CollectionChanged -= OnVersionEntriesChanged;
        }

        _subscribedVm = DataContext as ReconstructorViewModel;

        if (_subscribedVm is not { } vm)
        {
            _copyController = null;
            _verifyController = null;
            return;
        }

        vm.PropertyChanged += OnVmPropertyChanged;
        vm.VersionEntries.CollectionChanged += OnVersionEntriesChanged;

        _copyController = new ModalProgressWindowController<FileCopyProgressWindow>(
            this, () => vm.IsCopying, () => vm.StopCommand.Execute(null));
        _verifyController = new ModalProgressWindowController<CRCValidationProgressWindow>(
            this, () => vm.IsVerifying, () => vm.StopCommand.Execute(null));

        // Catch up with state that changed before the DataContext was wired.
        _copyController.OnBusyChanged(vm.IsCopying);
        _verifyController.OnBusyChanged(vm.IsVerifying);
    }

    private void OnVersionEntriesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (_subscribedVm is not { AutoScrollProgress: true } vm || vm.VersionEntries.Count == 0)
        {
            return;
        }

        // Defer the scroll: when the change came from a Dispatcher-marshalled update earlier in the
        // pipeline, the row container may not exist yet at the moment the event fires.
        Dispatcher.UIThread.Post(
            () =>
            {
                if (vm.VersionEntries.Count == 0)
                {
                    return;
                }

                _versionGrid.ScrollIntoView(vm.VersionEntries[^1], null);
            },
            DispatcherPriority.Background);
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ReconstructorViewModel.IsCopying))
        {
            if (sender is ReconstructorViewModel vmCopying)
            {
                _copyController?.OnBusyChanged(vmCopying.IsCopying);
            }

            return;
        }

        if (e.PropertyName == nameof(ReconstructorViewModel.IsVerifying))
        {
            if (sender is ReconstructorViewModel vmVerifying)
            {
                _verifyController?.OnBusyChanged(vmVerifying.IsVerifying);
            }

            return;
        }

        if (e.PropertyName != nameof(ReconstructorViewModel.IsRunning))
        {
            return;
        }

        if (sender is ReconstructorViewModel { IsRunning: false })
        {
            _isCompleted = true;
            _stopCloseButton.Content = "Close";
            _stopCloseButton.IsEnabled = true;
            _stopCloseButton.Classes.Remove("cancel");
            _stopCloseButton.Classes.Add("primary");
        }
    }

    private void OnStopCloseClick(object? sender, RoutedEventArgs e)
    {
        if (_isCompleted)
        {
            Close();
            return;
        }

        if (DataContext is ReconstructorViewModel vm)
        {
            vm.StopCommand.Execute(null);
            _stopCloseButton.IsEnabled = false;
            _stopCloseButton.Content = "Stopping...";
        }
    }

    private void OnCopyArgumentsClick(object? sender, RoutedEventArgs e)
    {
        if (GetSelectedVersionEntry() is { Arguments.Length: > 0 } entry)
        {
            CopyToClipboard(entry.Arguments);
        }
    }

    private void OnCopyFullCommandLineClick(object? sender, RoutedEventArgs e)
    {
        if (GetSelectedVersionEntry() is { FullCommandLine.Length: > 0 } entry)
        {
            CopyToClipboard(entry.FullCommandLine);
        }
    }

    // Avalonia's Clipboard is async and owned by the TopLevel (unlike WPF's static
    // Clipboard.SetText); fire-and-forget it here, guarded against a headless/detached TopLevel.
    private void CopyToClipboard(string text) =>
        _ = TopLevel.GetTopLevel(this)?.Clipboard?.SetTextAsync(text);

    // The grid is resolved once in the constructor, so the selected row is read straight off it
    // rather than walking the WPF MenuItem -> ContextMenu -> PlacementTarget chain.
    private ReconstructorViewModel.VersionEntry? GetSelectedVersionEntry() =>
        _versionGrid.SelectedItem as ReconstructorViewModel.VersionEntry;

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        if (!_isCompleted)
        {
            e.Cancel = true;
            if (DataContext is ReconstructorViewModel vm)
            {
                vm.StopCommand.Execute(null);
                _stopCloseButton.IsEnabled = false;
                _stopCloseButton.Content = "Stopping...";
            }

            return;
        }

        if (_subscribedVm is not null)
        {
            _subscribedVm.PropertyChanged -= OnVmPropertyChanged;
            _subscribedVm.VersionEntries.CollectionChanged -= OnVersionEntriesChanged;
        }

        base.OnClosing(e);
    }
}
