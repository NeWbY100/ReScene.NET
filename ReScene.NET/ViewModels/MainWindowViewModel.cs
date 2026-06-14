using System.Reflection;
using System.Windows.Shell;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ReScene.NET.Helpers;
using ReScene.NET.Models;
using ReScene.NET.Services;

namespace ReScene.NET.ViewModels;

/// <summary>
/// Root ViewModel that owns all child ViewModels and coordinates tab navigation and status aggregation.
/// </summary>
public partial class MainWindowViewModel : ViewModelBase
{
    private readonly IFileDialogService _fileDialog;
    private readonly IRecentFilesService _recentFiles;
    private readonly IAppSettingsService _appSettingsService;

    public HomeViewModel Home
    {
        get;
    }
    public InspectorViewModel Inspector
    {
        get;
    }
    public CreatorViewModel Creator
    {
        get;
    }
    public SRSCreatorViewModel SRSCreator
    {
        get;
    }
    public ReconstructorViewModel Reconstructor
    {
        get;
    }
    public SRSReconstructorViewModel SRSReconstructor
    {
        get;
    }
    public SampleRestorerViewModel SampleRestorer
    {
        get;
    }
    public FileCompareViewModel FileCompare
    {
        get;
    }

    public BeginnerShellViewModel Beginner
    {
        get;
    }

