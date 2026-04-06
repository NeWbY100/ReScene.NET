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
        MainWindow = new MainWindow
        {
            DataContext = new MainWindowViewModel(
                new SrrCreationService(), new SrsCreationService(), new SrsReconstructionService(),
                new SampleRestorerService(tempDir), new BruteForceService(), new FileCompareService(), new FileDialogService(), new RecentFilesService(), tempDir)
        };
        MainWindow.Show();
    }
}
