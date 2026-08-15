using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using ReScene.App.Core.Models;
using ReScene.App.Core.Services;
using ReScene.Manager.Views;

namespace ReScene.Manager.Tests;

/// <summary>
/// Headless tests for <see cref="MainWindow"/>'s deferred normal-bounds capture (F2). The window
/// tracks its last-known Normal geometry so a maximized close still persists sensible restore bounds.
/// The hazard: the platform can deliver a size/position change slightly BEFORE <c>WindowState</c>
/// flips to Maximized, so a synchronous capture would record maximized geometry as "normal". The fix
/// defers each capture to Background priority and re-checks the window state when it runs; these tests
/// read back the captured bounds through the maximized-close save path (which reads the tracked normal
/// fields) via a recording <see cref="IWindowStateService"/>.
/// </summary>
public class MainWindowBoundsCaptureTests
{
    private sealed class RecordingWindowState(WindowStateModel restore) : IWindowStateService
    {
        public WindowStateModel? Load() => restore;

        public WindowStateModel? Saved { get; private set; }

        public void Save(WindowStateModel state) => Saved = state;
    }

    private static WindowStateModel NormalRestore() => new()
    {
        Left = 50,
        Top = 50,
        Width = 1100,
        Height = 720,
        IsMaximized = false,
        SelectedTabIndex = 0,
    };

    [AvaloniaFact]
    public void NormalResize_IsCaptured_AndPersistedOnMaximizedClose()
    {
        var state = new RecordingWindowState(NormalRestore());
        var window = new MainWindow { WindowStateService = state };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        // Resize while Normal, then let the deferred capture commit the new normal bounds.
        window.Width = 1000;
        window.Height = 650;
        window.CaptureNormalBounds();
        Dispatcher.UIThread.RunJobs();

        // Maximize (its own deferred capture must self-cancel and not overwrite the normal bounds).
        window.WindowState = WindowState.Maximized;
        Dispatcher.UIThread.RunJobs();

        window.Close();

        Assert.NotNull(state.Saved);
        Assert.True(state.Saved.IsMaximized);
        Assert.Equal(1000, state.Saved.Width);
        Assert.Equal(650, state.Saved.Height);
    }

    [AvaloniaFact]
    public void MaximizeInFlight_DoesNotPersistMaximizedGeometryAsNormal()
    {
        var state = new RecordingWindowState(NormalRestore());
        var window = new MainWindow { WindowStateService = state };
        window.Show();
        Dispatcher.UIThread.RunJobs(); // tracked normal bounds = 1100 x 720

        // The race: a maximize-sized change arrives and triggers a capture while still Normal, but the
        // window is Maximized BEFORE the deferred capture runs. The deferred commit must re-check the
        // state and self-cancel, leaving the pre-maximize normal bounds intact.
        window.Width = 1920;
        window.Height = 1080;
        window.CaptureNormalBounds();          // posts a deferred capture (not yet committed)
        window.WindowState = WindowState.Maximized; // state flips before the deferred job runs
        Dispatcher.UIThread.RunJobs();         // deferred commit sees Maximized -> skips

        window.Close();

        Assert.NotNull(state.Saved);
        Assert.True(state.Saved.IsMaximized);
        // The persisted "normal" bounds are the pre-maximize size, NOT the 1920 x 1080 maximize
        // geometry — a synchronous capture would have wrongly recorded the latter.
        Assert.Equal(1100, state.Saved.Width);
        Assert.Equal(720, state.Saved.Height);
    }
}
