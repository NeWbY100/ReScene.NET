using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ReScene.NET.Models;
using ReScene.NET.Services;

namespace ReScene.NET.ViewModels;

public partial class SettingsViewModel : ViewModelBase
{
    private readonly IAppSettingsService _settingsService;
    private readonly IFileDialogService _fileDialog;

    public SettingsViewModel(IAppSettingsService settingsService, IFileDialogService fileDialog)
    {
        _settingsService = settingsService;
        _fileDialog = fileDialog;

        AppSettings settings = _settingsService.Load();
        DefaultAppName = settings.DefaultAppName;
        DefaultOutputDirectory = settings.DefaultOutputDirectory;
        RecentFilesLimit = settings.RecentFilesLimit;
    }

    [ObservableProperty]
    public partial string DefaultAppName { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string DefaultOutputDirectory { get; set; } = string.Empty;

    [ObservableProperty]
    public partial int RecentFilesLimit { get; set; } = 10;

    public bool DialogResult { get; private set; }

    [RelayCommand]
    private async Task BrowseOutputDirAsync()
    {
        string? path = await _fileDialog.OpenFolderAsync("Select default output directory");

        if (path is not null)
        {
            DefaultOutputDirectory = path;
        }
    }

    [RelayCommand]
    private void Save()
    {
        _settingsService.Save(new AppSettings
        {
            DefaultAppName = DefaultAppName,
            DefaultOutputDirectory = DefaultOutputDirectory,
            RecentFilesLimit = Math.Clamp(RecentFilesLimit, 1, 100)
        });
        DialogResult = true;
    }
}
