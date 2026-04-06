namespace ReScene.NET.Services;

/// <summary>
/// Creates and cleans up temporary directories under the application's temp folder.
/// </summary>
public class TempDirectoryService : ITempDirectoryService
{
    public string CreateTempDirectory()
    {
        string dir = Path.Combine(Path.GetTempPath(), "ReScene.NET", Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        return dir;
    }

    public void Cleanup(string? tempDir)
    {
        if (tempDir is null)
        {
            return;
        }

        try
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, true);
            }
        }
        catch
        {
            // Best-effort cleanup
        }
    }
}
