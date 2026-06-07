namespace ReScene.NET.ViewModels;

/// <summary>
/// Holds references to the shared task ViewModels used by the Beginner hub. The hub opens a
/// pop-up wizard per card (see <c>BeginnerWizardFactory</c>); navigation now lives in the wizard.
/// </summary>
public partial class BeginnerShellViewModel : ViewModelBase
{
    // Shared task ViewModels, assigned by MainWindowViewModel via object initializer.
    public CreatorViewModel Creator { get; set; } = null!;
    public SRSCreatorViewModel SRSCreator { get; set; } = null!;
    public ReconstructorViewModel Reconstructor { get; set; } = null!;
    public BeginnerRestoreViewModel Restore { get; set; } = null!;
    public SrrEditorViewModel SrrEditor { get; set; } = null!;
}
