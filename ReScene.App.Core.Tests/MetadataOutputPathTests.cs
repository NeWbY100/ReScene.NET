using ReScene.App.Core.Services;

namespace ReScene.App.Core.Tests;

/// <summary>
/// Pins the containment contract for file names read out of SRR/SRS metadata and used as write
/// destinations.
/// </summary>
/// <remarks>
/// Regression tests for two real sites — the bulk sample restore and the SRS reconstructor's
/// auto-filled output path — which both did <c>Path.Combine(chosenDirectory, metadataName)</c>.
/// That is not a containment primitive: an absolute name replaces the directory outright, and
/// <c>..</c> segments climb out of it.
/// </remarks>
public class MetadataOutputPathTests
{
    private static readonly string Root = OperatingSystem.IsWindows()
        ? @"C:\chosen\output"
        : "/chosen/output";

    [Theory]
    [InlineData("../escape.mkv")]
    [InlineData("..\\escape.mkv")]
    [InlineData("../../escape.mkv")]
    [InlineData("sub/../../escape.mkv")]
    [InlineData("sub\\..\\..\\escape.mkv")]
    public void TryResolve_TraversalSegments_AreRefused(string name)
    {
        bool ok = MetadataOutputPath.TryResolve(Root, name, out string full, out string error);

        Assert.False(ok);
        Assert.Equal(string.Empty, full);
        Assert.NotEmpty(error);
    }

    [Theory]
    [InlineData(@"C:\Windows\System32\evil.dll")]
    [InlineData("/etc/cron.d/evil")]
    [InlineData(@"\\server\share\evil.mkv")]
    [InlineData(@"D:relative-to-drive.mkv")]
    public void TryResolve_AbsoluteOrDriveQualifiedNames_AreRefused(string name)
    {
        // Path.Combine returns an absolute second argument VERBATIM, discarding the chosen
        // directory. These must be refused on every platform, not only the one whose path syntax
        // they use.
        bool ok = MetadataOutputPath.TryResolve(Root, name, out string full, out string error);

        Assert.False(ok);
        Assert.Equal(string.Empty, full);
        Assert.NotEmpty(error);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void TryResolve_EmptyName_IsRefused(string name)
    {
        Assert.False(MetadataOutputPath.TryResolve(Root, name, out _, out string error));
        Assert.NotEmpty(error);
    }

    [Fact]
    public void TryResolve_EmptyOutputDirectory_IsRefused()
    {
        Assert.False(MetadataOutputPath.TryResolve("", "sample.mkv", out _, out string error));
        Assert.NotEmpty(error);
    }

    [Theory]
    [InlineData("sample.mkv")]
    [InlineData("Sample/clip.mkv")]
    [InlineData("Sample\\clip.mkv")]
    [InlineData("./sample.mkv")]
    public void TryResolve_OrdinaryNames_ResolveInsideTheChosenDirectory(string name)
    {
        bool ok = MetadataOutputPath.TryResolve(Root, name, out string full, out string error);

        Assert.True(ok, error);
        string expectedRoot = Path.GetFullPath(Root) + Path.DirectorySeparatorChar;
        Assert.StartsWith(expectedRoot, full, StringComparison.Ordinal);
    }

    [Fact]
    public void TryResolve_SubdirectoryName_KeepsItsRelativeStructure()
    {
        Assert.True(MetadataOutputPath.TryResolve(Root, "Sample/clip.mkv", out string full, out _));

        Assert.Equal(
            Path.GetFullPath(Path.Combine(Root, "Sample", "clip.mkv")),
            full);
    }
}
