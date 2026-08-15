using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Platform;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using ReScene.App.Core;
using ReScene.App.Core.Models;
using ReScene.App.Core.Services;
using ReScene.App.Core.ViewModels;
using ReScene.Manager.Services;

namespace ReScene.Manager.Views;

/// <summary>
/// The application shell window, ported from the WPF <c>ReScene.NET.Views.MainWindow</c>. The
/// DataContext is a <see cref="MainWindowViewModel"/> supplied by the composition root
/// (<c>App.axaml.cs</c>). This code-behind carries the WPF window's non-MVVM behaviors: scene-file
/// drag-drop, Ctrl+1..7 tab switching, window-state persistence, command-line file open, the version
/// link launch, and the Settings/About/Exit menu handlers.
/// </summary>
public partial class MainWindow : Window
{
    private static readonly string[] _sceneExtensions = [".srr", ".srs", ".rar", ".mkv", ".webm"];

    // Avalonia has no WPF RestoreBounds, so we track the last known non-maximized geometry ourselves
    // (updated whenever the window is Normal) and persist it when the window closes while maximized.
    private double _normalLeft;
    private double _normalTop;
    private double _normalWidth = 1280;
    private double _normalHeight = 900;
    private bool _stateRestored;
    private bool _capturePending;

    // Windows-only taskbar progress consumer (ITaskbarList3); null off Windows / headless / on COM
    // failure. Subscribed to the VM's PropertyChanged while the window is open.
    private WindowsTaskbarProgress? _taskbarProgress;
    private MainWindowViewModel? _taskbarVm;

    /// <summary>Injected by the composition root before the window is shown.</summary>
    public IWindowStateService? WindowStateService { get; set; }

    public MainWindow()
    {
        AvaloniaXamlLoader.Load(this);

        DragDrop.SetAllowDrop(this, true);
        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        AddHandler(DragDrop.DropEvent, OnDrop);
        // Tunnel so Ctrl+1..7 is seen at the window before any focused child could swallow it
        // (mirrors the WPF PreviewKeyDown handler).
        AddHandler(KeyDownEvent, OnPreviewKeyDown, RoutingStrategies.Tunnel);

        PositionChanged += (_, _) => CaptureNormalBounds();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        // Keep the tracked "normal" geometry current on resize and on state transitions.
        if (change.Property == ClientSizeProperty || change.Property == WindowStateProperty)
        {
            CaptureNormalBounds();
        }
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);

        RestoreWindowState();

        if (this.FindControl<Button>("VersionLink") is { } versionLink && DataContext is MainWindowViewModel vm)
        {
            versionLink.Content = $"{AppInfo.DisplayName} v{vm.AppVersion}";
        }

        AttachTaskbarProgress();

        HandleCommandLineOpen();
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        SaveWindowState();
        DetachTaskbarProgress();
        base.OnClosing(e);

