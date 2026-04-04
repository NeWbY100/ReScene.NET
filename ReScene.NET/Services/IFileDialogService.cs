namespace ReScene.NET.Services;

public interface IFileDialogService
{
    public Task<string?> OpenFileAsync(string title, IReadOnlyList<string> filters);
    public Task<IReadOnlyList<string>> OpenFilesAsync(string title, IReadOnlyList<string> filters);
    public Task<string?> SaveFileAsync(string title, string defaultExtension, IReadOnlyList<string> filters, string? defaultFileName = null);
    public Task<string?> OpenFolderAsync(string title);
    public Task<bool> ShowConfirmAsync(string title, string message);
}
