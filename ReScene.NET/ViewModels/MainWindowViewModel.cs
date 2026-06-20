using System.ComponentModel;
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

    /// <summary>
    /// A long-running task VM: its property source (for change notifications), the busy and
    /// progress property names to watch, and accessors for the current busy flag and
    /// 0..1 progress value.
    /// </summary>
    private sealed record TaskRegistration(
        INotifyPropertyChanged Source,
        string BusyProperty,
        string ProgressProperty,
        Func<bool> IsBusy,
        Func<double> Progress);

    private readonly TaskRegistration[] _taskRegistrations;

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
        : this(new SRRCreationService(), new SRSCreationService(), new SRSReconstructionService(), new SampleRestorerService(new TempDirectoryService()), new BruteForceService(), new FileCompareService(new AppSettingsService()), new FileDialogService(), new RecentFilesService(new AppSettingsService()), new TempDirectoryService(), new SRREditingService(), new SRRVerifyService(), new PropertyExportService(), new AppSettingsService(), new HexDiffComputer(), new WpfDispatcher())
    {
    }

    public MainWindowViewModel(ISrrCreationService srrService, ISrsCreationService srsService, ISrsReconstructionService srsReconService, ISampleRestorerService sampleRestorerService, IBruteForceService bruteForceService, IFileCompareService fileCompareService, IFileDialogService fileDialog, IRecentFilesService recentFiles, ITempDirectoryService tempDir, ISrrEditingService srrEditingService, ISrrVerifyService srrVerifyService, IPropertyExportService propertyExportService, IAppSettingsService appSettingsService, IHexDiffComputer hexDiffComputer, IUiDispatcher? uiDispatcher = null)
    {
        _fileDialog = fileDialog;
        _recentFiles = recentFiles;
        _appSettingsService = appSettingsService;

        IUiDispatcher dispatcher = uiDispatcher ?? new WpfDispatcher();

        var imagePreviewService = new ImagePreviewService(fileDialog);
        Inspector = new InspectorViewModel(fileDialog, srrEditingService, srrVerifyService, propertyExportService, imagePreviewService, appSettingsService);
        Creator = new CreatorViewModel(srrService, srsService, fileDialog, tempDir, appSettingsService, dispatcher);
        SRSCreator = new SRSCreatorViewModel(srsService, fileDialog, tempDir, appSettingsService, dispatcher);
        Reconstructor = new ReconstructorViewModel(bruteForceService, fileDialog, appSettingsService, dispatcher);
        SRSReconstructor = new SRSReconstructorViewModel(srsReconService, fileDialog, tempDir, dispatcher);
        SampleRestorer = new SampleRestorerViewModel(sampleRestorerService, fileDialog, dispatcher);
        FileCompare = new FileCompareViewModel(fileCompareService, fileDialog, hexDiffComputer, dispatcher);

        var beginnerRestore = new BeginnerRestoreViewModel(fileDialog)
        {
            BulkRestorer = SampleRestorer,
            SingleRebuilder = SRSReconstructor,
        };
        Beginner = new BeginnerShellViewModel
        {
            // A dedicated CreatorViewModel (not the Advanced tab's shared one) so the wizard's
            // state and build never collide with the Advanced SRR Creator tab.
            CreateSrrWizard = new CreatorViewModel(srrService, srsService, fileDialog, tempDir, appSettingsService, dispatcher),
            SRSCreator = SRSCreator,
            Reconstructor = Reconstructor,
            Restore = beginnerRestore,
            SrrEditor = new SrrEditorViewModel(srrEditingService, fileDialog, tempDir, imagePreviewService),
        };

        Home = new HomeViewModel(
            recentFiles,
            openFile: OpenSceneFile,
            switchToCreator: () => SelectedTabIndex = 2,
            openDialog: OpenFileAsync,
            fileDialog: fileDialog);

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

        // Each long-running task VM contributes a busy flag and a 0..1 progress value.
        // The taskbar reflects the first busy task in declared order.
        _taskRegistrations =
        [
            new(Creator, nameof(CreatorViewModel.IsCreating), nameof(CreatorViewModel.ProgressPercent),
                () => Creator.IsCreating, () => Creator.ProgressPercent / 100.0),
            new(SRSCreator, nameof(SRSCreatorViewModel.IsCreating), nameof(SRSCreatorViewModel.ProgressPercent),
                () => SRSCreator.IsCreating, () => SRSCreator.ProgressPercent / 100.0),
            new(Reconstructor, nameof(ReconstructorViewModel.IsRunning), nameof(ReconstructorViewModel.ProgressPercent),
                () => Reconstructor.IsRunning, () => Reconstructor.ProgressPercent / 100.0),
            new(SRSReconstructor, nameof(SRSReconstructorViewModel.IsRebuilding), nameof(SRSReconstructorViewModel.ProgressPercent),
                () => SRSReconstructor.IsRebuilding, () => SRSReconstructor.ProgressPercent / 100.0),
            new(SampleRestorer, nameof(SampleRestorerViewModel.IsRestoring), nameof(SampleRestorerViewModel.ProgressPercent),
                () => SampleRestorer.IsRestoring, () => SampleRestorer.ProgressPercent / 100.0),
        ];

        foreach (TaskRegistration reg in _taskRegistrations)
        {
            reg.Source.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == reg.BusyProperty || e.PropertyName == reg.ProgressProperty)
                {
                    UpdateIsBusy();
                    UpdateTaskbarProgress();
                }
            };
        }

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
        => IsBusy = _taskRegistrations.Any(r => r.IsBusy()) || Inspector.IsExporting;

    private void UpdateTaskbarProgress()
    {
        // Reflect the first busy task in declared order (Inspector has no progress bar).
        TaskRegistration? busy = _taskRegistrations.FirstOrDefault(r => r.IsBusy());

        if (busy is not null)
        {
            TaskbarProgressState = TaskbarItemProgressState.Normal;
            TaskbarProgressValue = busy.Progress();
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
