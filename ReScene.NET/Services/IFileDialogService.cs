namespace ReScene.NET.Services;

public interface IFileDialogService
{
    Task<string?> OpenFileAsync(string title, IReadOnlyList<string> filters);
    Task<IReadOnlyList<string>> OpenFilesAsync(string title, IReadOnlyList<string> filters);
    Task<string?> SaveFileAsync(string title, string defaultExtension, IReadOnlyList<string> filters, string? defaultFileName = null);
    Task<string?> OpenFolderAsync(string title);
    Task<bool> ShowConfirmAsync(string title, string message);
}
