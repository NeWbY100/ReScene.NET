using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using ReScene.App.Core.Services;
using ReScene.App.Core.ViewModels;
using ReScene.Core;
using ReScene.Manager.Services;
using ReScene.Manager.Views;

namespace ReScene.Manager.Tests;

/// <summary>
/// Headless render tests for the ported <see cref="BruteForceProgressWindow"/>. The window's central
/// gate is <b>zero binding errors</b> (via <see cref="BindingErrorSink"/>) with a
/// <see cref="ReconstructorViewModel"/> DataContext, plus: the phase/progress/stats text bind, the
/// version-attempt <c>DataGrid</c> renders its 8 columns and seeded rows with a Copy
/// context menu, the Open-Folder button's visibility tracks <c>LastRunSucceeded</c>, and the
/// Stop/Close button flips state (content/style/enabled) when <c>IsRunning</c> goes false. Live
/// clipboard copy, live auto-scroll, and the Stop command actually cancelling a run need a real
/// owning Window/process and are covered by the Reconstructor tab's own launch-smoke check, not exercised here.
/// </summary>
public class BruteForceProgressWindowTests
{
    // ── Inert service doubles (no run is ever actually started) ──

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

    private sealed class InlineUiDispatcher : IUiDispatcher
    {
        public void Invoke(Action action) => action();
        public void Post(Action action) => action();
        public void Post(Action action, UiDispatcherPriority priority) => action();
        public bool CheckAccess() => true;
    }

    /// <summary>No-op timer factory: the elapsed-time timer never ticks in these tests.</summary>
    private sealed class InertUiTimerFactory : IUiTimerFactory
    {
        public IUiTimer Create(TimeSpan interval, Action onTick) => new NoOpTimer();

        private sealed class NoOpTimer : IUiTimer
        {
            public void Start() { }
            public void Stop() { }
        }
    }

    private static ReconstructorViewModel CreateVm() =>
        new(
            new InertBruteForceService(),
            new AvaloniaFileDialogService(static () => null), // headless: dialogs no-op, never block
            new InlineUiDispatcher(),
            new InertUiTimerFactory());

    private static ReconstructorViewModel.VersionEntry Row(string setText, string version, string status, string result, string args) =>
        new()
        {
            SetText = setText,
            VersionName = version,
            Status = status,
            Result = result,
            Arguments = args,
            VersionDirectory = @"C:\WinRAR\" + version,
        };

    private static void SeedProgress(ReconstructorViewModel vm)
    {
        vm.PhaseDescription = "Testing WinRAR versions...";
        vm.TestCountText = "Test 3 of 10";
        vm.ProgressPercentText = "30%";
        vm.ProgressPercent = 30;
        vm.CurrentDetailText = "Testing WinRAR 5.90 (Set 1/2)...";
        vm.ElapsedText = "00:12";
        vm.RemainingText = "00:28";
        vm.SpeedText = "2.5/s";
        vm.EtaText = "00:28";
    }

    [AvaloniaFact]
    public void PhaseHeading_IsPoliteLiveRegion_SoCompletionAndErrorAggregateAnnounceOnce()
    {
        // WCAG 4.1.3: the Row-0 phase/completion heading is a Polite live region so the run's
        // completion status — including the "N could not run" error aggregate baked into
        // PhaseDescription — is announced without focus, and without the per-cell grid chatter a
        // per-row live region would cause (grid Status/Result stay plain 4.1.2 content).
        ReconstructorViewModel vm = CreateVm();
        SeedProgress(vm);

        var window = new BruteForceProgressWindow { DataContext = vm };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        TextBlock heading = window.GetVisualDescendants().OfType<TextBlock>()
            .First(t => t.Text == vm.PhaseDescription);
        Assert.Equal(AutomationLiveSetting.Polite, AutomationProperties.GetLiveSetting(heading));
    }

