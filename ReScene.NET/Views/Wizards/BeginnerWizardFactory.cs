using System.Windows;
using ReScene.NET.Helpers;
using ReScene.NET.Models;
using ReScene.NET.ViewModels;
using ReScene.NET.ViewModels.Wizards;

namespace ReScene.NET.Views.Wizards;

/// <summary>Assembles the wizard (navigation VM + body view) for a Beginner hub card.</summary>
public static class BeginnerWizardFactory
{
    public static (WizardViewModel ViewModel, FrameworkElement Body) Create(BeginnerCard card, BeginnerShellViewModel shell)
    {
        // Reset the relevant task VM before building so the wizard opens with clean state.
        // Reset() is a no-op while that VM is mid-operation (IsCreating/IsRunning), so an active
        // run is never disrupted. Most cards reuse the Advanced tab's app-lifetime VM (a shared
        // singleton); the CreateSrr card uses a DEDICATED CreatorViewModel (see
        // MainWindowViewModel) so its state never collides with the Advanced SRR Creator tab.
        switch (card)
        {
            case BeginnerCard.CreateSrr:
                shell.CreateSrrWizard.Reset();
                return BuildCreateSrr(shell.CreateSrrWizard);
            case BeginnerCard.CreateSrs:
                shell.SRSCreator.Reset();
                return BuildCreateSrs(shell.SRSCreator);
            case BeginnerCard.Reconstruct:
                shell.Reconstructor.Reset();
                return BuildReconstruct(shell.Reconstructor);
            case BeginnerCard.Restore:
                shell.Restore.Reset();
                return BuildRestore(shell.Restore);
            case BeginnerCard.EditSrr:
                shell.SrrEditor.Reset();
                return BuildEditSrr(shell.SrrEditor);
            default:
                throw new ArgumentOutOfRangeException(nameof(card));
        }
    }

    private static (WizardViewModel, FrameworkElement) BuildCreateSrr(CreatorViewModel vm)
    {
        // The wizard lists sample SRS / subtitle SRRs as placeholders on the Manage step (built on
        // leaving the samples step) and generates the actual files at create time, so turn off the
        // Advanced tab's create-time scan generation — otherwise they'd be generated twice.
        vm.AutoCreateSRS = false;
        vm.CreateVobsubSRR = false;

        // Beginners benefit from OpenSubtitles matching, so include OSO hashes by default (the
        // Manage step exposes a checkbox to turn it off).
        vm.ComputeOSOHashes = true;

        var steps = new List<WizardStep>
        {
            new()
            {
                Title = "Choose the release",
                CanAdvance = () => vm.InputStatus.State == FieldState.Ok,
            },
            new()
            {
                // Collect sample/subtitle inputs the release scan can't find (e.g. an unextracted
                // release). On leaving, placeholder rows are added to the Manage step; the actual
                // .srs/.srr are generated at create time.
                Title = "Samples & subtitles",
                OnLeave = vm.BuildSampleAndSubtitlePlaceholders,
            },
            new()
            {
                Title = "Manage stored files",
                // Pre-fill a concrete .srr path before the Save step shows: the settings default
                // may be a bare directory, and an untouched field should still get a suggestion.
                OnLeave = () => vm.OutputPath =
                    FieldGuidance.SuggestSaveFileName(vm.OutputPath, vm.InputPath, ".srr") ?? vm.OutputPath,
            },
            new()
            {
                Title = "Save as",
                CanAdvance = () => !vm.IsCreating
                    && !string.IsNullOrWhiteSpace(vm.OutputPath)
                    && !Directory.Exists(vm.OutputPath),
                NextLabel = "Create",
                ConfirmLeave = () =>
                {
                    if (!File.Exists(vm.OutputPath))
                    {
                        return true;
                    }

                    if (MessageBox.Show(
                            $"A file already exists at:\n\n{vm.OutputPath}\n\nDo you want to overwrite it?",
                            "Overwrite existing file?",
                            MessageBoxButton.OKCancel,
                            MessageBoxImage.Warning) != MessageBoxResult.OK)
                    {
                        return false;
                    }

                    vm.SuppressOverwriteConfirm = true;
                    return true;
                },
                OnLeave = () =>
                {
                    if (vm.CreateSRRCommand.CanExecute(null))
                    {
                        vm.CreateSRRCommand.Execute(null);
                    }
                },
            },
            new()
            {
                Title = "Create",
                // No going back mid-run, and after success Back would only invite mistakes;
                // it stays for failed/cancelled runs so the user can adjust and retry.
                CanGoBack = () => !vm.IsCreating && !vm.BuildSucceeded,
            },
        };
        return (new WizardViewModel("Create an SRR", vm, steps), new CreateSrrWizardBody());
    }

