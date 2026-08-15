using ReScene.App.Core.Models;
using ReScene.App.Core.Services;
using ReScene.App.Core.ViewModels;
using ReScene.Core;
using ReScene.Manager.Services;
using ReScene.Manager.Views.Wizards;
using ReScene.SRR;
using ReScene.SRS;

namespace ReScene.Manager.Tests;

/// <summary>
/// Assembles a fully-wired <see cref="BeginnerShellViewModel"/> for the T5.2 wizard tests using the
/// same inert service doubles as the individual wizard-body tests
/// (<c>CreateSRRWizardBodyTests</c>/<c>CreateSRSWizardBodyTests</c>/<c>ReconstructWizardBodyTests</c>/
/// <c>RestoreWizardBodyTests</c>/<c>EditSRRWizardBodyTests</c>). Every task VM is real but backed by
/// doubles that never touch the disk or start a run; the <see cref="IFileDialogService"/> is a
/// headless <see cref="AvaloniaFileDialogService"/> (no active window → dialogs no-op, confirms
/// return false), so <see cref="BeginnerWizardFactory"/> can be exercised without any live I/O.
/// </summary>
internal static class BeginnerShellTestFactory
{
    // Headless dialog service: with no active window the sync members never block and Confirm returns
    // false, matching how the wizard-body tests wire their VMs.
    private static AvaloniaFileDialogService HeadlessDialog() => new(static () => null);

    /// <summary>
    /// Builds the shell. Pass <paramref name="fileDialogOverride"/> to control the sync
    /// <see cref="IFileDialogService.Confirm"/> the factory's wizard steps call (e.g. a stub that
    /// returns a known value); null uses the headless dialog (confirms return false).
    /// </summary>
    public static BeginnerShellViewModel Create(IFileDialogService? fileDialogOverride = null)
    {
        IFileDialogService fileDialog = fileDialogOverride ?? HeadlessDialog();
        var dispatcher = new InlineUiDispatcher();
        var tempDir = new InertTempDirectoryService();
        var appSettings = new DefaultAppSettingsService();

        var restore = new BeginnerRestoreViewModel(fileDialog)
        {
            BulkRestorer = new SampleRestorerViewModel(new InertSampleRestorerService(), fileDialog, dispatcher),
            SingleRebuilder = new SRSReconstructorViewModel(
                new InertSrsReconstructionService(), fileDialog, tempDir, dispatcher),
        };

        return new BeginnerShellViewModel
        {
            CreateSRRWizard = new CreatorViewModel(
                new InertSrrCreationService(), new InertSrsCreationService(), fileDialog, tempDir, appSettings, dispatcher, new InertReleaseScanner()),
            SRSCreator = new SRSCreatorViewModel(
                new InertSrsCreationService(), fileDialog, tempDir, appSettings, dispatcher),
            Reconstructor = new ReconstructorViewModel(
                new InertBruteForceService(), fileDialog, dispatcher, new InertUiTimerFactory()),
            Restore = restore,
            SRREditor = new SRREditorViewModel(
                new InertSRREditingService(), fileDialog, tempDir, new InertFilePreviewService()),
            FileDialog = fileDialog,
        };
    }

    // ── Inert service doubles (never invoked by the factory/window/hub tests) ──

    private sealed class InlineUiDispatcher : IUiDispatcher
    {
        public void Invoke(Action action) => action();
        public void Post(Action action) => action();
        public void Post(Action action, UiDispatcherPriority priority) => action();
        public bool CheckAccess() => true;
    }

    private sealed class InertTempDirectoryService : ITempDirectoryService
    {
        public string CreateTempDirectory() => Path.GetTempPath();
        public void Cleanup(string? tempDir) { }
    }

    private sealed class DefaultAppSettingsService : IAppSettingsService
    {
        public event EventHandler? Changed { add { } remove { } }
        public AppSettings Load() => new();
        public void Save(AppSettings settings) { }
    }

    private sealed class InertSrrCreationService : ISRRCreationService
    {
        public event EventHandler<SRRCreationProgressEventArgs>? Progress { add { } remove { } }

        public Task<SRRCreationResult> CreateFromRARAsync(string outputPath, IReadOnlyList<string> rarVolumePaths,
            IReadOnlyList<StoredFileEntry>? storedFiles, SRRCreationOptions options, CancellationToken ct)
            => Task.FromResult(new SRRCreationResult { Success = true });

        public Task<SRRCreationResult> CreateFromSFVAsync(string outputPath, string sfvFilePath,
            IReadOnlyList<StoredFileEntry>? additionalFiles, SRRCreationOptions options, CancellationToken ct)
            => Task.FromResult(new SRRCreationResult { Success = true });

