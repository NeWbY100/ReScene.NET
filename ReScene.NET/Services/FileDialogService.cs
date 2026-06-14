using System.Windows;
using Microsoft.Win32;
using ReScene.NET.Views;

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
        {
            return Task.FromResult<IReadOnlyList<string>>(dialog.FileNames.ToList());
        }

        return Task.FromResult<IReadOnlyList<string>>([]);
    }

    public Task<string?> SaveFileAsync(string title, string defaultExtension, IReadOnlyList<string> filters, string? defaultFileName = null)
    {
        // Callers may suggest a full path; open the dialog in that folder but
        // show only the file name in the name box.
        string? directory = string.IsNullOrEmpty(defaultFileName) ? null : Path.GetDirectoryName(defaultFileName);

        var dialog = new SaveFileDialog
        {
            Title = title,
            DefaultExt = defaultExtension,
            Filter = BuildFilter(filters),
            FileName = string.IsNullOrEmpty(defaultFileName) ? string.Empty : Path.GetFileName(defaultFileName)
        };

        if (!string.IsNullOrEmpty(directory) && Directory.Exists(directory))
        {
            dialog.InitialDirectory = directory;
        }

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
        MessageBoxResult result = MessageBox.Show(message, title,
            MessageBoxButton.OKCancel, MessageBoxImage.Warning);
        return Task.FromResult(result == MessageBoxResult.OK);
    }

    public Task<string?> PromptForTextAsync(string title, string message, string initialValue)
    {
        var window = new PromptWindow(title, message, initialValue)
        {
            Owner = Application.Current?.MainWindow
        };

        bool? result = window.ShowDialog();
        return Task.FromResult(result == true ? window.ResultText : null);
    }

    private static string BuildFilter(IReadOnlyList<string> filters) =>
        // Avalonia filters: "Description|*.ext1;*.ext2"
        // WPF filters: "Description|*.ext1;*.ext2" (same format)
        string.Join("|", filters);
}
