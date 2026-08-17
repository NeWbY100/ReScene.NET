using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using ReScene.App.Core.ViewModels;
using ReScene.Manager.Helpers;

namespace ReScene.Manager.Views;

/// <summary>
/// The SRS Reconstructor tab, ported from the WPF <c>ReScene.NET.Views.SRSReconstructorView</c>.
/// Bound to a <see cref="SRSReconstructorViewModel"/> (supplied by the shell via
/// <c>DataContext="{Binding SRSReconstructor}"</c>). Path TextBox file-drop is declarative via
/// <c>behaviors:TextBoxDropBehavior.DropMode="File"</c> in the XAML. The only remaining code-behind
/// responsibility is opening/closing the shared <see cref="IsoProgressWindowController"/> modal in
/// step with the VM's <c>ISOProcessing</c> flag.
/// </summary>
public partial class SRSReconstructorView : UserControl
{
    private IsoProgressWindowController? _isoController;
    private SRSReconstructorViewModel? _subscribedVm;

    public SRSReconstructorView()
    {
        AvaloniaXamlLoader.Load(this);

        DataContextChanged += OnDataContextChanged;

        // Small-window layout degradation: the switch height is DERIVED from this view's own
        // measured expanded floor, not named here. As in SRSCreatorView, the config band (row 1)
        // declares no expanded minimum — at expanded size it is an uncapped Auto row that does not
        // give, so its measured content height is what the floor owes it.
        // x:CompileBindings="False" means x:Name elements are NOT wired to auto-generated fields
        // here (same as every other ported view in this project — see SRSCreatorView's own
        // note); resolved once via FindControl instead.
        var root = (Grid)Content!;
        ScrollViewer helpBody = this.FindControl<ScrollViewer>("HelpBody")!;
        TextBox srsFileTextBox = this.FindControl<TextBox>("SRSFileTextBox")!;
        Behaviors.CompactHeightBehavior.SetEnabled(root, true);
        Behaviors.CompactHeightBehavior.SetRowSizes(root,
            [new Behaviors.CompactRowSize(RowIndex: 1, NormalHeight: double.NaN,
                CompactMinHeight: 80, Mode: Behaviors.CompactRowMode.AutoToStar)]);
        Behaviors.CompactHeightBehavior.SetHelpBody(root, helpBody);
        Behaviors.CompactHeightBehavior.SetHelpBodyMaxHeight(root, 40);
        Behaviors.CompactHeightBehavior.SetRestoreFocusTarget(root, srsFileTextBox);
    }

    // Avalonia's DataContextChanged carries no old/new values (unlike WPF's
    // DependencyPropertyChangedEventArgs), so the previously-subscribed VM is tracked in a field.
    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        _subscribedVm?.PropertyChanged -= OnVmPropertyChanged;

        _isoController = null;
        _subscribedVm = DataContext as SRSReconstructorViewModel;

        if (_subscribedVm is not { } vm)
        {
            return;
        }

        // Forward cancellation to the existing generated CancelRebuildCommand.
        _isoController = new IsoProgressWindowController(
            this, () => vm.ISOProcessing, () => vm.CancelRebuildCommand.Execute(null));
        vm.PropertyChanged += OnVmPropertyChanged;
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