        public Task<SRRCreationResult> CreateFromInputsAsync(string outputPath, IReadOnlyList<string> inputFiles,
            string? rootFolder, bool storeRelativePaths, IReadOnlyList<StoredFileEntry>? additionalFiles,
            SRRCreationOptions options, CancellationToken ct)
            => Task.FromResult(new SRRCreationResult { Success = true });
    }

    private sealed class InertReleaseScanner : IReleaseScanner
    {
        public ReleaseScanResult Scan(string releaseRoot, CancellationToken ct = default) => new([], [], [], [], [], []);
    }

    private sealed class InertSrsCreationService : ISRSCreationService
    {
        public event EventHandler<SRSCreationProgressEventArgs>? Progress { add { } remove { } }
        public event EventHandler<SRSScanProgressEventArgs>? ScanProgress { add { } remove { } }

        public Task<SRSCreationResult> CreateAsync(string outputPath, string sampleFilePath, SRSCreationOptions options, CancellationToken ct)
            => Task.FromResult(new SRSCreationResult { Success = true });
    }

    private sealed class InertSampleRestorerService : ISampleRestorerService
    {
        public event EventHandler<SRSReconstructionProgressEventArgs>? Progress { add { } remove { } }

        public List<SRSEntryInfo> GetSRSEntries(string srrFilePath) => [];

        public Task<SRSReconstructionResult> RestoreSampleAsync(
            string srrFilePath, string srsFileName, string mediaFilePath, string outputPath, CancellationToken ct)
            => Task.FromResult(new SRSReconstructionResult(true, true, 0, 0, 0, 0, null));
    }

    private sealed class InertSrsReconstructionService : ISRSReconstructionService
    {
        public event EventHandler<SRSReconstructionProgressEventArgs>? Progress { add { } remove { } }
        public event EventHandler<SRSScanProgressEventArgs>? ScanProgress { add { } remove { } }

        public Task<SRSReconstructionResult> RebuildAsync(string srsFilePath, string mediaFilePath, string outputPath, CancellationToken ct)
            => Task.FromResult(new SRSReconstructionResult(true, true, 0, 0, 0, 0, null));
    }

    private sealed class InertBruteForceService : IBruteForceService
    {
        public event EventHandler<BruteForceProgressEventArgs>? Progress { add { } remove { } }
        public event EventHandler<BruteForceStatusChangedEventArgs>? StatusChanged { add { } remove { } }
        public event EventHandler<LogEventArgs>? LogMessage { add { } remove { } }
        public event EventHandler<FileCopyProgressEventArgs>? FileCopyProgress { add { } remove { } }
        public event EventHandler<CRCValidationProgressEventArgs>? CRCValidationProgress { add { } remove { } }
        public event EventHandler<TimestampPreservationFailedEventArgs>? TimestampPreservationFailed { add { } remove { } }

        public Task<BruteForceRunResult> RunAsync(BruteForceOptions options, CancellationToken cancellationToken = default)
            => Task.FromResult(new BruteForceRunResult(true, null));
    }

    private sealed class InertUiTimerFactory : IUiTimerFactory
    {
        public IUiTimer Create(TimeSpan interval, Action onTick) => new NoOpTimer();

        private sealed class NoOpTimer : IUiTimer
        {
            public void Start() { }
            public void Stop() { }
        }
    }

    private sealed class InertSRREditingService : ISRREditingService
    {
        public void AddStoredFiles(string srrFilePath, IReadOnlyList<(string StoredName, string FilePath)> files) { }
        public void RemoveStoredFiles(string srrFilePath, IReadOnlyList<string> storedNames) { }
        public Task RenameStoredFileAsync(string srrPath, string oldName, string newName, CancellationToken ct = default) => Task.CompletedTask;
        public Task MoveStoredFileAsync(string srrPath, string storedName, int offset, CancellationToken ct = default) => Task.CompletedTask;
        public IReadOnlyList<StoredFileInfo> GetStoredFiles(string srrFilePath) => [];
        public Task<string?> ExtractStoredFileAsync(string srrFilePath, string outputDirectory, string storedName, CancellationToken ct = default) => Task.FromResult<string?>(null);
        public Task<byte[]?> ReadStoredFileBytesAsync(string srrFilePath, string storedName, CancellationToken ct = default) => Task.FromResult<byte[]?>(null);
    }

    private sealed class InertFilePreviewService : IFilePreviewService
    {
        public void Preview(byte[] data, string fileName) { }
    }
}