    [AvaloniaFact]
    public void Renders_PhaseProgressStatsAndGrid_NoBindingErrors()
    {
        ReconstructorViewModel vm = CreateVm();
        SeedProgress(vm);
        vm.VersionEntries.Add(Row("1/2", "5.90", "Testing", "", "a -m5 output.rar *.bin"));
        vm.VersionEntries.Add(Row("2/2", "5.71", "Complete", "Match", "a -m3 output.rar *.bin"));

        using var sink = new BindingErrorSink();
        var window = new BruteForceProgressWindow { DataContext = vm };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("Brute Force Progress", window.Title);

        TextBlock[] textBlocks = [.. window.GetVisualDescendants().OfType<TextBlock>()];
        Assert.Contains(textBlocks, t => t.Text == vm.PhaseDescription);
        Assert.Contains(textBlocks, t => t.Text == vm.TestCountText);
        Assert.Contains(textBlocks, t => t.Text == vm.ProgressPercentText);
        Assert.Contains(textBlocks, t => t.Text == vm.CurrentDetailText);
        Assert.Contains(textBlocks, t => t.Text == vm.ElapsedText);
        Assert.Contains(textBlocks, t => t.Text == vm.RemainingText);
        Assert.Contains(textBlocks, t => t.Text == vm.SpeedText);
        Assert.Contains(textBlocks, t => t.Text == vm.EtaText);

        ProgressBar bar = window.GetVisualDescendants().OfType<ProgressBar>().Single();
        Assert.Equal(30, bar.Value);

        // 8-column read-only grid, reflecting the VM's VersionEntries collection.
        DataGrid grid = window.GetVisualDescendants().OfType<DataGrid>().Single(g => g.Name == "VersionGrid");
        Assert.Equal(8, grid.Columns.Count);
        Assert.Equal(["Set", "Version", "Status", "Result", "Start", "End", "Duration", "Arguments"],
            grid.Columns.Select(c => c.Header).ToArray());
        Assert.True(grid.IsReadOnly);
        // Explicitly off: this was the ONE grid inheriting Avalonia's CanUserSortColumns=True
        // default — live click-to-sort on the flat header band has no assessed affordance;
        // all nine grids now agree. Re-gate before enabling sorting anywhere.
        Assert.False(grid.CanUserSortColumns);
        Assert.Same(vm.VersionEntries, grid.ItemsSource);

        int rows = window.GetVisualDescendants().OfType<DataGridRow>().Count();
        Assert.Equal(vm.VersionEntries.Count, rows);

        // Copy Arguments / Copy Full Command Line context menu.
        Assert.NotNull(grid.ContextMenu);
        string[] menuHeaders = [.. grid.ContextMenu!.Items.OfType<MenuItem>().Select(m => (string)m.Header!)];
        Assert.Equal(["Copy Arguments", "Copy Full Command Line"], menuHeaders);

        Assert.Empty(sink.Messages);
    }

    [AvaloniaFact]
    public void OpenFolderButton_VisibilityTracksLastRunSucceeded()
    {
        ReconstructorViewModel vm = CreateVm();
        vm.LastRunSucceeded = false;

        using var sink = new BindingErrorSink();
        var window = new BruteForceProgressWindow { DataContext = vm };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        Button openFolder = window.GetVisualDescendants().OfType<Button>().Single(b => b.Content is "Open Folder");
        Assert.False(openFolder.IsVisible);

        vm.LastRunSucceeded = true;
        Dispatcher.UIThread.RunJobs();
        Assert.True(openFolder.IsVisible);

        vm.LastRunSucceeded = false;
        Dispatcher.UIThread.RunJobs();
        Assert.False(openFolder.IsVisible);

        Assert.Empty(sink.Messages);
    }

    [AvaloniaFact]
    public void StopCloseButton_FlipsToCloseState_WhenIsRunningGoesFalse_ThenClosesOnClick()
    {
        ReconstructorViewModel vm = CreateVm();
        vm.IsRunning = true;

        using var sink = new BindingErrorSink();
        var window = new BruteForceProgressWindow { DataContext = vm };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        Button stopClose = window.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "btnStopClose");
        Assert.Equal("Stop", stopClose.Content);
        Assert.Contains("cancel", stopClose.Classes);

        // The Stop/Close state machine is driven off the VM's IsRunning property changing to false
        // (the tracker/service normally flips this at the end of a run).
        vm.IsRunning = false;
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("Close", stopClose.Content);
        Assert.True(stopClose.IsEnabled);
        Assert.Contains("primary", stopClose.Classes);
        Assert.DoesNotContain("cancel", stopClose.Classes);

        bool closed = false;
        window.Closed += (_, _) => closed = true;
        stopClose.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Dispatcher.UIThread.RunJobs();

        Assert.True(closed);
        Assert.Empty(sink.Messages);
    }
}
