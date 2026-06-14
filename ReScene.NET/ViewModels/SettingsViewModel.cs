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
        MkvMaxElements = settings.MkvMaxElements;
        ReconstructWinRarPath = settings.ReconstructWinRarPath;
        ReconstructOutputPath = settings.ReconstructOutputPath;
        Mode = settings.Mode ?? UserMode.Advanced;
    }

    [ObservableProperty]
    public partial string DefaultAppName { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string DefaultOutputDirectory { get; set; } = string.Empty;

    [ObservableProperty]
    public partial int RecentFilesLimit { get; set; } = 10;

    [ObservableProperty]
    public partial int MkvMaxElements { get; set; } = Core.Comparison.MKVFileData.DefaultMaxElements;

    [ObservableProperty]
    public partial string ReconstructWinRarPath { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string ReconstructOutputPath { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsBeginnerMode))]
    [NotifyPropertyChangedFor(nameof(IsAdvancedMode))]
    public partial UserMode Mode { get; set; }

    public bool IsBeginnerMode
    {
        get => Mode == UserMode.Beginner;
        set { if (value) { Mode = UserMode.Beginner; } }
    }

    public bool IsAdvancedMode
    {
        get => Mode == UserMode.Advanced;
        set { if (value) { Mode = UserMode.Advanced; } }
    }

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
    private async Task BrowseReconstructWinRarAsync()
    {
        string? path = await _fileDialog.OpenFolderAsync("Select WinRAR versions folder");

        if (path is not null)
        {
            ReconstructWinRarPath = path;
        }
    }

    [RelayCommand]
    private async Task BrowseReconstructOutputAsync()
    {
        string? path = await _fileDialog.OpenFolderAsync("Select reconstruction output folder");

        if (path is not null)
        {
            ReconstructOutputPath = path;
        }
    }

    [RelayCommand]
    private void Save()
    {
        _settingsService.Save(new AppSettings
        {
            DefaultAppName = DefaultAppName,
            DefaultOutputDirectory = DefaultOutputDirectory,
            RecentFilesLimit = Math.Clamp(RecentFilesLimit, 1, 100),
            MkvMaxElements = Math.Clamp(MkvMaxElements, 100, 1_000_000),
            ReconstructWinRarPath = ReconstructWinRarPath,
            ReconstructOutputPath = ReconstructOutputPath,
            Mode = Mode,
        });
        DialogResult = true;
    }
}
