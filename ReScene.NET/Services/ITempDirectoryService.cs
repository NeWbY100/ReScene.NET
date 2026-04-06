namespace ReScene.NET.Services;

/// <summary>
/// Manages temporary directory creation and cleanup for the application.
/// </summary>
public interface ITempDirectoryService
{
    /// <summary>
    /// Creates a new temporary directory under the application's temp folder.
    /// </summary>
    /// <returns>
    /// The full path to the created directory.
    /// </returns>
    public string CreateTempDirectory();

    /// <summary>
    /// Deletes a temporary directory and all its contents. Best-effort — exceptions are suppressed.
    /// </summary>
    public void Cleanup(string? tempDir);
}
