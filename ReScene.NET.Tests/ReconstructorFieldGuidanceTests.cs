using ReScene.NET.ViewModels.Reconstruction;

namespace ReScene.NET.Tests;

public class ReconstructorFieldGuidanceTests : TempDirTestBase
{
    [Fact]
    public void PathsNeedAttention_AllEmpty_IsTrue()
    {
        Assert.True(ReconstructorFieldGuidance.PathsNeedAttention("", "", "", ""));
    }

    [Fact]
    public void PathsNeedAttention_OutputEmpty_IsTrue()
    {
        string verify = Path.Combine(TempDir, "verify.sfv");
        File.WriteAllText(verify, "");
        // WinRAR/Release/Verify all valid, Output empty → still needs attention.
        Assert.True(ReconstructorFieldGuidance.PathsNeedAttention(TempDir, TempDir, verify, ""));
    }

    [Fact]
    public void PathsNeedAttention_NonexistentWinRar_IsTrue()
    {
        string verify = Path.Combine(TempDir, "verify.sfv");
        File.WriteAllText(verify, "");
        string missing = Path.Combine(TempDir, "does-not-exist");
        Assert.True(ReconstructorFieldGuidance.PathsNeedAttention(missing, TempDir, verify, TempDir));
    }

    [Fact]
    public void PathsNeedAttention_AllValid_IsFalse()
    {
        string verify = Path.Combine(TempDir, "verify.sfv");
        File.WriteAllText(verify, "");
        // WinRAR + Release = existing dirs, Verify = existing file, Output = non-empty.
        Assert.False(ReconstructorFieldGuidance.PathsNeedAttention(TempDir, TempDir, verify, TempDir));
    }
}
