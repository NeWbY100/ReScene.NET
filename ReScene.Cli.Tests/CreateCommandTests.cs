namespace ReScene.Cli.Tests;

/// <summary>
/// Tests for the <c>create</c> verb: a real create-then-verify round-trip from the
/// store_little.rar fixture, plus the usage / missing-input exit codes.
/// </summary>
public class CreateCommandTests : TempDirTestBase
{
    private static string FixturePath(string name) => Path.Combine(AppContext.BaseDirectory, "TestData", name);

    [Fact]
    public async Task Create_FromRealRar_ProducesAnSrrThatVerifiesClean()
    {
        string outSrr = Path.Combine(TempDir, "created.srr");
        using (new ConsoleCapture())
        {
            int createExit = await Program.Main(["create", "-o", outSrr, FixturePath("store_little.rar")]);
            Assert.Equal(0, createExit);
        }

        Assert.True(File.Exists(outSrr));

        using var console = new ConsoleCapture();
        int verifyExit = await Program.Main(["verify", outSrr]);

        Assert.Equal(0, verifyExit);
        Assert.Contains("OK", console.Stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Create_MissingRarFile_ReturnsTwo()
    {
        using var console = new ConsoleCapture();

        int exitCode = await Program.Main(["create", "-o", Path.Combine(TempDir, "out.srr"), Path.Combine(TempDir, "absent.rar")]);

        Assert.Equal(2, exitCode);
        Assert.Contains("RAR file not found", console.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Create_WithoutOutputOption_ReturnsUsageError()
    {
        using var console = new ConsoleCapture();

        int exitCode = await Program.Main(["create", "some.rar"]);

        Assert.Equal(1, exitCode);
        Assert.Contains("Usage: rescene create", console.Stderr, StringComparison.Ordinal);
    }
}
