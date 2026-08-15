using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using ReScene.App.Core.Models;
using ReScene.App.Core.Services;
using ReScene.App.Core.ViewModels;
using ReScene.Manager.Services;
using ReScene.Manager.Views;

namespace ReScene.Manager.Tests;

/// <summary>
/// Headless render/behavior tests for the ported application shell
/// (<see cref="MainWindow"/> + <see cref="AdvancedShellView"/> + <see cref="HomeView"/>). The
/// central gate is <b>zero binding errors</b> (via <see cref="BindingErrorSink"/>) when the shell
/// renders against a real <see cref="MainWindowViewModel"/>, plus: the menu bar, the 8-tab
/// <see cref="TabControl"/> and the live <see cref="HomeView"/> are present; the VM's
/// <c>SelectedTabIndex</c> drives the tab strip; and toggling the mode swaps the advanced/beginner
/// hosts. Live menu interaction (opening the real Settings/About dialogs) is the controller's
/// Phase-4 launch-smoke, not exercised here.
/// </summary>
/// <remarks>
/// Shares the "AppDataConfig" collection with the other classes that mutate
/// <see cref="AppDataConfig.FolderName"/> / <c>AppInfo.DisplayName</c>, so none run
/// concurrently. Each test points <see cref="AppDataConfig.FolderName"/> at a unique temp folder so
/// the real settings/recent-files services never touch the machine's <c>%LOCALAPPDATA%</c>.
/// </remarks>
[Collection("AppDataConfig")]
public class MainWindowShellTests
{
    /// <summary>
    /// A fixed window-state seam so the shell restores into a concrete Normal-mode size. Headless
    /// windows given the default Maximized state get no real client size, so the nested
    /// UserControl content presenters never run a measure pass and the TabControl/HomeView never
    /// enter the visual tree. Restoring a Normal size guarantees a layout pass (and exercises the
    /// non-null restore path).
    /// </summary>
    private sealed class FixedWindowState(WindowStateModel model) : IWindowStateService
    {
        public WindowStateModel? Load() => model;

        public void Save(WindowStateModel state)
        {
        }
    }

    private static MainWindow CreateShell(MainWindowViewModel vm) =>
        new()
        {
            DataContext = vm,
            WindowStateService = new FixedWindowState(new WindowStateModel
            {
                Left = 50,
                Top = 50,
                Width = 1100,
                Height = 720,
                IsMaximized = false,
                SelectedTabIndex = 0,
            }),
        };

    private static MainWindowViewModel CreateViewModel()
    {
        var tempDir = new TempDirectoryService();
        var appSettings = new AppSettingsService();
        var fileDialog = new AvaloniaFileDialogService(static () => null);
        var imageLoader = new AvaloniaImageLoader();

        return new MainWindowViewModel(
            new SRRCreationService(), new SRSCreationService(), new SRSReconstructionService(),
            new SampleRestorerService(tempDir), new BruteForceService(), new FileCompareService(appSettings),
            fileDialog, new RecentFilesService(appSettings), tempDir, new SRREditingService(),
            new SRRVerifyService(), new PropertyExportService(), appSettings, new HexDiffComputer(),
            new AvaloniaUiTimerFactory(),
            new AvaloniaFilePreviewService(imageLoader, static () => null),
            new AvaloniaImagePreviewService(imageLoader, fileDialog, static () => null),
            new AvaloniaUiDispatcher());
    }

    private static T Using<T>(Func<T> body)
    {
        string originalFolder = AppDataConfig.FolderName;
        AppDataConfig.FolderName = $"ReScene.Manager.Tests-{Guid.NewGuid():N}";
        try
        {
            return body();
        }
        finally
        {
            AppDataConfig.FolderName = originalFolder;
        }
    }

    [AvaloniaFact]
    public void Shell_RendersMenuTabsAndHome_NoBindingErrors()
    {
        _ = Using<object?>(() =>
        {
            MainWindowViewModel vm = CreateViewModel();
            // A fresh install resolves to Beginner mode; switch to Advanced so the tab shell renders.
            vm.SetAdvancedModeCommand.Execute(null);

            using var sink = new BindingErrorSink();
            MainWindow window = CreateShell(vm);
            window.Show();
            Dispatcher.UIThread.RunJobs();

            // Menu bar with the three top-level menus.
            Menu menu = window.GetVisualDescendants().OfType<Menu>().Single();
            List<string?> topLevel = [.. menu.GetVisualDescendants().OfType<MenuItem>().Select(m => m.Header as string)];
            Assert.Contains("_File", topLevel);
            Assert.Contains("_Mode", topLevel);
            Assert.Contains("_Help", topLevel);

            // The 8-tab advanced shell (advanced is the default mode).
            TabControl tabs = window.GetVisualDescendants().OfType<TabControl>().Single();
            Assert.Equal(8, window.GetVisualDescendants().OfType<TabItem>().Count());

            // Home tab is selected by default and hosts the real HomeView.
            Assert.True(vm.IsAdvancedMode);
            Assert.Equal(0, tabs.SelectedIndex);
            Assert.Single(window.GetVisualDescendants().OfType<HomeView>());

            Assert.Empty(sink.Messages);

            return null;
        });
    }

    [AvaloniaFact]
    public void SelectedTabIndex_DrivesTheTabControl()
    {
        _ = Using<object?>(() =>
        {
            MainWindowViewModel vm = CreateViewModel();
            vm.SetAdvancedModeCommand.Execute(null);

            MainWindow window = CreateShell(vm);
            window.Show();
            Dispatcher.UIThread.RunJobs();

            TabControl tabs = window.GetVisualDescendants().OfType<TabControl>().Single();
            Assert.Equal(0, tabs.SelectedIndex);

            vm.SelectedTabIndex = 4;
            Dispatcher.UIThread.RunJobs();

            Assert.Equal(4, tabs.SelectedIndex);

            return null;
        });
    }

    [AvaloniaFact]
    public void ModeToggle_SwapsAdvancedAndBeginnerHosts()
    {
        _ = Using<object?>(() =>
        {
            MainWindowViewModel vm = CreateViewModel();
            vm.SetAdvancedModeCommand.Execute(null);

            MainWindow window = CreateShell(vm);
            window.Show();
            Dispatcher.UIThread.RunJobs();

            AdvancedShellView advanced = window.GetVisualDescendants().OfType<AdvancedShellView>().Single();
            Border beginner = window.GetVisualDescendants().OfType<Border>().Single(b => b.Name == "BeginnerHost");

            // Advanced mode: advanced shell visible, beginner host hidden.
            Assert.True(advanced.IsVisible);
            Assert.False(beginner.IsVisible);

            vm.SetBeginnerModeCommand.Execute(null);
            Dispatcher.UIThread.RunJobs();

            // Beginner mode: hosts swap.
            Assert.False(advanced.IsVisible);
            Assert.True(beginner.IsVisible);

            return null;
        });
    }
}
