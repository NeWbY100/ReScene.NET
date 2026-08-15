namespace ReScene.Cli.Tests;

/// <summary>
/// Tests for the <c>inspect</c> verb: block listing output and the usage / load-failure exit
/// codes (0 success, 1 usage, 2 failure).
/// </summary>
public class InspectCommandTests : TempDirTestBase
{
    [Fact]
    public async Task Inspect_SrrWithStoredFile_ListsHeaderAndStoredFileRows()
    {
        string srr = CliSrrFixture.WriteSrr(
            Path.Combine(TempDir, "inspect.srr"), ("release.nfo", [1, 2, 3, 4]));
        using var console = new ConsoleCapture();

        int exitCode = await Program.Main(["inspect", srr]);

        Assert.Equal(0, exitCode);
        Assert.Contains("Header", console.Stdout, StringComparison.Ordinal);
        Assert.Contains("StoredFile", console.Stdout, StringComparison.Ordinal);
        Assert.Contains("release.nfo", console.Stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Inspect_NoArguments_ReturnsUsageError()
    {
        using var console = new ConsoleCapture();

        int exitCode = await Program.Main(["inspect"]);

        Assert.Equal(1, exitCode);
        Assert.Contains("Usage: rescene inspect", console.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Inspect_MissingFile_ReturnsTwo()
    {
        using var console = new ConsoleCapture();

        int exitCode = await Program.Main(["inspect", Path.Combine(TempDir, "absent.srr")]);

        Assert.Equal(2, exitCode);
        Assert.Contains("File not found", console.Stderr, StringComparison.Ordinal);
    }
}