        if (DataContext is MainWindowViewModel vm)
        {
            vm.Cleanup();
        }
    }

    // ── Windows taskbar progress (ITaskbarList3) ─────────────────────

    private void AttachTaskbarProgress()
    {
        // Guard the whole feature off non-Windows platforms so the windows-only COM wrapper is never
        // referenced there (satisfies CA1416 platform-compatibility flow analysis).
        if (!OperatingSystem.IsWindows() || DataContext is not MainWindowViewModel vm)
        {
            return;
        }

        // TryCreate returns null on a headless window or if COM activation fails, so this wiring is
        // inert everywhere except a real Win32 desktop window.
        _taskbarProgress = WindowsTaskbarProgress.TryCreate(this);
        if (_taskbarProgress is null)
        {
            return;
        }

        _taskbarVm = vm;
        _taskbarProgress.Update(vm.TaskbarProgressState, vm.TaskbarProgressValue);
        vm.PropertyChanged += OnTaskbarPropertyChanged;
    }

    private void DetachTaskbarProgress()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        _taskbarVm?.PropertyChanged -= OnTaskbarPropertyChanged;
        _taskbarVm = null;

        _taskbarProgress?.Clear();
        _taskbarProgress = null;
    }

    private void OnTaskbarPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        // VM property changes already arrive on the UI thread (the VM marshals via its dispatcher), so
        // no extra thread-hop is needed before the COM call. This handler is only ever subscribed on
        // Windows (AttachTaskbarProgress bails otherwise); the OS guard makes that explicit for CA1416.
        if (OperatingSystem.IsWindows() && _taskbarProgress is not null && sender is MainWindowViewModel vm
            && (e.PropertyName == nameof(MainWindowViewModel.TaskbarProgressState)
                || e.PropertyName == nameof(MainWindowViewModel.TaskbarProgressValue)))
        {
            _taskbarProgress.Update(vm.TaskbarProgressState, vm.TaskbarProgressValue);
        }
    }

    // ── Scene-file helpers ───────────────────────────────────────────

    private static bool IsSceneFile(string path)
    {
        foreach (string ext in _sceneExtensions)
        {
            if (path.EndsWith(ext, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static string? FirstSceneFile(DragEventArgs e)
    {
        // Avalonia 11.3 replaced the obsolete IDataObject with IDataTransfer; TryGetFiles() returns
        // every dropped storage item (or null), matching the WPF handler that scanned all paths for
        // the first scene file.
        IStorageItem[]? files = e.DataTransfer.TryGetFiles();
        if (files is null)
        {
            return null;
        }

        foreach (IStorageItem item in files)
        {
            string? path = item.TryGetLocalPath();
            if (path is not null && IsSceneFile(path))
            {
                return path;
            }
        }

        return null;
    }

    // ── Drag & drop ──────────────────────────────────────────────────

    private void OnDragOver(object? sender, DragEventArgs e)
    {
        e.DragEffects = FirstSceneFile(e) is not null ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnDrop(object? sender, DragEventArgs e)
    {
        e.Handled = true;

        if (DataContext is MainWindowViewModel vm && FirstSceneFile(e) is { } path)
        {
            _ = vm.OpenSceneFileAsync(path);
        }
    }

    // ── Keyboard: Ctrl+1..7 tab switching (Advanced mode only) ────────

    private void OnPreviewKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.KeyModifiers == KeyModifiers.Control && e.Key >= Key.D1 && e.Key <= Key.D7
            && DataContext is MainWindowViewModel vm && vm.IsAdvancedMode)
        {
            vm.SelectedTabIndex = e.Key - Key.D1;
            e.Handled = true;
        }
    }

    // ── Command-line open ─────────────────────────────────────────────

    private void HandleCommandLineOpen()
    {
        string[] args = Environment.GetCommandLineArgs();
        if (args.Length > 1 && IsSceneFile(args[1]) && File.Exists(args[1])
            && DataContext is MainWindowViewModel vm)
        {
            _ = vm.OpenSceneFileAsync(args[1]);
        }
    }

    // ── Window-state persistence ──────────────────────────────────────

    private void RestoreWindowState()
    {
        _stateRestored = true;

        WindowStateModel? state = WindowStateService?.Load();
        if (state is null)
        {
            // First-launch defaults.
            Width = 1280;
            Height = 900;
            WindowState = WindowState.Maximized;
            return;
        }

        var rect = new PixelRect(
            (int)state.Left, (int)state.Top,
            (int)Math.Max(1, state.Width), (int)Math.Max(1, state.Height));

        if (IsRectOnAnyScreen(CurrentScreenBounds(), rect))
        {
            Position = new PixelPoint((int)state.Left, (int)state.Top);
        }

        Width = Math.Max(MinWidth, state.Width);
        Height = Math.Max(MinHeight, state.Height);
        WindowState = state.IsMaximized ? WindowState.Maximized : WindowState.Normal;

        _normalLeft = state.Left;
        _normalTop = state.Top;
        _normalWidth = Width;
        _normalHeight = Height;

        if (DataContext is MainWindowViewModel vm)
        {
            vm.SelectedTabIndex = Math.Clamp(state.SelectedTabIndex, 0, 7);
        }
    }

    private void SaveWindowState()
    {
        if (WindowStateService is null)
        {
            return;
        }

        bool maximized = WindowState == WindowState.Maximized;
        double width = double.IsNaN(Width) ? _normalWidth : Width;
        double height = double.IsNaN(Height) ? _normalHeight : Height;

        var state = new WindowStateModel
        {
            Left = maximized ? _normalLeft : Position.X,
            Top = maximized ? _normalTop : Position.Y,
            Width = maximized ? _normalWidth : width,
            Height = maximized ? _normalHeight : height,
            IsMaximized = maximized,
        };

        if (DataContext is MainWindowViewModel vm)
        {
            state.SelectedTabIndex = vm.SelectedTabIndex;
        }

        WindowStateService.Save(state);
    }

    // Internal so the deferred-capture race test can trigger a capture directly.
    internal void CaptureNormalBounds()
    {
        // Coalesce a burst of move/resize events (each fires this) into a single deferred commit.
        if (_capturePending)
        {
            return;
        }

        _capturePending = true;

        // Defer the actual capture to Background priority and re-check the window state when it runs.
        // The platform can deliver a size/position change slightly BEFORE WindowState flips to
        // Maximized; a synchronous capture would then record maximized geometry as the "normal"
        // bounds. By the time a Background-priority job runs, the state flip from the same input burst
        // has been processed, so a maximize-in-flight capture self-cancels in CommitNormalBounds.
        Dispatcher.UIThread.Post(CommitNormalBounds, DispatcherPriority.Background);
    }

    private void CommitNormalBounds()
    {
        _capturePending = false;

        if (_stateRestored && WindowState == WindowState.Normal && !double.IsNaN(Width) && Width > 0)
        {
            _normalLeft = Position.X;
            _normalTop = Position.Y;
            _normalWidth = Width;
            _normalHeight = Height;
        }
    }

    private IReadOnlyList<PixelRect> CurrentScreenBounds()
    {
        var bounds = new List<PixelRect>();
        Screens? screens = Screens;
        if (screens is not null)
        {
            foreach (Screen screen in screens.All)
            {
                bounds.Add(screen.Bounds);
            }
        }

        return bounds;
    }

    /// <summary>
    /// True when <paramref name="windowRect"/> overlaps any screen — the guard that keeps a restored
    /// window from being positioned off every display. Pure so it can be unit-tested without a live
    /// screen.
    /// </summary>
    internal static bool IsRectOnAnyScreen(IReadOnlyList<PixelRect> screens, PixelRect windowRect)
    {
        foreach (PixelRect screen in screens)
        {
            if (screen.Intersects(windowRect))
            {
                return true;
            }
        }

        return false;
    }

    // ── Menu handlers ─────────────────────────────────────────────────

    private void OnSettingsClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm)
        {
            return;
        }

        var settingsVm = new SettingsViewModel(vm.AppSettingsService, vm.FileDialog);
        _ = new SettingsWindow(settingsVm).ShowDialog(this);
    }

    private void OnAboutClick(object? sender, RoutedEventArgs e)
    {
        string version = DataContext is MainWindowViewModel vm ? vm.AppVersion : "?";
        _ = new AboutWindow(version).ShowDialog(this);
    }

    private void OnExitClick(object? sender, RoutedEventArgs e) => Close();

    private void OnVersionLinkClick(object? sender, RoutedEventArgs e)
        => new SystemLauncherService().OpenUrl("https://github.com/NeWbY100/ReScene.Manager");
}
