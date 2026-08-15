using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using ReScene.App.Core.ViewModels;
using ReScene.Manager.Helpers;

namespace ReScene.Manager.Views;

/// <summary>
/// Modal file-copy progress dialog opened by <see cref="BruteForceProgressWindow"/> while the RAR
/// reconstructor copies a matched release's files, ported from the WPF
/// <c>ReScene.NET.Views.FileCopyProgressWindow</c>. Its <see cref="Avalonia.StyledElement.DataContext"/> is the same
/// <c>ReconstructorViewModel</c> the owning window uses, so every binding here reads that VM's
/// <c>Copy*</c> progress properties directly. Opened/closed by a
/// <see cref="Helpers.ModalProgressWindowController{TWindow}"/> keyed off <c>IsCopying</c>; on
/// <see cref="Control.Loaded"/> the Cancel button is wired through
/// <see cref="ProgressWindowLifecycle"/> so Cancel (or a native close while copying) drives
/// <c>StopCommand</c> and shows a "Cancelling..." grace period instead of tearing the dialog down —
/// the controller still closes it programmatically once <c>IsCopying</c> clears.
/// </summary>
public partial class FileCopyProgressWindow : Window
{
    public FileCopyProgressWindow()
    {
        AvaloniaXamlLoader.Load(this);
        Loaded += OnLoaded;
    }

    private void OnLoaded(object? sender, RoutedEventArgs e)
    {
        // The controller sets DataContext before ShowDialog, so it is available here. Wire once.
        Loaded -= OnLoaded;
        if (DataContext is ReconstructorViewModel vm)
        {
            ProgressWindowLifecycle.Attach(this, vm, () => vm.IsCopying, this.FindControl<Button>("CancelButton")!);
        }
    }
}
