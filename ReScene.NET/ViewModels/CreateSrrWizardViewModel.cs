using ReScene.NET.Helpers;
using ReScene.NET.Services;

namespace ReScene.NET.ViewModels;

/// <summary>
/// Beginner "Create an SRR" facade for the build-a-draft-then-curate flow. Composes the existing
/// <see cref="CreatorViewModel"/> (input + build) and a dedicated <see cref="SrrEditorViewModel"/>
/// (manage + save): the release is built into a throwaway draft, the user curates that draft with
/// the same Manage step as the Edit wizard, then saves the result to a chosen location.
/// </summary>
/// <remarks>
/// The hosting <c>WizardViewModel</c> observes only this facade, so the sub-VMs' changes are
/// surfaced as our own for step gating. The draft temp directory is owned here; it is cleaned on
/// rebuild and on <see cref="Reset"/> (and again by the editor's working-copy cleanup once adopted).
/// </remarks>
public partial class CreateSrrWizardViewModel : ViewModelBase
{
    private readonly ITempDirectoryService _tempDir;
    private string? _draftDir;

    public CreatorViewModel Creator { get; }
    public SrrEditorViewModel Editor { get; }

    public CreateSrrWizardViewModel(CreatorViewModel creator, SrrEditorViewModel editor, ITempDirectoryService tempDir)
    {
        Creator = creator;
        Editor = editor;
        _tempDir = tempDir;

        // Surface sub-VM changes (InputStatus, BuildSucceeded, OutputPath, …) as our own so the
        // hosting wizard — which only observes this facade — re-evaluates its step gating.
        Creator.PropertyChanged += (_, _) => OnPropertyChanged(nameof(Creator));
        Editor.PropertyChanged += (_, _) => OnPropertyChanged(nameof(Editor));
    }

    /// <summary>
    /// Builds the SRR into a fresh throwaway draft so the user can curate it before choosing where
    /// to save. Called when leaving the release step.
    /// </summary>
    public void PrepareDraft()
    {
        // Drop any earlier draft (e.g. the user went back and changed the input) before rebuilding.
        if (_draftDir is not null)
        {
            _tempDir.Cleanup(_draftDir);
        }

        _draftDir = _tempDir.CreateTempDirectory();

        string releaseName = Path.GetFileNameWithoutExtension(Creator.InputPath).TrimEnd('.');
        if (string.IsNullOrWhiteSpace(releaseName))
        {
            releaseName = "release";
        }

        Creator.OutputPath = Path.Combine(_draftDir, releaseName + ".srr");
        Creator.SuppressOverwriteConfirm = true;

        // Set the build-gate state synchronously so the "Building draft" step can never be left with
        // a stale BuildSucceeded from a previous build, and shows progress the moment it appears.
        Creator.BuildSucceeded = false;
        Creator.ShowProgress = true;

        if (Creator.CreateSRRCommand.CanExecute(null))
        {
            Creator.CreateSRRCommand.Execute(null);
        }
    }

    /// <summary>
    /// Hands the freshly built draft to the editor for curation, suggesting an output next to the
    /// release. Called when leaving the build step (after it succeeded).
    /// </summary>
    public void AdoptDraftIntoEditor()
    {
        string suggested = FieldGuidance.SuggestSiblingPath(Creator.InputPath, ".srr");
        Editor.AdoptWorkingCopy(Creator.OutputPath, suggested);
    }

    /// <summary>Clears both sub-VMs and the draft so a wizard re-open starts clean.</summary>
    public void Reset()
    {
        if (_draftDir is not null)
        {
            // The facade is the sole owner of the draft temp directory (the editor skips cleanup of
            // an adopted working copy), so clean it here — covers both an adopted draft and one that
            // was built but never adopted (the user closed before the manage step).
            _tempDir.Cleanup(_draftDir);
            _draftDir = null;
        }

        Creator.Reset();
        Editor.Reset();
    }
}
