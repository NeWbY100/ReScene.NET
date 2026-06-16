namespace ReScene.NET.Tests;

/// <summary>
/// Base class for test classes that need a unique, per-test-class temporary
/// directory. The directory is created in the constructor and recursively
/// deleted (best-effort) in <see cref="Dispose"/>. Subclasses access it through
/// <see cref="TempDir"/> and may create sub-directories beneath it.
/// </summary>
public abstract class TempDirTestBase : IDisposable
{
    /// <summary>Absolute path to this test class's unique temporary directory.</summary>
    protected string TempDir { get; }

    protected TempDirTestBase()
    {
        TempDir = Path.Combine(Path.GetTempPath(), $"rescene_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(TempDir);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(TempDir, true);
        }
        catch
        {
            // Best-effort cleanup.
        }

        GC.SuppressFinalize(this);
    }
}
