using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using ReScene.App.Core.ViewModels;
using ReScene.Manager.Helpers;

namespace ReScene.Manager.Views;

/// <summary>
/// Modal CRC-validation progress dialog opened by <see cref="BruteForceProgressWindow"/> while the RAR
/// reconstructor verifies the copied files against the release, ported from the WPF
/// <c>ReScene.NET.Views.CRCValidationProgressWindow</c>. Its <see cref="Avalonia.StyledElement.DataContext"/> is the
/// same <c>ReconstructorViewModel</c> the owning window uses, so every binding here reads that VM's
/// <c>Verify*</c> progress properties directly. Opened/closed by a
/// <see cref="Helpers.ModalProgressWindowController{TWindow}"/> keyed off <c>IsVerifying</c>; on
/// <see cref="Control.Loaded"/> the Cancel button is wired through
/// <see cref="ProgressWindowLifecycle"/> so Cancel (or a native close while verifying) drives
/// <c>StopCommand</c> and shows a "Cancelling..." grace period instead of tearing the dialog down —
/// the controller still closes it programmatically once <c>IsVerifying</c> clears.
/// </summary>
public partial class CRCValidationProgressWindow : Window
{
    public CRCValidationProgressWindow()
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
            ProgressWindowLifecycle.Attach(this, vm, () => vm.IsVerifying, this.FindControl<Button>("CancelButton")!);
        }
    }
}
