using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using ReScene.App.Core.Services;
using ReScene.App.Core.ViewModels;
using ReScene.Manager.Services;
using ReScene.SRR;

namespace ReScene.Manager.Tests;

/// <summary>
/// Proves the full <see cref="MainWindowViewModel"/> graph wires under Avalonia, using the exact
/// seam implementations and parameter order <see cref="App.OnFrameworkInitializationCompleted"/>
/// uses. <see cref="AppDataConfig.FolderName"/> is pointed at a unique temp folder for the
/// duration of the test so it never touches the real <c>%LOCALAPPDATA%</c>; the class shares the
/// "AppDataConfig" collection with <see cref="AppDataConfigTests"/> so the two never mutate the
/// shared static concurrently.
/// </summary>
[Collection("AppDataConfig")]
public class CompositionRootTests
{
    [AvaloniaFact]
    public void Constructs_FullGraph_AllChildViewModelsNonNull()
    {
        string originalFolder = AppDataConfig.FolderName;
        AppDataConfig.FolderName = $"ReScene.Manager.Tests-{Guid.NewGuid():N}";
        try
        {
            var tempDir = new TempDirectoryService();
            var appSettings = new AppSettingsService();
            var fileDialog = new AvaloniaFileDialogService(static () => null);
            var imageLoader = new AvaloniaImageLoader();

            var vm = new MainWindowViewModel(
                new SRRCreationService(), new SRSCreationService(), new SRSReconstructionService(),
                new SampleRestorerService(tempDir), new BruteForceService(), new FileCompareService(appSettings),
                fileDialog, new RecentFilesService(appSettings), tempDir, new SRREditingService(),
                new SRRVerifyService(), new PropertyExportService(), appSettings, new HexDiffComputer(),
                new AvaloniaUiTimerFactory(),
                new AvaloniaFilePreviewService(imageLoader, static () => null),
                new AvaloniaImagePreviewService(imageLoader, fileDialog, static () => null),
                new AvaloniaUiDispatcher());

            Assert.NotNull(vm.Home);
            Assert.NotNull(vm.Inspector);
            Assert.NotNull(vm.Creator);
            Assert.NotNull(vm.SRSCreator);
            Assert.NotNull(vm.Reconstructor);
            Assert.NotNull(vm.SRSReconstructor);
            Assert.NotNull(vm.SampleRestorer);
            Assert.NotNull(vm.FileCompare);
            Assert.NotNull(vm.Beginner);
        }
        finally
        {
            AppDataConfig.FolderName = originalFolder;
        }
    }

    /// <summary>
    /// A progress-raising <see cref="ISRRCreationService"/> stand-in. The composition root's real
    /// services expose no way to raise <c>Progress</c> from a test, and this test's whole subject is
    /// WHICH view-model receives it — so the injected advanced service is a double while every other
    /// seam stays the production implementation.
    /// </summary>
    private sealed class ProbeSRRCreationService : ISRRCreationService
    {
        public event EventHandler<SRRCreationProgressEventArgs>? Progress;

        public void RaiseProgress(string message) =>
            Progress?.Invoke(this, new SRRCreationProgressEventArgs { ProgressPercent = 63, Message = message });

        public Task<SRRCreationResult> CreateFromRARAsync(string outputPath, IReadOnlyList<string> rarVolumePaths,
            IReadOnlyList<StoredFileEntry>? storedFiles, SRRCreationOptions options, CancellationToken ct) =>
            throw new NotSupportedException("This probe never creates; it only publishes progress.");

        public Task<SRRCreationResult> CreateFromSFVAsync(string outputPath, string sfvFilePath,
            IReadOnlyList<StoredFileEntry>? additionalFiles, SRRCreationOptions options, CancellationToken ct) =>
            throw new NotSupportedException("This probe never creates; it only publishes progress.");

        public Task<SRRCreationResult> CreateFromInputsAsync(string outputPath, IReadOnlyList<string> inputFiles,
            string? rootFolder, bool storeRelativePaths, IReadOnlyList<StoredFileEntry>? additionalFiles,
            SRRCreationOptions options, CancellationToken ct) =>
            throw new NotSupportedException("This probe never creates; it only publishes progress.");
    }

    /// <summary>
    /// The Advanced tab's Creator and the Beginner wizard's Creator must not share a progress
    /// stream. Each <see cref="CreatorViewModel"/> subscribes its injected
    /// <see cref="ISRRCreationService"/> in its constructor and never unsubscribes, so if the
    /// composition root ever hands the wizard the SAME service instance the advanced tab got, an SRR
    /// build in one tab streams its progress and log into the other. This asserts the wiring at the
    /// composition root — <c>CreatorViewModelTests</c> pins the view-model's own isolation given
    /// distinct publishers, which cannot see a sharing mistake made HERE.
    /// </summary>
    [AvaloniaFact]
    public void Wizard_And_AdvancedCreator_DoNotShareAProgressStream()
    {
        string originalFolder = AppDataConfig.FolderName;
        AppDataConfig.FolderName = $"ReScene.Manager.Tests-{Guid.NewGuid():N}";
        try
        {
            var tempDir = new TempDirectoryService();
            var appSettings = new AppSettingsService();
            var fileDialog = new AvaloniaFileDialogService(static () => null);
            var imageLoader = new AvaloniaImageLoader();
            var advancedSrr = new ProbeSRRCreationService();

            var vm = new MainWindowViewModel(
                advancedSrr, new SRSCreationService(), new SRSReconstructionService(),
                new SampleRestorerService(tempDir), new BruteForceService(), new FileCompareService(appSettings),
                fileDialog, new RecentFilesService(appSettings), tempDir, new SRREditingService(),
                new SRRVerifyService(), new PropertyExportService(), appSettings, new HexDiffComputer(),
                new AvaloniaUiTimerFactory(),
                new AvaloniaFilePreviewService(imageLoader, static () => null),
                new AvaloniaImagePreviewService(imageLoader, fileDialog, static () => null),
                new AvaloniaUiDispatcher());

            CreatorViewModel wizard = vm.Beginner.CreateSRRWizard;
            Assert.NotSame(vm.Creator, wizard);

            int wizardProgressBefore = wizard.ProgressPercent;
            string wizardMessageBefore = wizard.ProgressMessage;
            string[] wizardLogBefore = [.. wizard.LogEntries];

            advancedSrr.RaiseProgress("advanced-tab progress line");

            // CreatorViewModel.OnProgress marshals through IUiDispatcher.Post, and the composition
            // root's real AvaloniaUiDispatcher queues rather than running inline — pump the queue so
            // the posted updates land before they are asserted on.
            Dispatcher.UIThread.RunJobs();

            // The tab that owns this service saw it...
            Assert.Equal(63, vm.Creator.ProgressPercent);
            Assert.Contains(vm.Creator.LogEntries, l => l.Contains("advanced-tab progress line", StringComparison.Ordinal));

            // ...and the wizard's Creator is completely untouched.
            Assert.Equal(wizardProgressBefore, wizard.ProgressPercent);
            Assert.Equal(wizardMessageBefore, wizard.ProgressMessage);
            Assert.Equal(wizardLogBefore, wizard.LogEntries);
        }
        finally
        {
            AppDataConfig.FolderName = originalFolder;
        }
    }
}
