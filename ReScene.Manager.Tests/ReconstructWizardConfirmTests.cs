using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using ReScene.App.Core.Services;
using ReScene.App.Core.ViewModels;
using ReScene.App.Core.ViewModels.Wizards;
using ReScene.Manager.Views.Wizards;

namespace ReScene.Manager.Tests;

/// <summary>
/// Locks the Reconstruct wizard's "Files &amp; folders" delete-confirmation: its text names the two
/// reserved subtrees (<c>output</c> + <c>.rescene-work</c>), and it is gated on the plan-before-mutate
/// preflight — a run that would be rejected never shows the "clear the output" confirm (cases j, i).
/// </summary>
public sealed class ReconstructWizardConfirmTests : IDisposable
{
    private readonly List<string> _temps = [];

    private string NewTempDir()
    {
        string dir = Path.Combine(Path.GetTempPath(), $"rescene-wiz-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        _temps.Add(dir);
        return dir;
    }

    public void Dispose()
    {
        foreach (string d in _temps)
        {
            try
            { Directory.Delete(d, recursive: true); }
            catch { /* best effort */ }
        }
    }

    /// <summary>Records every confirm shown and returns a fixed answer; all other members are inert.</summary>
    private sealed class RecordingConfirmDialog : IFileDialogService
    {
        public bool Result { get; init; } = true;
        public List<(string Title, string Message)> Confirms { get; } = [];

        public bool Confirm(string title, string message)
        {
            Confirms.Add((title, message));
            return Result;
        }

        public Task<bool> ShowConfirmAsync(string title, string message)
        {
            Confirms.Add((title, message));
            return Task.FromResult(Result);
        }

        public Task<string?> OpenFileAsync(string title, IReadOnlyList<string> filters, string? initialPath = null) => Task.FromResult<string?>(null);
        public Task<IReadOnlyList<string>> OpenFilesAsync(string title, IReadOnlyList<string> filters, string? initialPath = null) => Task.FromResult<IReadOnlyList<string>>([]);
        public Task<string?> SaveFileAsync(string title, string defaultExtension, IReadOnlyList<string> filters, string? defaultFileName = null) => Task.FromResult<string?>(null);
        public Task<string?> OpenFolderAsync(string title, string? initialPath = null) => Task.FromResult<string?>(null);
        public Task<string?> PromptForTextAsync(string title, string message, string initialValue) => Task.FromResult<string?>(null);
        public void ShowError(string title, string message) { }
        public void ShowWarning(string title, string message) { }
        public void ShowInfo(string title, string message) { }
    }

    [AvaloniaFact]
    public void FilesAndFolders_OutputNotEmpty_ConfirmNamesReservedSubtrees()
    {
        var dialog = new RecordingConfirmDialog { Result = true };
        BeginnerShellViewModel shell = BeginnerShellTestFactory.Create(dialog);

        string output = NewTempDir();
        Directory.CreateDirectory(Path.Combine(output, "output"));
        File.WriteAllText(Path.Combine(output, "output", "old.rar"), "stale");

        // Create() resets the reconstructor, so set the paths AFTER building the wizard.
        (WizardViewModel wizard, Control _) = BeginnerWizardFactory.Create(BeginnerCard.Reconstruct, shell);
        shell.Reconstructor.WinRARPath = NewTempDir();
        shell.Reconstructor.ReleasePath = NewTempDir(); // empty → no subdir timestamp warning
        shell.Reconstructor.OutputPath = output;

        bool advanced = wizard.Steps[1].ConfirmLeave!();

        Assert.True(advanced);
        (string _, string message) = Assert.Single(dialog.Confirms);
        Assert.Contains("output", message, StringComparison.Ordinal);
        Assert.Contains(".rescene-work", message, StringComparison.Ordinal);
        Assert.True(shell.Reconstructor.SuppressOutputNotEmptyConfirm);

        wizard.Dispose();
    }

    [AvaloniaFact]
    public void FilesAndFolders_PreflightRejects_SkipsDeleteConfirm()
    {
        var dialog = new RecordingConfirmDialog { Result = true };
        BeginnerShellViewModel shell = BeginnerShellTestFactory.Create(dialog);

        string output = NewTempDir();
        // Output already has reconstruction artifacts (would otherwise trigger the delete confirm)...
        Directory.CreateDirectory(Path.Combine(output, "output"));
        File.WriteAllText(Path.Combine(output, "output", "old.rar"), "stale");
        // ...but the WinRAR folder sits inside the reserved output subtree, so the preflight rejects.
        string winrar = Path.Combine(output, "output", "winrar");
        Directory.CreateDirectory(winrar);

        // Create() resets the reconstructor, so set the paths AFTER building the wizard.
        (WizardViewModel wizard, Control _) = BeginnerWizardFactory.Create(BeginnerCard.Reconstruct, shell);
        shell.Reconstructor.WinRARPath = winrar;
        shell.Reconstructor.ReleasePath = NewTempDir();
        shell.Reconstructor.OutputPath = output;

        bool advanced = wizard.Steps[1].ConfirmLeave!();

        Assert.True(advanced);                 // proceeds to Start, which surfaces the specific rejection
        Assert.Empty(dialog.Confirms);          // the "clear the output" confirm was NOT shown
        Assert.False(shell.Reconstructor.SuppressOutputNotEmptyConfirm);

        wizard.Dispose();
    }
}
