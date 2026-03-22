using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ReScene.NET.Services;

namespace ReScene.NET.ViewModels;

public partial class MainWindowViewModel : ViewModelBase
{
    private readonly IFileDialogService _fileDialog;
    private readonly IRecentFilesService _recentFiles;

    public HomeViewModel Home { get; }
    public InspectorViewModel Inspector { get; }
    public CreatorViewModel Creator { get; }
    public SrsCreatorViewModel SrsCreator { get; }
    public ReconstructorViewModel Reconstructor { get; }
    public SrsReconstructorViewModel SrsReconstructor { get; }
    public SampleRestorerViewModel SampleRestorer { get; }
    public FileCompareViewModel FileCompare { get; }

    [ObservableProperty]
    private int _selectedTabIndex;

    [ObservableProperty]
    private string _windowTitle = "ReScene.NET";

    [ObservableProperty]
    private string _statusMessage = "Ready";

    [ObservableProperty]
    private bool _isBusy;

    public MainWindowViewModel()
        : this(new SrrCreationService(), new SrsCreationService(), new SrsReconstructionService(), new SampleRestorerService(), new BruteForceService(), new FileCompareService(), new FileDialogService(), new RecentFilesService())
    {
    }

    public MainWindowViewModel(ISrrCreationService srrService, ISrsCreationService srsService, ISrsReconstructionService srsReconService, ISampleRestorerService sampleRestorerService, IBruteForceService bruteForceService, IFileCompareService fileCompareService, IFileDialogService fileDialog, IRecentFilesService recentFiles)
    {
        _fileDialog = fileDialog;
        _recentFiles = recentFiles;

        Inspector = new InspectorViewModel(fileDialog);
        Creator = new CreatorViewModel(srrService, fileDialog);
        SrsCreator = new SrsCreatorViewModel(srsService, fileDialog);
        Reconstructor = new ReconstructorViewModel(bruteForceService, fileDialog);
        SrsReconstructor = new SrsReconstructorViewModel(srsReconService, fileDialog);
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
                StatusMessage = Inspector.StatusMessage;
            else if (e.PropertyName == nameof(InspectorViewModel.IsExporting))
                UpdateIsBusy();
        };

        Creator.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(CreatorViewModel.IsCreating))
                UpdateIsBusy();
        };

        SrsCreator.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(SrsCreatorViewModel.IsCreating))
                UpdateIsBusy();
        };

        Reconstructor.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ReconstructorViewModel.IsRunning))
                UpdateIsBusy();
        };

        SrsReconstructor.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(SrsReconstructorViewModel.IsRebuilding))
                UpdateIsBusy();
        };

        SampleRestorer.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(SampleRestorerViewModel.IsRestoring))
                UpdateIsBusy();
        };
    }

    [RelayCommand]
    private async Task ExportStoredFileAsync() => await Inspector.ExportBlockCommand.ExecuteAsync(null);

    [RelayCommand]
    private async Task OpenFileAsync()
    {
        string? path = await _fileDialog.OpenFileAsync(
            "Open Scene File", ["Scene Files|*.srr;*.srs", "SRR Files|*.srr", "SRS Files|*.srs", "All Files|*.*"]);

        if (path != null)
            OpenSceneFile(path);
    }

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

    public void Cleanup()
    {
        Inspector.Dispose();
        FileCompare.Dispose();
    }
}
