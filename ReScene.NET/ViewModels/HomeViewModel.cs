using System.Collections.ObjectModel;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ReScene.NET.Models;
using ReScene.NET.Services;

namespace ReScene.NET.ViewModels;

public partial class HomeViewModel : ViewModelBase
{
    private readonly IRecentFilesService _recentFiles;
    private readonly Action<string> _openFile;
    private readonly Action _switchToCreator;
    private readonly Func<Task> _openDialog;
    private readonly IFileDialogService _fileDialog;

    public ObservableCollection<RecentFileEntry> RecentFiles { get; } = [];

    [ObservableProperty]
    public partial bool HasRecentFiles { get; set; }

    public HomeViewModel(
        IRecentFilesService recentFiles,
        Action<string> openFile,
        Action switchToCreator,
        Func<Task> openDialog,
        IFileDialogService fileDialog)
    {
        _recentFiles = recentFiles;
        _openFile = openFile;
        _switchToCreator = switchToCreator;
        _openDialog = openDialog;
        _fileDialog = fileDialog;

        LoadRecentFiles();
    }

    public void LoadRecentFiles()
    {
        RecentFiles.Clear();
        foreach (RecentFileEntry entry in _recentFiles.LoadEntries())
        {
            RecentFiles.Add(entry);
        }

        HasRecentFiles = RecentFiles.Count > 0;
    }

    [RelayCommand]
    private async Task OpenInspectAsync() => await _openDialog();

    [RelayCommand]
    private void SwitchToCreator() => _switchToCreator();

    [RelayCommand]
    private void OpenRecentFile(RecentFileEntry entry)
    {
        if (File.Exists(entry.FilePath))
        {
            _openFile(entry.FilePath);
        }
        else
        {
            _fileDialog.ShowWarning("File Not Found", $"File not found:\n{entry.FilePath}");
        }
    }

    [RelayCommand]
    private void RemoveRecentFile(RecentFileEntry entry)
    {
        _recentFiles.RemoveEntry(entry.FilePath);
        RecentFiles.Remove(entry);
        HasRecentFiles = RecentFiles.Count > 0;
    }

    [RelayCommand]
    private void ClearRecentFiles()
    {
        _recentFiles.Clear();
        RecentFiles.Clear();
        HasRecentFiles = false;
    }

    [RelayCommand]
    private static void OpenUrl(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true });
        }
        catch { }
    }
}
