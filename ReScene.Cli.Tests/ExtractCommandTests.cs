namespace ReScene.Cli.Tests;

/// <summary>
/// Tests for the <c>extract</c> verb: relative directory structure is preserved, and an SRR
/// carrying a hostile stored name (rooted or with ".." segments) is refused outright with
/// exit code 2 and nothing written — not silently sanitized into a different layout.
/// </summary>
public class ExtractCommandTests : TempDirTestBase
{
    private static readonly byte[] NfoBytes = [0x4E, 0x46, 0x4F, 0x21];
    private static readonly byte[] SrsBytes = [0x53, 0x52, 0x53, 0x21, 0x00, 0x01];

    [Fact]
    public async Task Extract_PreservesRelativeDirectoryStructure()
    {
        string srr = CliSrrFixture.WriteSrr(Path.Combine(TempDir, "bulk.srr"),
            ("release.nfo", NfoBytes), ("Sample/clip.srs", SrsBytes));
        string outDir = Path.Combine(TempDir, "out");
        using var console = new ConsoleCapture();

        int exitCode = await Program.Main(["extract", "-o", outDir, srr]);

        Assert.Equal(0, exitCode);
        Assert.Equal(NfoBytes, File.ReadAllBytes(Path.Combine(outDir, "release.nfo")));
        Assert.Equal(SrsBytes, File.ReadAllBytes(Path.Combine(outDir, "Sample", "clip.srs")));
        Assert.Contains("release.nfo", console.Stdout, StringComparison.Ordinal);
        Assert.Contains("Sample/clip.srs", console.Stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Extract_HostileParentSegmentName_RefusesWholeSrrAndWritesNothing()
    {
        // The benign entry comes first in block order: silent sanitizing (the old behavior)
        // would extract it and rewrite "../evil.txt" as "evil.txt" inside the output directory.
        // The contract under test is refusal: exit 2, no file inside the output directory, and
        // — above all — no file ABOVE it.
        string srr = CliSrrFixture.WriteSrr(Path.Combine(TempDir, "hostile.srr"),
            ("release.nfo", NfoBytes), ("../evil.txt", SrsBytes));
        string outDir = Path.Combine(TempDir, "out");
        Directory.CreateDirectory(outDir);
        using var console = new ConsoleCapture();

        int exitCode = await Program.Main(["extract", "-o", outDir, srr]);

        Assert.Equal(2, exitCode);
        Assert.Empty(Directory.EnumerateFileSystemEntries(outDir));
        Assert.False(File.Exists(Path.Combine(TempDir, "evil.txt")));
        Assert.Contains("evil.txt", console.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Extract_RootedName_RefusesWholeSrrAndWritesNothing()
    {
        string srr = CliSrrFixture.WriteSrr(Path.Combine(TempDir, "rooted.srr"),
            ("C:/evil.txt", SrsBytes));
        string outDir = Path.Combine(TempDir, "out");
        Directory.CreateDirectory(outDir);
        using var console = new ConsoleCapture();

        int exitCode = await Program.Main(["extract", "-o", outDir, srr]);

        Assert.Equal(2, exitCode);
        Assert.Empty(Directory.EnumerateFileSystemEntries(outDir));
    }

    [Fact]
    public async Task Extract_WithoutOutputOption_ReturnsUsageError()
    {
        using var console = new ConsoleCapture();

        int exitCode = await Program.Main(["extract", "some.srr"]);

        Assert.Equal(1, exitCode);
        Assert.Contains("Usage: rescene extract", console.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Extract_MissingSrrFile_ReturnsTwo()
    {
        using var console = new ConsoleCapture();

        int exitCode = await Program.Main(["extract", "-o", Path.Combine(TempDir, "out"), Path.Combine(TempDir, "absent.srr")]);

        Assert.Equal(2, exitCode);
        Assert.Contains("SRR file not found", console.Stderr, StringComparison.Ordinal);
    }
}
