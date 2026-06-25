using ReScene.NET.Models;
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
    public void PathsNeedAttention_OutputWhitespace_IsTrue()
    {
        string verify = Path.Combine(TempDir, "verify.sfv");
        File.WriteAllText(verify, "");
        // Output that is only whitespace counts as unset (IsNullOrWhiteSpace).
        Assert.True(ReconstructorFieldGuidance.PathsNeedAttention(TempDir, TempDir, verify, "   "));
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

    [Fact]
    public void EvaluateWinRarPath_Empty_IsWarning()
    {
        Assert.Equal(FieldState.Warning, ReconstructorFieldGuidance.EvaluateWinRarPath("").State);
    }

    [Fact]
    public void EvaluateReleasePath_Empty_IsWarning()
    {
        Assert.Equal(FieldState.Warning, ReconstructorFieldGuidance.EvaluateReleasePath("").State);
    }

    [Fact]
    public void EvaluateVerificationPath_Empty_IsWarning()
    {
        Assert.Equal(FieldState.Warning, ReconstructorFieldGuidance.EvaluateVerificationPath("").State);
    }

    [Fact]
    public void EvaluateOutputPath_Empty_IsWarning()
    {
        Assert.Equal(FieldState.Warning, ReconstructorFieldGuidance.EvaluateOutputPath("").State);
    }

    [Fact]
    public void EvaluateOutputPath_Whitespace_IsWarning()
    {
        Assert.Equal(FieldState.Warning, ReconstructorFieldGuidance.EvaluateOutputPath("   ").State);
    }

    [Fact]
    public void EvaluateOutputPath_Set_IsOk()
    {
        Assert.Equal(FieldState.Ok, ReconstructorFieldGuidance.EvaluateOutputPath(TempDir).State);
    }
}
