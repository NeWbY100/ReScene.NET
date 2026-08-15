using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using ReScene.App.Core;
using ReScene.App.Core.Services;
using ReScene.App.Core.ViewModels;
using ReScene.Manager.Helpers;
using ReScene.Manager.Services;
using ReScene.Manager.Views;

namespace ReScene.Manager;

public partial class App : Application
{
    private HighContrastThemeService? _highContrast;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // Re-establish the WPF app's three last-chance exception handlers. AppDomain and
            // TaskScheduler surface otherwise-silent background failures here; the UI-thread
            // (Dispatcher) equivalent of WPF's DispatcherUnhandledException is wired below, once the
            // error dialog it shows exists.
            AppDomain.CurrentDomain.UnhandledException += (_, e) =>
                Trace.TraceError($"Fatal unhandled exception (terminating={e.IsTerminating}): {e.ExceptionObject}");
            TaskScheduler.UnobservedTaskException += (_, e) =>
            {
                Trace.TraceError($"Unobserved task exception: {e.Exception}");
                e.SetObserved();
            };

            // Settings folder + display name — both now equal the App.Core defaults (the WPF
            // head that needed different values is deleted); kept as explicit belt so a future
            // second head can't silently inherit this head's identity.
            AppDataConfig.FolderName = "ReScene.Manager";
            AppInfo.DisplayName = "ReScene Manager";

            // Follow the OS contrast preference for the life of the process. Started before the
            // window is built so a machine already in high contrast never shows a normal-theme
            // frame first, and kept in a field so the ColorValuesChanged subscription outlives
            // this method.
            _highContrast = new HighContrastThemeService(this, PlatformSettings);
            _highContrast.Start();

            var window = new MainWindow
            {
                // Mirrors the WPF app: the window persists its position/size/tab across runs.
                WindowStateService = new WindowStateService(),
            };
            // Resolve the currently-active window first so a sync dialog (e.g. a wizard's confirm)
            // is owned by the active pop-up (the WizardWindow when open), not always MainWindow —
            // otherwise it renders behind the modal wizard. Falls back to MainWindow, then the
            // freshly-built window. Resolved lazily when a dialog is requested.
            Window Owner() =>
                desktop.Windows.FirstOrDefault(w => w.IsActive)
                ?? desktop.MainWindow
                ?? window;

            var tempDir = new TempDirectoryService();
            var appSettings = new AppSettingsService();
            var fileDialog = new AvaloniaFileDialogService(Owner);
            var imageLoader = new AvaloniaImageLoader();

            // Third WPF handler equivalent (DispatcherUnhandledException): log a UI-thread exception,
            // show a non-fatal error dialog via the existing sync-modal path, and mark it handled so
            // the app keeps running. Wired here because it needs the fileDialog built above.
            var uiExceptionHandler = new UiThreadExceptionHandler(fileDialog.ShowError);
            Dispatcher.UIThread.UnhandledException += (_, e) => e.Handled = uiExceptionHandler.Handle(e.Exception);

            window.DataContext = new MainWindowViewModel(
                new SRRCreationService(), new SRSCreationService(), new SRSReconstructionService(),
                new SampleRestorerService(tempDir), new BruteForceService(), new FileCompareService(appSettings),
                fileDialog, new RecentFilesService(appSettings), tempDir, new SRREditingService(),
                new SRRVerifyService(), new PropertyExportService(), appSettings, new HexDiffComputer(),
                new AvaloniaUiTimerFactory(),
                new AvaloniaFilePreviewService(imageLoader, Owner),
                new AvaloniaImagePreviewService(imageLoader, fileDialog, Owner),
                new AvaloniaUiDispatcher());

            desktop.MainWindow = window;
        }
        base.OnFrameworkInitializationCompleted();
    }
}
