using System.ComponentModel;
using System.Diagnostics;
using System.Reflection;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ReScene.App.Core.Helpers;
using ReScene.App.Core.Models;
using ReScene.App.Core.Services;
namespace ReScene.App.Core.ViewModels;

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

    // Index of the Compare tab in the advanced shell's TabControl (AdvancedShellView.axaml order).
    private const int CompareTabIndex = 7;

    // Leaving the Compare tab closes both compare panes: they hold memory-mapped handles —
    // OS-level file locks — on the compared files, and a hidden tab must not keep user files
    // locked. Fire-and-forget in house style; CloseAllAsync no-ops when nothing is loaded and
    // waits out an in-flight compare before disposing.
    partial void OnSelectedTabIndexChanged(int oldValue, int newValue)
    {
        if (oldValue == CompareTabIndex && newValue != CompareTabIndex)
        {
            _ = FileCompare.CloseAllAsync();
        }
    }

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
        // Mode switching swaps the entire shell, hiding the Compare tab; release any files it
        // holds open (see OnSelectedTabIndexChanged). Applies to external mode changes too,
        // so it runs before the save-suppression check. No-ops during construction: the
        // initial Mode assignment happens after the child VMs exist and nothing is loaded.
        _ = FileCompare.CloseAllAsync();

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
    public partial string WindowTitle { get; set; } = AppInfo.DisplayName;

    [ObservableProperty]
    public partial string StatusMessage { get; set; } = "Ready";

    [ObservableProperty]
    public partial bool IsBusy { get; set; }

    [ObservableProperty]
    public partial TaskbarProgressState TaskbarProgressState { get; set; } = TaskbarProgressState.None;

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

    public MainWindowViewModel(ISRRCreationService srrService, ISRSCreationService srsService, ISRSReconstructionService srsReconService, ISampleRestorerService sampleRestorerService, IBruteForceService bruteForceService, IFileCompareService fileCompareService, IFileDialogService fileDialog, IRecentFilesService recentFiles, ITempDirectoryService tempDir, ISRREditingService srrEditingService, ISRRVerifyService srrVerifyService, IPropertyExportService propertyExportService, IAppSettingsService appSettingsService, IHexDiffComputer hexDiffComputer, IUiTimerFactory uiTimerFactory, IFilePreviewService filePreviewService, IImagePreviewService imagePreviewService, IUiDispatcher uiDispatcher)
    {
        ArgumentNullException.ThrowIfNull(uiDispatcher);

        _fileDialog = fileDialog;
        _recentFiles = recentFiles;
        _appSettingsService = appSettingsService;

        IUiDispatcher dispatcher = uiDispatcher;

        // Platform-aware URL/folder launcher; no WPF-specific implementation is needed (it lives
        // entirely in App.Core, mirroring ReScene.Lib's RarExecutable platform-detection style).
        var launcher = new SystemLauncherService();

        // Stateless — unlike ISRRCreationService/ISRSCreationService (see below) there's no
        // per-instance progress stream to keep separate, so both CreatorViewModel instances share
        // this one scanner.
        var releaseScanner = new ReleaseScanner();

        Inspector = new InspectorViewModel(fileDialog, srrEditingService, srrVerifyService, propertyExportService, imagePreviewService, appSettingsService);
        // Each creation VM gets its OWN creation-service instance. These services are stateless
        // wrappers that expose a dedicated SRRWriter/SRSWriter's progress events; sharing one
        // instance across VMs makes every subscriber receive that writer's progress, so a creation
        // in one VM would stream into the others' progress/log. The injected instances seed the
        // advanced Creator tab; the SRS Creator and the wizard below get their own.
        Creator = new CreatorViewModel(srrService, srsService, fileDialog, tempDir, appSettingsService, dispatcher, releaseScanner);
        SRSCreator = new SRSCreatorViewModel(new SRSCreationService(), fileDialog, tempDir, appSettingsService, dispatcher);
        Reconstructor = new ReconstructorViewModel(bruteForceService, fileDialog, dispatcher, uiTimerFactory, appSettingsService, tempDir, launcher);
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
            // state and build never collide with the Advanced SRR Creator tab. It also gets its
            // own creation-service instances so progress never crosses over to another VM.
            CreateSRRWizard = new CreatorViewModel(new SRRCreationService(), new SRSCreationService(), fileDialog, tempDir, appSettingsService, dispatcher, releaseScanner),
            SRSCreator = SRSCreator,
            Reconstructor = Reconstructor,
            Restore = beginnerRestore,
            SRREditor = new SRREditorViewModel(srrEditingService, fileDialog, tempDir, filePreviewService),
            FileDialog = fileDialog,
        };

        Home = new HomeViewModel(
            recentFiles,
            openFile: path => _ = OpenSceneFileAsync(path),
            switchToCreator: () => SelectedTabIndex = 2,
            openDialog: OpenFileAsync,
            fileDialog: fileDialog,
            launcher: launcher);

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
            "Open Scene File", FileDialogFilters.SceneFiles, Inspector.LoadedFilePath);

        if (path is not null)
        {
            await OpenSceneFileAsync(path);
        }
    }

    /// <summary>
    /// Opens a scene file (SRR/SRS) in the Inspector tab and updates the window title.
    /// </summary>
    /// <param name="filePath">
    /// Absolute path to the scene file.
    /// </param>
    public async Task OpenSceneFileAsync(string filePath)
    {
        Mode = UserMode.Advanced;
        SelectedTabIndex = 1; // Switch to Inspector tab immediately so the load is visible
        WindowTitle = $"{AppInfo.DisplayName} - {Path.GetFileName(filePath)}";

        await Inspector.LoadFileAsync(filePath);
        StatusMessage = Inspector.StatusMessage;

        // Recording the recent file is best-effort: a failure here (e.g. a locked settings file)
        // must not surface as an error or fault this method, which some callers fire-and-forget.
        try
        {
            _recentFiles.AddEntry(filePath);
            Home.LoadRecentFiles();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Failed to update recent files: {ex.Message}");
        }
    }

    private void UpdateIsBusy()
        => IsBusy = _taskRegistrations.Any(r => r.IsBusy()) || Inspector.IsExporting;

    private void UpdateTaskbarProgress()
    {
        // Reflect the first busy task in declared order (Inspector has no progress bar).
        TaskRegistration? busy = _taskRegistrations.FirstOrDefault(r => r.IsBusy());

        if (busy is not null)
        {
            TaskbarProgressState = TaskbarProgressState.Normal;
            TaskbarProgressValue = busy.Progress();
        }
        else
        {
            TaskbarProgressState = TaskbarProgressState.None;
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
        Reconstructor.Cleanup();
    }
}
