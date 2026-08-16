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
public class MetadataOutputPathTests : TempDirTestBase
{
    // A REAL directory, so the link-resolution branch actually runs. With a fictional root the
    // resolver has nothing to resolve and that whole check is skipped, which would leave it
    // untested.
    private string Root => TempDir;

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

    [Theory]
    [InlineData("Movie: Part 1.mkv")]
    [InlineData("Artist - Album: Deluxe.flac")]
    [InlineData("12:34 timestamp.mkv")]
    public void TryResolve_ColonInAnOrdinaryName_IsHandledPerPlatform(string name)
    {
        // ':' means different things on the two platforms, so the correct verdict differs and the
        // test has to say which it expects rather than assert one everywhere.
        bool ok = MetadataOutputPath.TryResolve(Root, name, out string full, out string error);

        if (OperatingSystem.IsWindows())
        {
            // Any colon past the drive-letter position opens an alternate data stream, so this
            // name cannot become the ordinary file the user expects to see.
            Assert.False(ok);
            Assert.Contains("alternate data stream", error, StringComparison.Ordinal);
        }
        else
        {
            // On POSIX ':' is an ordinary character. Refusing it — which an earlier version of
            // this guard did, to catch "C:\..." on Linux — rejected legitimate sample names.
            Assert.True(ok, error);
            Assert.StartsWith(Path.GetFullPath(Root) + Path.DirectorySeparatorChar, full, StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData("NUL")]
    [InlineData("CON.mkv")]
    [InlineData("aux.srs")]
    [InlineData("COM1.txt")]
    [InlineData("Sample/LPT1.mkv")]
    public void TryResolve_ReservedWindowsDeviceName_IsRefused(string name)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;   // DOS device names are reserved only on Windows.
        }


        // These are lexically inside the chosen directory, so the containment checks cannot see
        // them, but Windows resolves them to devices rather than files in EVERY directory — a
        // bulk restore would silently discard output or fail instead of producing a sample.
        Assert.False(MetadataOutputPath.TryResolve(Root, name, out _, out string error));
        Assert.Contains("reserved Windows device name", error, StringComparison.Ordinal);
    }

    [Fact]
    public void TryResolve_AlternateDataStreamSuffix_IsRefusedOnWindows()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;   // Alternate data streams are a Windows concept.
        }


        Assert.False(MetadataOutputPath.TryResolve(Root, "movie.mkv:restored", out _, out string error));
        Assert.Contains("alternate data stream", error, StringComparison.Ordinal);
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
