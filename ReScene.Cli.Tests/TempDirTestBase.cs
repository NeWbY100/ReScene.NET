namespace ReScene.Cli.Tests;

/// <summary>
/// Base class giving each test class its own unique temp directory, best-effort deleted on
/// dispose — the same pattern as the identically named helpers in ReScene.Tests and
/// ReScene.App.Core.Tests.
/// </summary>
public abstract class TempDirTestBase : IDisposable
{
    protected string TempDir { get; } =
        Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "ReScene.Cli.Tests-" + Guid.NewGuid().ToString("N"))).FullName;

    public void Dispose()
    {
        Dispose(disposing: true);
        GC.SuppressFinalize(this);
    }

    protected virtual void Dispose(bool disposing)
    {
        if (!disposing)
        {
            return;
        }

        try
        {
            Directory.Delete(TempDir, recursive: true);
        }
        catch (IOException)
        {
            // Best-effort cleanup; a straggling handle must not fail the test run.
        }
        catch (UnauthorizedAccessException)
        {
            // Same.
        }
    }
}
