using System.Reflection;
using System.Windows.Shell;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ReScene.NET.Helpers;
using ReScene.NET.Services;

namespace ReScene.NET.ViewModels;

/// <summary>
/// Root ViewModel that owns all child ViewModels and coordinates tab navigation and status aggregation.
/// </summary>
public partial class MainWindowViewModel : ViewModelBase
{
    private readonly IFileDialogService _fileDialog;
    private readonly IRecentFilesService _recentFiles;

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
    public SrsCreatorViewModel SrsCreator
    {
        get;
    }
    public ReconstructorViewModel Reconstructor
    {
        get;
    }
    public SrsReconstructorViewModel SrsReconstructor
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

    [ObservableProperty]
    private int _selectedTabIndex;

    [ObservableProperty]
    private string _windowTitle = "ReScene.NET";

    [ObservableProperty]
    private string _statusMessage = "Ready";

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private TaskbarItemProgressState _taskbarProgressState = TaskbarItemProgressState.None;

    [ObservableProperty]
    private double _taskbarProgressValue;

    public string AppVersion { get; } = GetAppVersion();

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
        : this(new SrrCreationService(), new SrsCreationService(), new SrsReconstructionService(), new SampleRestorerService(new TempDirectoryService()), new BruteForceService(), new FileCompareService(), new FileDialogService(), new RecentFilesService(), new TempDirectoryService(), new SrrEditingService())
    {
    }

    public MainWindowViewModel(ISrrCreationService srrService, ISrsCreationService srsService, ISrsReconstructionService srsReconService, ISampleRestorerService sampleRestorerService, IBruteForceService bruteForceService, IFileCompareService fileCompareService, IFileDialogService fileDialog, IRecentFilesService recentFiles, ITempDirectoryService tempDir, ISrrEditingService srrEditingService)
    {
        _fileDialog = fileDialog;
        _recentFiles = recentFiles;

        Inspector = new InspectorViewModel(fileDialog, srrEditingService);
        Creator = new CreatorViewModel(srrService, srsService, fileDialog, tempDir);
        SrsCreator = new SrsCreatorViewModel(srsService, fileDialog, tempDir);
        Reconstructor = new ReconstructorViewModel(bruteForceService, fileDialog);
        SrsReconstructor = new SrsReconstructorViewModel(srsReconService, fileDialog, tempDir);
        SampleRestorer = new SampleRestorerViewModel(sampleRestorerService, fileDialog);
        FileCompare = new FileCompareViewModel(fileCompareService, fileDialog);
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

        SrsCreator.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(SrsCreatorViewModel.IsCreating) or nameof(SrsCreatorViewModel.ProgressPercent))
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

        SrsReconstructor.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(SrsReconstructorViewModel.IsRebuilding) or nameof(SrsReconstructorViewModel.ProgressPercent))
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
            || SrsCreator.IsCreating
            || Reconstructor.IsRunning
            || SrsReconstructor.IsRebuilding
            || SampleRestorer.IsRestoring;
    }

    private void UpdateTaskbarProgress()
    {
        if (Creator.IsCreating)
        {
            TaskbarProgressState = TaskbarItemProgressState.Normal;
            TaskbarProgressValue = Creator.ProgressPercent / 100.0;
        }
        else if (SrsCreator.IsCreating)
        {
            TaskbarProgressState = TaskbarItemProgressState.Normal;
            TaskbarProgressValue = SrsCreator.ProgressPercent / 100.0;
        }
        else if (Reconstructor.IsRunning)
        {
            TaskbarProgressState = TaskbarItemProgressState.Normal;
            TaskbarProgressValue = Reconstructor.ProgressPercent / 100.0;
        }
        else if (SrsReconstructor.IsRebuilding)
        {
            TaskbarProgressState = TaskbarItemProgressState.Normal;
            TaskbarProgressValue = SrsReconstructor.ProgressPercent / 100.0;
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