    [ObservableProperty]
    public partial int SelectedTabIndex { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsAdvancedMode))]
    [NotifyPropertyChangedFor(nameof(IsBeginnerMode))]
    public partial UserMode Mode { get; set; }

    public bool IsAdvancedMode => Mode == UserMode.Advanced;

    public bool IsBeginnerMode => Mode == UserMode.Beginner;

    private bool _applyingExternalModeChange;

    [RelayCommand]
    private void SetBeginnerMode() => Mode = UserMode.Beginner;

    [RelayCommand]
    private void SetAdvancedMode() => Mode = UserMode.Advanced;

    partial void OnModeChanged(UserMode value)
    {
        if (_applyingExternalModeChange)
        {
            return;
        }

        AppSettings settings = _appSettingsService.Load();
        settings.Mode = value;
        _appSettingsService.Save(settings);
    }

    private void OnSettingsChanged(object? sender, EventArgs e)
    {
        UserMode resolved = _appSettingsService.Load().Mode ?? Mode;
        if (resolved == Mode)
        {
            return;
        }

        _applyingExternalModeChange = true;
        Mode = resolved;
        _applyingExternalModeChange = false;
    }

    [ObservableProperty]
    public partial string WindowTitle { get; set; } = "ReScene.NET";

    [ObservableProperty]
    public partial string StatusMessage { get; set; } = "Ready";

    [ObservableProperty]
    public partial bool IsBusy { get; set; }

    [ObservableProperty]
    public partial TaskbarItemProgressState TaskbarProgressState { get; set; } = TaskbarItemProgressState.None;

    [ObservableProperty]
    public partial double TaskbarProgressValue { get; set; }

    public string AppVersion { get; } = GetAppVersion();

    public IAppSettingsService AppSettingsService => _appSettingsService;

    public IFileDialogService FileDialog => _fileDialog;

    private static string GetAppVersion()
    {
        string? version = Assembly.GetEntryAssembly()?
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;

        if (version is null)
        {
            return "0.0.0";
        }

        // InformationalVersion is "1.0.0+abcdef1" — extract hash after '+'
        int plus = version.IndexOf('+', StringComparison.Ordinal);
        return plus >= 0 ? version[..plus] + " (" + version[(plus + 1)..] + ")" : version;
    }

    public MainWindowViewModel()
        : this(new SRRCreationService(), new SRSCreationService(), new SRSReconstructionService(), new SampleRestorerService(new TempDirectoryService()), new BruteForceService(), new FileCompareService(new AppSettingsService()), new FileDialogService(), new RecentFilesService(new AppSettingsService()), new TempDirectoryService(), new SRREditingService(), new SRRVerifyService(), new PropertyExportService(), new AppSettingsService(), new HexDiffComputer())
    {
    }

    public MainWindowViewModel(ISrrCreationService srrService, ISrsCreationService srsService, ISrsReconstructionService srsReconService, ISampleRestorerService sampleRestorerService, IBruteForceService bruteForceService, IFileCompareService fileCompareService, IFileDialogService fileDialog, IRecentFilesService recentFiles, ITempDirectoryService tempDir, ISrrEditingService srrEditingService, ISrrVerifyService srrVerifyService, IPropertyExportService propertyExportService, IAppSettingsService appSettingsService, IHexDiffComputer hexDiffComputer)
    {
        _fileDialog = fileDialog;
        _recentFiles = recentFiles;
        _appSettingsService = appSettingsService;

        Inspector = new InspectorViewModel(fileDialog, srrEditingService, srrVerifyService, propertyExportService, appSettingsService);
        Creator = new CreatorViewModel(srrService, srsService, fileDialog, tempDir, appSettingsService);
        SRSCreator = new SRSCreatorViewModel(srsService, fileDialog, tempDir, appSettingsService);
        Reconstructor = new ReconstructorViewModel(bruteForceService, fileDialog, appSettingsService);
        SRSReconstructor = new SRSReconstructorViewModel(srsReconService, fileDialog, tempDir);
        SampleRestorer = new SampleRestorerViewModel(sampleRestorerService, fileDialog);
        FileCompare = new FileCompareViewModel(fileCompareService, fileDialog, hexDiffComputer);

        var beginnerRestore = new BeginnerRestoreViewModel(fileDialog)
        {
            BulkRestorer = SampleRestorer,
            SingleRebuilder = SRSReconstructor,
        };
        Beginner = new BeginnerShellViewModel
        {
            // A dedicated CreatorViewModel (not the Advanced tab's shared one) so the wizard's
            // state and build never collide with the Advanced SRR Creator tab.
            CreateSrrWizard = new CreatorViewModel(srrService, srsService, fileDialog, tempDir, appSettingsService),
            SRSCreator = SRSCreator,
            Reconstructor = Reconstructor,
            Restore = beginnerRestore,
            SrrEditor = new SrrEditorViewModel(srrEditingService, fileDialog, tempDir),
        };

        Home = new HomeViewModel(
            recentFiles,
            openFile: OpenSceneFile,
            switchToCreator: () => SelectedTabIndex = 2,
            openDialog: OpenFileAsync);

        Inspector.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(InspectorViewModel.StatusMessage))
            {
                StatusMessage = Inspector.StatusMessage;
            }
            else if (e.PropertyName == nameof(InspectorViewModel.IsExporting))
            {
                UpdateIsBusy();
            }
        };

        Creator.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(CreatorViewModel.IsCreating) or nameof(CreatorViewModel.ProgressPercent))
            {
                UpdateIsBusy();
                UpdateTaskbarProgress();
            }
        };

        SRSCreator.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(SRSCreatorViewModel.IsCreating) or nameof(SRSCreatorViewModel.ProgressPercent))
            {
                UpdateIsBusy();
                UpdateTaskbarProgress();
            }
        };

        Reconstructor.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(ReconstructorViewModel.IsRunning) or nameof(ReconstructorViewModel.ProgressPercent))
            {
                UpdateIsBusy();
                UpdateTaskbarProgress();
            }
        };

        SRSReconstructor.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(SRSReconstructorViewModel.IsRebuilding) or nameof(SRSReconstructorViewModel.ProgressPercent))
            {
                UpdateIsBusy();
                UpdateTaskbarProgress();
            }
        };

        SampleRestorer.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(SampleRestorerViewModel.IsRestoring) or nameof(SampleRestorerViewModel.ProgressPercent))
            {
                UpdateIsBusy();
                UpdateTaskbarProgress();
            }
        };

        // Apply the persisted/resolved mode without writing it back: the value was just
        // loaded, so suppress OnModeChanged's save. Subscribe to Changed afterwards.
        _applyingExternalModeChange = true;
        Mode = _appSettingsService.Load().Mode ?? UserMode.Advanced;
        _applyingExternalModeChange = false;
        _appSettingsService.Changed += OnSettingsChanged;
    }

    [RelayCommand]
    private async Task ExportStoredFileAsync() => await Inspector.ExportBlockCommand.ExecuteAsync(null);

    [RelayCommand]
    private async Task OpenFileAsync()
    {
        string? path = await _fileDialog.OpenFileAsync(
            "Open Scene File", FileDialogFilters.SceneFiles);

        if (path is not null)
        {
            OpenSceneFile(path);
        }
    }

    /// <summary>
    /// Opens a scene file (SRR/SRS) in the Inspector tab and updates the window title.
    /// </summary>
    /// <param name="filePath">
    /// Absolute path to the scene file.
    /// </param>
    public void OpenSceneFile(string filePath)
    {
        Mode = UserMode.Advanced;
        Inspector.LoadFile(filePath);
        SelectedTabIndex = 1; // Switch to Inspector tab
        WindowTitle = $"ReScene.NET - {Path.GetFileName(filePath)}";
        StatusMessage = Inspector.StatusMessage;

        _recentFiles.AddEntry(filePath);
        Home.LoadRecentFiles();
    }

    private void UpdateIsBusy()
    {
        IsBusy = Inspector.IsExporting
            || Creator.IsCreating
            || SRSCreator.IsCreating
            || Reconstructor.IsRunning
            || SRSReconstructor.IsRebuilding
            || SampleRestorer.IsRestoring;
    }

    private void UpdateTaskbarProgress()
    {
        if (Creator.IsCreating)
        {
            TaskbarProgressState = TaskbarItemProgressState.Normal;
            TaskbarProgressValue = Creator.ProgressPercent / 100.0;
        }
        else if (SRSCreator.IsCreating)
        {
            TaskbarProgressState = TaskbarItemProgressState.Normal;
            TaskbarProgressValue = SRSCreator.ProgressPercent / 100.0;
        }
        else if (Reconstructor.IsRunning)
        {
            TaskbarProgressState = TaskbarItemProgressState.Normal;
            TaskbarProgressValue = Reconstructor.ProgressPercent / 100.0;
        }
        else if (SRSReconstructor.IsRebuilding)
        {
            TaskbarProgressState = TaskbarItemProgressState.Normal;
            TaskbarProgressValue = SRSReconstructor.ProgressPercent / 100.0;
        }
        else if (SampleRestorer.IsRestoring)
        {
            TaskbarProgressState = TaskbarItemProgressState.Normal;
            TaskbarProgressValue = SampleRestorer.ProgressPercent / 100.0;
        }
        else
        {
            TaskbarProgressState = TaskbarItemProgressState.None;
            TaskbarProgressValue = 0;
        }
    }

    /// <summary>
    /// Disposes child ViewModels that hold unmanaged resources.
    /// </summary>
    public void Cleanup()
    {
        Inspector.Dispose();
        FileCompare.Dispose();
    }
}
