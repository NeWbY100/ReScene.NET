using System.Windows;
using ReScene.NET.Services;
using ReScene.NET.ViewModels;
using ReScene.NET.Views;

namespace ReScene.NET;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var tempDir = new TempDirectoryService();
        var windowState = new WindowStateService();
        var appSettings = new AppSettingsService();
        MainWindow = new MainWindow
        {
            WindowStateService = windowState,
            Opacity = 0,
            DataContext = new MainWindowViewModel(
                new SRRCreationService(), new SRSCreationService(), new SRSReconstructionService(),
                new SampleRestorerService(tempDir), new BruteForceService(), new FileCompareService(), new FileDialogService(), new RecentFilesService(appSettings), tempDir, new SRREditingService(), new SRRVerifyService(), new PropertyExportService(), appSettings)
        };
        MainWindow.Show();
    }
}