    private static (WizardViewModel, FrameworkElement) BuildCreateSrs(SRSCreatorViewModel vm)
    {
        var steps = new List<WizardStep>
        {
            new() { Title = "Choose the sample", CanAdvance = () => vm.SampleStatus.State != FieldState.Error && !string.IsNullOrWhiteSpace(vm.InputPath) && (!vm.IsISOSource || vm.SelectedISOMediaFile is not null) },
            new()
            {
                Title = "Choose where to save",
                CanAdvance = () => !string.IsNullOrWhiteSpace(vm.OutputPath),
                NextLabel = "Create",
                ConfirmLeave = () =>
                {
                    if (!vm.HasValidMainFile && MessageBox.Show(
                            "No full movie was selected, so match offsets will be 0. The SRS will still rebuild " +
                            "the sample, but restoring is slower and could match the wrong data if a track's " +
                            "signature isn't unique.\n\nCreate a signature-only SRS anyway?",
                            "Create a signature-only SRS?",
                            MessageBoxButton.OKCancel,
                            MessageBoxImage.Warning) != MessageBoxResult.OK)
                    {
                        return false;
                    }

                    if (File.Exists(vm.OutputPath) && MessageBox.Show(
                            $"An SRS file already exists at:\n\n{vm.OutputPath}\n\nDo you want to overwrite it?",
                            "Overwrite existing SRS?",
                            MessageBoxButton.OKCancel,
                            MessageBoxImage.Warning) != MessageBoxResult.OK)
                    {
                        return false;
                    }

                    // Already warned here (or a movie is present); don't re-ask inside CreateSRSAsync.
                    vm.SuppressNoMovieConfirm = true;
                    return true;
                },
                OnLeave = () =>
                {
                    if (vm.CreateSRSCommand.CanExecute(null))
                    {
                        vm.CreateSRSCommand.Execute(null);
                    }
                },
            },
            new() { Title = "Create" },
        };
        return (new WizardViewModel("Create a sample SRS", vm, steps), new CreateSrsWizardBody());
    }

