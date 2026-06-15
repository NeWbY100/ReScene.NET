using System.Diagnostics;
using System.Windows;
using System.Windows.Threading;
using ReScene.NET.Services;
using ReScene.NET.ViewModels;
using ReScene.NET.Views;

namespace ReScene.NET;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Surface otherwise-silent failures: UI-thread exceptions (e.g. in the dispatcher
        // progress callbacks), faulted tasks nobody awaited, and fatal background-thread
        // exceptions. Without these, such failures either crash with no message or vanish.
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
        AppDomain.CurrentDomain.UnhandledException += OnAppDomainUnhandledException;

        var tempDir = new TempDirectoryService();
        var windowState = new WindowStateService();
        var appSettings = new AppSettingsService();
        MainWindow = new MainWindow
        {
            WindowStateService = windowState,
            Opacity = 0,
            DataContext = new MainWindowViewModel(
                new SRRCreationService(), new SRSCreationService(), new SRSReconstructionService(),
                new SampleRestorerService(tempDir), new BruteForceService(), new FileCompareService(appSettings), new FileDialogService(), new RecentFilesService(appSettings), tempDir, new SRREditingService(), new SRRVerifyService(), new PropertyExportService(), appSettings, new HexDiffComputer(), new WpfDispatcher())
        };
        MainWindow.Show();
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Trace.TraceError($"Unhandled UI exception: {e.Exception}");
        MessageBox.Show(
            $"An unexpected error occurred:\n\n{e.Exception.Message}\n\nThe application will try to continue.",
            "Unexpected error",
            MessageBoxButton.OK,
            MessageBoxImage.Error);

        // Keep the app alive: these usually originate in non-critical UI callbacks, and a
        // surfaced-then-continued error beats a silent crash.
        e.Handled = true;
    }

    private static void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        Trace.TraceError($"Unobserved task exception: {e.Exception}");

        // Prevent the process from terminating on a faulted task that was never awaited.
        e.SetObserved();
    }

    private static void OnAppDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
        => Trace.TraceError($"Fatal unhandled exception (terminating={e.IsTerminating}): {e.ExceptionObject}");
}
