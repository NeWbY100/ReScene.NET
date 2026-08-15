using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using ReScene.App.Core.Models;
using ReScene.App.Core.Services;
using ReScene.App.Core.ViewModels;
using ReScene.App.Core.ViewModels.Wizards;
using ReScene.Manager.Services;
using ReScene.Manager.Views.Wizards;
using ReScene.SRR;
using ReScene.SRS;

namespace ReScene.Manager.Tests;

/// <summary>
/// Headless render tests for the ported Beginner "Create an SRR" wizard body
/// (<see cref="CreateSRRWizardBody"/>). The body's DataContext is a <see cref="CreatorViewModel"/>;
/// its five step panels are <c>IsVisible</c>-bound to the hosting Window's
/// <see cref="WizardViewModel.CurrentStepIndex"/> via <c>$parent[Window]</c> + the
/// <c>IndexEqualsConverter</c>. The central gate is <b>zero binding errors</b> (via
/// <see cref="BindingErrorSink"/>); the tests also confirm the panels toggle with the index and that
/// the stored-files step hosts the expected two-column <c>DataGrid</c>. The creation pipeline
/// and file dialogs are inert fakes — only the view wiring is exercised.
/// </summary>
public class CreateSRRWizardBodyTests
{
    // ── Inert service doubles (the view test never runs a creation) ──

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

    private sealed class InlineUiDispatcher : IUiDispatcher
    {
        public void Invoke(Action action) => action();
        public void Post(Action action) => action();
        public void Post(Action action, UiDispatcherPriority priority) => action();
        public bool CheckAccess() => true;
    }

    private static CreatorViewModel CreateViewModel() =>
        new(
            new InertSrrCreationService(),
            new InertSrsCreationService(),
            new AvaloniaFileDialogService(static () => null), // headless: dialogs no-op, never block
            new InertTempDirectoryService(),
            new DefaultAppSettingsService(),
            new InlineUiDispatcher(),
            new InertReleaseScanner());

    private static WizardViewModel CreateWizard(CreatorViewModel content) =>
        new("Create an SRR", content,
        [
            new WizardStep { Title = "Release" },
            new WizardStep { Title = "Samples & subtitles" },
            new WizardStep { Title = "Stored files" },
            new WizardStep { Title = "Save as" },
            new WizardStep { Title = "Create" },
        ]);

    // Mirror how WizardWindow wires them: the Window's DataContext is the WizardViewModel; the body's
    // DataContext is the task VM (its Content). Returns both so tests can drive CurrentStepIndex.
    private static (Window window, CreateSRRWizardBody body, WizardViewModel wizard) Show(CreatorViewModel vm)
    {
        WizardViewModel wizard = CreateWizard(vm);
        var body = new CreateSRRWizardBody { DataContext = wizard.Content };
        // Set the Window's DataContext (the WizardViewModel that the step panels reach via
        // $parent[Window]) before parenting the body, so its ancestor binding never sees a null.
        var window = new Window { Width = 900, Height = 700, DataContext = wizard, Content = body };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return (window, body, wizard);
    }

    [AvaloniaFact]
    public void StepPanels_ToggleWithCurrentStepIndex_NoBindingErrors()
    {
        CreatorViewModel vm = CreateViewModel();

        using var sink = new BindingErrorSink();
        (_, CreateSRRWizardBody body, WizardViewModel wizard) = Show(vm);

        // The root grid's direct children are the five step panels, in order.
        Grid root = Assert.IsType<Grid>(body.Content);
        Assert.Equal(5, root.Children.Count);

        wizard.CurrentStepIndex = 0;
        Dispatcher.UIThread.RunJobs();
        Assert.True(root.Children[0].IsVisible);
        Assert.False(root.Children[1].IsVisible);
        Assert.False(root.Children[4].IsVisible);

        // Bump to another step and confirm the visibility follows the index.
        wizard.CurrentStepIndex = 2;
        Dispatcher.UIThread.RunJobs();
        Assert.False(root.Children[0].IsVisible);
        Assert.True(root.Children[2].IsVisible);
        Assert.False(root.Children[3].IsVisible);

        Assert.Empty(sink.Messages);
    }

    [AvaloniaFact]
    public void StoredFilesStep_HasTwoColumnGrid_NoBindingErrors()
    {
        CreatorViewModel vm = CreateViewModel();

        using var sink = new BindingErrorSink();
        (Window window, _, WizardViewModel wizard) = Show(vm);

        // Reveal the stored-files step so the grid realizes, then assert its two columns.
        wizard.CurrentStepIndex = 2;
        Dispatcher.UIThread.RunJobs();

        DataGrid grid = window.GetVisualDescendants().OfType<DataGrid>().Single();
        Assert.Equal(2, grid.Columns.Count);
        Assert.Equal("Stored name", grid.Columns[0].Header);
        Assert.Equal("Source file", grid.Columns[1].Header);
        Assert.Same(vm.StoredFiles, grid.ItemsSource);

        Assert.Empty(sink.Messages);
    }
}
