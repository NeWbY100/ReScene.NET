using ReScene.App.Core.Models;
using ReScene.App.Core.Services;

namespace ReScene.App.Core.Tests;

/// <summary>
/// Tests that <see cref="WindowStateService"/> resolves its file path per-instance from the current
/// <see cref="AppDataConfig.FolderName"/> (F8). The path used to be a <c>static readonly</c> field
/// frozen at type init, so a later folder switch (the per-head folder, or cross-test isolation) was
/// ignored — the same anti-pattern <see cref="AppSettingsService"/> was already converted away from.
/// </summary>
public class WindowStateServiceTests
{
    [Fact]
    public void FilePath_IsResolvedPerInstance_FromCurrentFolderName()
    {
        string original = AppDataConfig.FolderName;
        string folderA = $"ReScene.WindowStateTests-{Guid.NewGuid():N}";
        string folderB = $"ReScene.WindowStateTests-{Guid.NewGuid():N}";
        try
        {
            AppDataConfig.FolderName = folderA;
            var underA = new WindowStateService();
            underA.Save(new WindowStateModel { Left = 10, Top = 20, Width = 800, Height = 600, IsMaximized = false });

            // A second instance created under a DIFFERENT folder must not see A's file. With a static
            // frozen path this would have wrongly loaded A's state (or shared it).
            AppDataConfig.FolderName = folderB;
            Assert.Null(new WindowStateService().Load());

            // Back under A: a fresh instance picks up the folder at construction and sees the state.
            AppDataConfig.FolderName = folderA;
            WindowStateModel? loaded = new WindowStateService().Load();
            Assert.NotNull(loaded);
            Assert.Equal(800, loaded.Width);
            Assert.Equal(600, loaded.Height);
        }
        finally
        {
            AppDataConfig.FolderName = original;
            DeleteAppDataFolder(folderA);
            DeleteAppDataFolder(folderB);
        }
    }

    private static void DeleteAppDataFolder(string folderName)
    {
        string dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), folderName);
        if (Directory.Exists(dir))
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
