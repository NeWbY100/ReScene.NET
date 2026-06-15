namespace ReScene.NET.Services;

public interface IFileDialogService
{
    public Task<string?> OpenFileAsync(string title, IReadOnlyList<string> filters);
    public Task<IReadOnlyList<string>> OpenFilesAsync(string title, IReadOnlyList<string> filters);
    public Task<string?> SaveFileAsync(string title, string defaultExtension, IReadOnlyList<string> filters, string? defaultFileName = null);
    public Task<string?> OpenFolderAsync(string title);
    public Task<bool> ShowConfirmAsync(string title, string message);
    public Task<string?> PromptForTextAsync(string title, string message, string initialValue);

    /// <summary>Shows a synchronous error dialog (OK button, error icon).</summary>
    public void ShowError(string title, string message);

    /// <summary>Shows a synchronous warning dialog (OK button, warning icon).</summary>
    public void ShowWarning(string title, string message);

    /// <summary>Shows a synchronous informational dialog (OK button, information icon).</summary>
    public void ShowInfo(string title, string message);

    /// <summary>Shows a synchronous OK/Cancel confirmation dialog; returns true when OK is chosen.</summary>
    public bool Confirm(string title, string message);
}
