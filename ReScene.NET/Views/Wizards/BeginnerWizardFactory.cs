using System.Windows;
using ReScene.NET.Models;
using ReScene.NET.ViewModels;
using ReScene.NET.ViewModels.Wizards;

namespace ReScene.NET.Views.Wizards;

/// <summary>Assembles the wizard (navigation VM + body view) for a Beginner hub card.</summary>
public static class BeginnerWizardFactory
{
    public static (WizardViewModel ViewModel, FrameworkElement Body) Create(BeginnerCard card, BeginnerShellViewModel shell)
    {
        // Reset the relevant shared (app-lifetime singleton) VM before building so the wizard
        // opens with clean state. Reset() is a no-op if that VM is busy with an operation
        // started from the Advanced tab, so an active run is never disrupted.
        switch (card)
        {
            case BeginnerCard.CreateSrr:
                shell.Creator.Reset();
                return BuildCreateSrr(shell.Creator);
            case BeginnerCard.CreateSrs:
                shell.SRSCreator.Reset();
                return BuildCreateSrs(shell.SRSCreator);
            case BeginnerCard.Reconstruct:
                shell.Reconstructor.Reset();
                return BuildReconstruct(shell.Reconstructor);
            case BeginnerCard.Restore:
                shell.Restore.Reset();
                return BuildRestore(shell.Restore);
            default:
                throw new ArgumentOutOfRangeException(nameof(card));
        }
    }

    private static (WizardViewModel, FrameworkElement) BuildCreateSrr(CreatorViewModel vm)
    {
        var steps = new List<WizardStep>
        {
            new() { Title = "Choose the release", CanAdvance = () => vm.InputStatus.State != FieldState.Error && !string.IsNullOrWhiteSpace(vm.InputPath) },
            new() { Title = "Choose where to save", CanAdvance = () => !string.IsNullOrWhiteSpace(vm.OutputPath) },
            new() { Title = "Create" },
        };
        return (new WizardViewModel("Create an SRR", vm, steps), new CreateSrrWizardBody());
    }

    private static (WizardViewModel, FrameworkElement) BuildCreateSrs(SRSCreatorViewModel vm)
    {
        var steps = new List<WizardStep>
        {
            new() { Title = "Choose the sample", CanAdvance = () => vm.SampleStatus.State != FieldState.Error && !string.IsNullOrWhiteSpace(vm.InputPath) && (!vm.IsISOSource || vm.SelectedISOMediaFile is not null) },
            new() { Title = "Choose where to save", CanAdvance = () => !string.IsNullOrWhiteSpace(vm.OutputPath) },
            new() { Title = "Create" },
        };
        return (new WizardViewModel("Create a sample SRS", vm, steps), new CreateSrsWizardBody());
    }

    private static (WizardViewModel, FrameworkElement) BuildReconstruct(ReconstructorViewModel vm)
    {
        var steps = new List<WizardStep>
        {
            new() { Title = "Import the SRR", CanAdvance = () => vm.HasImportedSrr && !vm.HasCustomPackerWarning },
            new() { Title = "WinRAR versions", CanAdvance = () => !string.IsNullOrWhiteSpace(vm.WinRarPath) },
            new() { Title = "Extracted files", CanAdvance = () => !string.IsNullOrWhiteSpace(vm.ReleasePath) },
            new() { Title = "Output folder", CanAdvance = () => !string.IsNullOrWhiteSpace(vm.OutputPath) },
            new() { Title = "Reconstruct" },
        };
        return (new WizardViewModel("Reconstruct RAR archives", vm, steps), new ReconstructWizardBody());
    }

    private static (WizardViewModel, FrameworkElement) BuildRestore(BeginnerRestoreViewModel vm)
    {
        var steps = new List<WizardStep>
        {
            new() { Title = "Choose your file", CanAdvance = () => vm.ShowFlow },
            new() { Title = "Media & output", CanAdvance = () => vm.IsBulk
                ? !string.IsNullOrWhiteSpace(vm.BulkRestorer?.MediaDirectoryPath)
                : !string.IsNullOrWhiteSpace(vm.SingleRebuilder?.MediaFilePath) },
            new() { Title = "Restore" },
        };
        return (new WizardViewModel("Restore a sample", vm, steps), new RestoreWizardBody());
    }
}