    private static (WizardViewModel, FrameworkElement) BuildReconstruct(ReconstructorViewModel vm)
    {
        // Beginners almost always want the complete archive set, not just the first volume —
        // pre-check it on every wizard open (the Advanced tab keeps its own unchecked default).
        vm.CompleteAllVolumes = true;

        var steps = new List<WizardStep>
        {
            new() { Title = "Import the SRR", CanAdvance = () => vm.HasImportedSrr && !vm.HasCustomPackerWarning },
            new()
            {
                Title = "Files & folders",
                CanAdvance = () => !vm.IsRunning
                    && !string.IsNullOrWhiteSpace(vm.WinRarPath)
                    && !string.IsNullOrWhiteSpace(vm.ReleasePath)
                    && !string.IsNullOrWhiteSpace(vm.OutputPath)
                    && !string.IsNullOrWhiteSpace(vm.VerificationPath),
                NextLabel = "Start",
                // Ask Start's confirmation questions here, while this step is still visible,
                // instead of letting them pop over the progress step. Confirmed answers set
                // one-shot suppress flags so Start doesn't repeat them.
                ConfirmLeave = () =>
                {
                    if (vm.NeedsSubdirTimestampWarning())
                    {
                        if (MessageBox.Show(
                                ReconstructorViewModel.SubdirTimestampWarningText,
                                "Warning: modified date",
                                MessageBoxButton.OKCancel,
                                MessageBoxImage.Warning) != MessageBoxResult.OK)
                        {
                            return false;
                        }

                        vm.SuppressSubdirTimestampConfirm = true;
                    }

                    bool outputNotEmpty;
                    try
                    {
                        outputNotEmpty = Directory.Exists(vm.OutputPath)
                            && Directory.EnumerateFileSystemEntries(vm.OutputPath).Any();
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        // Unreadable directory — let Start surface the real error.
                        return true;
                    }

                    if (!outputNotEmpty)
                    {
                        return true;
                    }

                    if (MessageBox.Show(
                            $"The output directory is not empty:\n\n{vm.OutputPath}\n\nIts contents will be deleted before starting. Continue?",
                            "Output directory not empty",
                            MessageBoxButton.OKCancel,
                            MessageBoxImage.Warning) != MessageBoxResult.OK)
                    {
                        return false;
                    }

                    vm.SuppressOutputNotEmptyConfirm = true;
                    return true;
                },
                OnLeave = () =>
                {
                    if (vm.StartCommand.CanExecute(null))
                    {
                        vm.StartCommand.Execute(null);
                    }
                },
            },
            new()
            {
                Title = "Reconstruct",
                // Once the reconstruction completed successfully there is nothing to go back for —
                // hide Back so it can't be clicked by accident. It stays for failed/cancelled runs
                // so the user can adjust paths and retry.
                CanGoBack = () => !vm.LastRunSucceeded,
            },
        };
        return (new WizardViewModel("Reconstruct RAR archives", vm, steps), new ReconstructWizardBody());
    }

    private static (WizardViewModel, FrameworkElement) BuildRestore(BeginnerRestoreViewModel vm)
    {
        var steps = new List<WizardStep>
        {
            new() { Title = "Choose your file", CanAdvance = () => vm.ShowFlow },
            new()
            {
                Title = "Media & output",
                // Gate on the underlying command so all prerequisites (paths, a selected sample) are met
                // before the rebuild/restore can be started.
                CanAdvance = () => vm.IsBulk
                    ? vm.BulkRestorer?.RestoreCommand.CanExecute(null) == true
                    : vm.SingleRebuilder?.RebuildCommand.CanExecute(null) == true,
                NextLabelFunc = () => vm.IsBulk ? "Restore All" : "Rebuild",
                OnLeave = () =>
                {
                    if (vm.IsBulk)
                    {
                        if (vm.BulkRestorer?.RestoreCommand.CanExecute(null) == true)
                        {
                            vm.BulkRestorer.RestoreCommand.Execute(null);
                        }
                    }
                    else if (vm.SingleRebuilder?.RebuildCommand.CanExecute(null) == true)
                    {
                        vm.SingleRebuilder.RebuildCommand.Execute(null);
                    }
                },
            },
            new() { Title = "Restore" },
        };
        return (new WizardViewModel("Restore a sample", vm, steps), new RestoreWizardBody());
    }

    private static (WizardViewModel, FrameworkElement) BuildEditSrr(SrrEditorViewModel vm)
    {
        var steps = new List<WizardStep>
        {
            new() { Title = "Choose the SRR", CanAdvance = () => vm.SourceStatus.State == FieldState.Ok, OnLeave = vm.EnsureWorkingCopy },
            new() { Title = "Manage stored files" },
            new()
            {
                Title = "Save as",
                CanAdvance = () => !string.IsNullOrWhiteSpace(vm.OutputPath),
                NextLabel = "Save",
                ConfirmLeave = () =>
                {
                    if (!File.Exists(vm.OutputPath))
                    {
                        return true;
                    }

                    return MessageBox.Show(
                        $"A file already exists at:\n\n{vm.OutputPath}\n\nDo you want to overwrite it?",
                        "Overwrite existing file?",
                        MessageBoxButton.OKCancel,
                        MessageBoxImage.Warning) == MessageBoxResult.OK;
                },
                OnLeave = vm.Save,
            },
            new() { Title = "Done" },
        };
        return (new WizardViewModel("Edit an SRR", vm, steps), new EditSrrWizardBody());
    }
}
