namespace ReScene.Cli.Tests;

/// <summary>
/// Tests for the <c>verify</c> verb against a real SRR fixture and a corrupt file
/// (0 valid, 1 usage, 2 invalid or unreadable).
/// </summary>
public class VerifyCommandTests : TempDirTestBase
{
    private static string FixturePath(string name) => Path.Combine(AppContext.BaseDirectory, "TestData", name);

    [Fact]
    public async Task Verify_RealSrrFixture_ReportsOkAndReturnsZero()
    {
        using var console = new ConsoleCapture();

        int exitCode = await Program.Main(["verify", FixturePath("store_little.srr")]);

        Assert.Equal(0, exitCode);
        Assert.Contains("OK", console.Stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Verify_GarbageFile_ReturnsTwo()
    {
        string garbage = Path.Combine(TempDir, "garbage.srr");
        File.WriteAllBytes(garbage, [0xDE, 0xAD, 0xBE, 0xEF, 0x00, 0x11, 0x22, 0x33]);
        using var console = new ConsoleCapture();

        int exitCode = await Program.Main(["verify", garbage]);

        Assert.Equal(2, exitCode);
    }

    [Fact]
    public async Task Verify_NoArguments_ReturnsUsageError()
    {
        using var console = new ConsoleCapture();

        int exitCode = await Program.Main(["verify"]);

        Assert.Equal(1, exitCode);
        Assert.Contains("Usage: rescene verify", console.Stderr, StringComparison.Ordinal);
    }
}
