using Microsoft.Win32;

namespace ReScene.NET.Services;

public class FileDialogService : IFileDialogService
{
    public Task<string?> OpenFileAsync(string title, IReadOnlyList<string> filters)
    {
        var dialog = new OpenFileDialog
        {
            Title = title,
            Filter = BuildFilter(filters),
            Multiselect = false
        };

        return Task.FromResult(dialog.ShowDialog() == true ? dialog.FileName : null);
    }

    public Task<IReadOnlyList<string>> OpenFilesAsync(string title, IReadOnlyList<string> filters)
    {
        var dialog = new OpenFileDialog
        {
            Title = title,
            Filter = BuildFilter(filters),
            Multiselect = true
        };

        if (dialog.ShowDialog() == true)
            return Task.FromResult<IReadOnlyList<string>>(dialog.FileNames.ToList());

        return Task.FromResult<IReadOnlyList<string>>([]);
    }

    public Task<string?> SaveFileAsync(string title, string defaultExtension, IReadOnlyList<string> filters, string? defaultFileName = null)
    {
        var dialog = new SaveFileDialog
        {
            Title = title,
            DefaultExt = defaultExtension,
            Filter = BuildFilter(filters),
            FileName = defaultFileName ?? string.Empty
        };

        return Task.FromResult(dialog.ShowDialog() == true ? dialog.FileName : null);
    }

    public Task<string?> OpenFolderAsync(string title)
    {
        var dialog = new OpenFolderDialog
        {
            Title = title
        };

        return Task.FromResult(dialog.ShowDialog() == true ? dialog.FolderName : null);
    }

    public Task<bool> ShowConfirmAsync(string title, string message)
    {
        var result = System.Windows.MessageBox.Show(message, title,
            System.Windows.MessageBoxButton.OKCancel, System.Windows.MessageBoxImage.Warning);
        return Task.FromResult(result == System.Windows.MessageBoxResult.OK);
    }

    private static string BuildFilter(IReadOnlyList<string> filters)
    {
        // Avalonia filters: "Description|*.ext1;*.ext2"
        // WPF filters: "Description|*.ext1;*.ext2" (same format)
        return string.Join("|", filters);
    }
}
