using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ReScene.NET.ViewModels;

/// <summary>
/// Hosts the Beginner hub. Holds references to the shared task ViewModels and tracks which
/// task card (if any) is open. Navigation state is independent of the task VMs.
/// </summary>
public partial class BeginnerShellViewModel : ViewModelBase
{
    // Shared task ViewModels, assigned by MainWindowViewModel via object initializer.
    public CreatorViewModel Creator { get; set; } = null!;
    public SRSCreatorViewModel SRSCreator { get; set; } = null!;
    public ReconstructorViewModel Reconstructor { get; set; } = null!;
    public BeginnerRestoreViewModel Restore { get; set; } = null!;

    /// <summary>Invoked with the current card to switch to Advanced mode on the matching tab.</summary>
    public Action<BeginnerCard>? OpenInAdvancedAction { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsHubVisible))]
    [NotifyPropertyChangedFor(nameof(ShowCreateSrr))]
    [NotifyPropertyChangedFor(nameof(ShowCreateSrs))]
    [NotifyPropertyChangedFor(nameof(ShowReconstruct))]
    [NotifyPropertyChangedFor(nameof(ShowRestore))]
    [NotifyPropertyChangedFor(nameof(CurrentTitle))]
    public partial BeginnerCard? CurrentCard { get; set; }

    public bool IsHubVisible => CurrentCard is null;
    public bool ShowCreateSrr => CurrentCard == BeginnerCard.CreateSrr;
    public bool ShowCreateSrs => CurrentCard == BeginnerCard.CreateSrs;
    public bool ShowReconstruct => CurrentCard == BeginnerCard.Reconstruct;
    public bool ShowRestore => CurrentCard == BeginnerCard.Restore;

    public string CurrentTitle => CurrentCard switch
    {
        BeginnerCard.CreateSrr => "Create an SRR",
        BeginnerCard.CreateSrs => "Create a sample SRS",
        BeginnerCard.Reconstruct => "Reconstruct RAR archives",
        BeginnerCard.Restore => "Restore a sample",
        _ => string.Empty,
    };

    [RelayCommand]
    private void OpenCard(BeginnerCard card) => CurrentCard = card;

    [RelayCommand]
    private void BackToHub() => CurrentCard = null;

    [RelayCommand]
    private void OpenInAdvanced(BeginnerCard card) => OpenInAdvancedAction?.Invoke(card);
}
