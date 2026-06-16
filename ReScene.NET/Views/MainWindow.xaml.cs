using System.Diagnostics;
using System.Windows;
using System.Windows.Input;
using System.Windows.Navigation;
using ReScene.NET.Helpers;
using ReScene.NET.Models;
using ReScene.NET.Services;
using ReScene.NET.ViewModels;

namespace ReScene.NET.Views;

public partial class MainWindow : Window
{
    public IWindowStateService? WindowStateService { get; set; }

    public MainWindow()
    {
        InitializeComponent();
        SourceInitialized += OnSourceInitialized;

        Drop += OnDrop;
        DragOver += OnDragOver;
    }

    private void OnSourceInitialized(object? _, EventArgs e)
    {
        DarkTitleBar.Enable(this);
        RestoreWindowState();
    }

    protected override void OnContentRendered(EventArgs e)
    {
        base.OnContentRendered(e);
        Opacity = 1;

        // Handle command-line arguments
        string[] args = Environment.GetCommandLineArgs();
        if (args.Length > 1 && IsSceneFile(args[1]) && File.Exists(args[1]))
        {
            if (DataContext is MainWindowViewModel vm)
            {
                vm.OpenSceneFile(args[1]);
            }
        }
    }

    private static bool IsSceneFile(string path)
    {
        return path.EndsWith(".srr", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(".srs", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(".rar", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(".mkv", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(".webm", StringComparison.OrdinalIgnoreCase);
    }

    private void OnDragOver(object _, DragEventArgs e)
    {
        if (e.Handled)
        {
            return;
        }

        e.Effects = DragDropEffects.None;

        if (e.Data.GetDataPresent(DataFormats.FileDrop)
            && e.Data.GetData(DataFormats.FileDrop) is string[] files)
        {
            foreach (string file in files)
            {
                if (IsSceneFile(file))
                {
                    e.Effects = DragDropEffects.Copy;
                    break;
                }
            }
        }

        e.Handled = true;
    }

    private void OnDrop(object _, DragEventArgs e)
    {
        if (e.Handled)
        {
            return;
        }

        if (!e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            return;
        }

        if (e.Data.GetData(DataFormats.FileDrop) is not string[] files)
        {
            return;
        }

        foreach (string file in files)
        {
            if (IsSceneFile(file))
            {
                if (DataContext is MainWindowViewModel vm)
                {
                    vm.OpenSceneFile(file);
                }

                break;
            }
        }
    }

    private void OnHyperlinkRequestNavigate(object _, RequestNavigateEventArgs e)
    {
        Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
        e.Handled = true;
    }

    private void OnExitClick(object _, RoutedEventArgs e) => Close();

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        SaveWindowState();
        base.OnClosing(e);

        if (DataContext is MainWindowViewModel vm)
        {
            vm.Cleanup();
        }
    }

    #region Window State Persistence

    private void RestoreWindowState()
    {
        WindowStateModel? state = WindowStateService?.Load();
        if (state is null)
        {
            // First launch defaults
            Width = 1280;
            Height = 900;
            WindowState = System.Windows.WindowState.Maximized;
            return;
        }

        // Validate position is on-screen
        if (state.Left >= SystemParameters.VirtualScreenLeft
            && state.Top >= SystemParameters.VirtualScreenTop
            && state.Left + state.Width <= SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth
            && state.Top + state.Height <= SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight)
        {
            Left = state.Left;
            Top = state.Top;
        }

        Width = Math.Max(MinWidth, state.Width);
        Height = Math.Max(MinHeight, state.Height);
        WindowState = state.IsMaximized ? System.Windows.WindowState.Maximized : System.Windows.WindowState.Normal;

        if (DataContext is MainWindowViewModel vm)
        {
            vm.SelectedTabIndex = Math.Clamp(state.SelectedTabIndex, 0, 7);
        }
    }

    private void SaveWindowState()
    {
        // Use RestoreBounds for position/size when maximized
        Rect bounds = WindowState == System.Windows.WindowState.Maximized
            ? RestoreBounds
            : new Rect(Left, Top, Width, Height);

        var state = new WindowStateModel
        {
            Left = bounds.Left,
            Top = bounds.Top,
            Width = bounds.Width,
            Height = bounds.Height,
            IsMaximized = WindowState == System.Windows.WindowState.Maximized
        };

        if (DataContext is MainWindowViewModel vm)
        {
            state.SelectedTabIndex = vm.SelectedTabIndex;
        }

        WindowStateService?.Save(state);
    }

    #endregion

    private void OnSettingsClick(object _, RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm)
        {
            return;
        }

        var settingsVm = new ViewModels.SettingsViewModel(vm.AppSettingsService, vm.FileDialog);
        var window = new Views.SettingsWindow
        {
            Owner = this,
            DataContext = settingsVm
        };
        window.ShowDialog();
    }

    private void OnAboutClick(object _, RoutedEventArgs e)
    {
        string version = DataContext is MainWindowViewModel vm ? vm.AppVersion : "?";

        var window = new Views.AboutWindow(version)
        {
            Owner = this
        };
        window.ShowDialog();
    }

    private void OnPreviewKeyDown(object _, KeyEventArgs e)
    {
        // Ctrl+1 through Ctrl+7 switch tabs (Advanced mode only)
        if (Keyboard.Modifiers == ModifierKeys.Control && e.Key >= Key.D1 && e.Key <= Key.D7)
        {
            if (DataContext is MainWindowViewModel vm && vm.IsAdvancedMode)
            {
                vm.SelectedTabIndex = e.Key - Key.D1;
                e.Handled = true;
            }
        }
    }
}
