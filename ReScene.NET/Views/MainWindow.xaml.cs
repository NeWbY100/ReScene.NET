using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
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
        SourceInitialized += (_, _) => DarkTitleBar.Enable(this);

        Drop += OnDrop;
        DragOver += OnDragOver;
    }

    protected override void OnContentRendered(EventArgs e)
    {
        base.OnContentRendered(e);
        RestoreWindowState();

        // Handle command-line arguments
        var args = Environment.GetCommandLineArgs();
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
            || path.EndsWith(".rar", StringComparison.OrdinalIgnoreCase);
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
            foreach (var file in files)
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

        foreach (var file in files)
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
            vm.SelectedTabIndex = state.SelectedTabIndex;
        }
    }

    private void SaveWindowState()
    {
        // Use RestoreBounds for position/size when maximized
        var bounds = WindowState == System.Windows.WindowState.Maximized
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

    private void OnAboutClick(object _, RoutedEventArgs e)
    {
        string version = DataContext is ViewModels.MainWindowViewModel vm ? vm.AppVersion : "?";

        var panel = new StackPanel { Margin = new Thickness(20) };

        panel.Children.Add(new TextBlock { Text = "ReScene.NET", FontSize = 18, FontWeight = FontWeights.Bold });
        panel.Children.Add(new TextBlock { Text = $"Version {version}", Foreground = System.Windows.Media.Brushes.Gray, Margin = new Thickness(0, 4, 0, 0) });
        panel.Children.Add(new TextBlock { Text = "Inspect, create, and reconstruct ReScene (SRR/SRS) files", Margin = new Thickness(0, 8, 0, 0) });

        // Links
        var linksPanel = new StackPanel { Margin = new Thickness(0, 12, 0, 0) };
        linksPanel.Children.Add(MakeHyperlink("ReScene.NET on GitHub", "https://github.com/NeWbY100/ReScene.NET"));
        linksPanel.Children.Add(MakeHyperlink("ReScene.Lib on GitHub", "https://github.com/NeWbY100/ReScene.Lib"));
        linksPanel.Children.Add(MakeHyperlink("ReScene Wiki", "https://rescene.wikidot.com"));
        panel.Children.Add(linksPanel);

        var dialog = new Window
        {
            Title = "About ReScene.NET",
            Width = 400,
            Height = 250,
            ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner = this,
            Content = panel
        };

        dialog.ShowDialog();
    }

    private static TextBlock MakeHyperlink(string text, string url)
    {
        var link = new System.Windows.Documents.Hyperlink(new System.Windows.Documents.Run(text))
        {
            NavigateUri = new Uri(url)
        };
        link.RequestNavigate += (_, e) =>
        {
            Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
            e.Handled = true;
        };
        return new TextBlock(link) { Margin = new Thickness(0, 2, 0, 0) };
    }

    private void OnPreviewKeyDown(object _, KeyEventArgs e)
    {
        // Ctrl+1 through Ctrl+7 switch tabs
        if (Keyboard.Modifiers == ModifierKeys.Control && e.Key >= Key.D1 && e.Key <= Key.D7)
        {
            if (DataContext is MainWindowViewModel vm)
            {
                vm.SelectedTabIndex = e.Key - Key.D1;
            }

            e.Handled = true;
        }
    }
}
