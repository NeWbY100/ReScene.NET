using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using ReScene.App.Core.ViewModels;
using ReScene.Manager.Helpers;

namespace ReScene.Manager.Views;

/// <summary>
/// The SRS Creator tab, ported from the WPF <c>ReScene.NET.Views.SRSCreatorView</c>. Bound to a
/// <see cref="SRSCreatorViewModel"/> (supplied by the shell via <c>DataContext="{Binding SRSCreator}"</c>).
/// Path TextBox file-drop is declarative via <c>behaviors:TextBoxDropBehavior.DropMode="File"</c> in
/// the XAML (the WPF original wired it imperatively in <c>Loaded</c> since it had no such attached
/// property). The only remaining code-behind responsibility is opening/closing the shared
/// <see cref="IsoProgressWindowController"/> modal in step with the VM's <c>ISOProcessing</c> flag.
/// </summary>
public partial class SRSCreatorView : UserControl
{
    private IsoProgressWindowController? _isoController;
    private SRSCreatorViewModel? _subscribedVm;

    public SRSCreatorView()
    {
        AvaloniaXamlLoader.Load(this);

        DataContextChanged += OnDataContextChanged;

        // Small-window layout degradation: the switch height is DERIVED from this view's own
        // measured expanded floor, not named here. The config band (row 1) declares NO expanded
        // minimum, unlike CreatorView's and SampleRestorerView's: at expanded size it is a plain
        // Auto row with nothing capping it, so it does not give and its content height genuinely
        // is what the floor owes it. Its worst-case content is small and fixed regardless of
        // window size, which is why this view needs no cap in the first place.
        // x:CompileBindings="False" means x:Name elements are NOT wired to auto-generated fields
        // here (same as every other ported view in this project — see ReconstructorView's own
        // note); resolved once via FindControl instead.
        var root = (Grid)Content!;
        ScrollViewer helpBody = this.FindControl<ScrollViewer>("HelpBody")!;
        TextBox inputTextBox = this.FindControl<TextBox>("InputTextBox")!;
        Behaviors.CompactHeightBehavior.SetEnabled(root, true);
        Behaviors.CompactHeightBehavior.SetRowSizes(root,
            [new Behaviors.CompactRowSize(RowIndex: 1, NormalHeight: double.NaN,
                CompactMinHeight: 80, Mode: Behaviors.CompactRowMode.AutoToStar)]);
        Behaviors.CompactHeightBehavior.SetHelpBody(root, helpBody);
        Behaviors.CompactHeightBehavior.SetHelpBodyMaxHeight(root, 40);
        Behaviors.CompactHeightBehavior.SetRestoreFocusTarget(root, inputTextBox);
    }

    // Avalonia's DataContextChanged carries no old/new values (unlike WPF's
    // DependencyPropertyChangedEventArgs), so the previously-subscribed VM is tracked in a field.
    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        _subscribedVm?.PropertyChanged -= OnVmPropertyChanged;

        _isoController = null;
        _subscribedVm = DataContext as SRSCreatorViewModel;

        if (_subscribedVm is not { } vm)
        {
            return;
        }

        // Forward cancellation to the existing generated CancelCreationCommand.
        _isoController = new IsoProgressWindowController(
            this, () => vm.ISOProcessing, () => vm.CancelCreationCommand.Execute(null));
        vm.PropertyChanged += OnVmPropertyChanged;
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
