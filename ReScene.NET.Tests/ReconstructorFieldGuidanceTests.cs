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
        string release = Path.Combine(TempDir, "release");
        string output = Path.Combine(TempDir, "output");
        Directory.CreateDirectory(release);
        Directory.CreateDirectory(output);
        string verify = Path.Combine(TempDir, "verify.sfv");
        File.WriteAllText(verify, "");
        // WinRAR + Release = existing dirs, Verify = existing file, Output = separate non-empty dir.
        Assert.False(ReconstructorFieldGuidance.PathsNeedAttention(TempDir, release, verify, output));
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

    [Fact]
    public void PathsOverlap_SamePath_IsTrue()
    {
        Assert.True(ReconstructorFieldGuidance.PathsOverlap(TempDir, TempDir));
    }

    [Fact]
    public void PathsOverlap_OutputNestedInRelease_IsTrue()
    {
        string output = Path.Combine(TempDir, "output");
        Assert.True(ReconstructorFieldGuidance.PathsOverlap(TempDir, output));
    }

    [Fact]
    public void PathsOverlap_ReleaseNestedInOutput_IsTrue()
    {
        string release = Path.Combine(TempDir, "release");
        Assert.True(ReconstructorFieldGuidance.PathsOverlap(release, TempDir));
    }

    [Fact]
    public void PathsOverlap_Siblings_IsFalse()
    {
        string a = Path.Combine(TempDir, "release");
        string b = Path.Combine(TempDir, "output");
        Assert.False(ReconstructorFieldGuidance.PathsOverlap(a, b));
    }

    [Fact]
    public void PathsOverlap_SimilarPrefixButNotNested_IsFalse()
    {
        // "rel" must not be considered nested in "release".
        string a = Path.Combine(TempDir, "rel");
        string b = Path.Combine(TempDir, "release");
        Assert.False(ReconstructorFieldGuidance.PathsOverlap(a, b));
    }

    [Fact]
    public void PathsOverlap_EmptyPathA_IsFalse()
    {
        Assert.False(ReconstructorFieldGuidance.PathsOverlap("", TempDir));
    }

    [Fact]
    public void PathsOverlap_EmptyPathB_IsFalse()
    {
        Assert.False(ReconstructorFieldGuidance.PathsOverlap(TempDir, ""));
    }

    [Fact]
    public void PathsOverlap_DiffersOnlyByCase_IsTrue()
    {
        Assert.True(ReconstructorFieldGuidance.PathsOverlap(TempDir.ToUpperInvariant(), TempDir.ToLowerInvariant()));
    }

    [Fact]
    public void EvaluateReleasePath_OverlapsOutput_IsError()
    {
        FieldStatus s = ReconstructorFieldGuidance.EvaluateReleasePath(TempDir, TempDir);
        Assert.Equal(FieldState.Error, s.State);
        Assert.Contains("different folders", s.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void EvaluateOutputPath_OverlapsRelease_IsError()
    {
        FieldStatus s = ReconstructorFieldGuidance.EvaluateOutputPath(TempDir, TempDir);
        Assert.Equal(FieldState.Error, s.State);
        Assert.Contains("different folders", s.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void EvaluateReleasePath_NoOverlap_FallsThroughToSinglePath()
    {
        string release = Path.Combine(TempDir, "release");
        string output = Path.Combine(TempDir, "output");
        Directory.CreateDirectory(release);
        FieldStatus s = ReconstructorFieldGuidance.EvaluateReleasePath(release, output);
        Assert.Equal(FieldState.Ok, s.State); // existing release dir -> "Source files selected."
    }

    [Fact]
    public void EvaluateOutputPath_NoOverlap_FallsThroughToSinglePath()
    {
        string release = Path.Combine(TempDir, "release");
        string output = Path.Combine(TempDir, "output");
        FieldStatus s = ReconstructorFieldGuidance.EvaluateOutputPath(output, release);
        Assert.Equal(FieldState.Ok, s.State); // non-empty output -> "Output folder set."
    }

    [Fact]
    public void EvaluateReleasePath_EmptyOutput_NoFalseOverlap()
    {
        // Output empty -> not an overlap; release falls through to its single-path result.
        FieldStatus s = ReconstructorFieldGuidance.EvaluateReleasePath(TempDir, "");
        Assert.Equal(FieldState.Ok, s.State);
    }

    [Fact]
    public void PathsNeedAttention_Overlap_IsTrue()
    {
        string verify = Path.Combine(TempDir, "verify.sfv");
        File.WriteAllText(verify, "");
        // WinRAR/Release/Verify/Output all otherwise valid, but Release == Output.
        Assert.True(ReconstructorFieldGuidance.PathsNeedAttention(TempDir, TempDir, verify, TempDir));
    }
}
