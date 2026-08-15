namespace ReScene.Cli.Tests;

/// <summary>
/// Tests for <see cref="Program"/>'s command routing: usage banner, help aliases, and the
/// unknown-command error path, with their exit codes.
/// </summary>
public class ProgramTests
{
    [Fact]
    public async Task Main_NoArguments_PrintsUsageAndReturnsZero()
    {
        using var console = new ConsoleCapture();

        int exitCode = await Program.Main([]);

        Assert.Equal(0, exitCode);
        Assert.Contains("Usage: rescene <command> [args]", console.Stdout, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("--help")]
    [InlineData("-h")]
    [InlineData("help")]
    public async Task Main_HelpAliases_PrintUsageAndReturnZero(string alias)
    {
        using var console = new ConsoleCapture();

        int exitCode = await Program.Main([alias]);

        Assert.Equal(0, exitCode);
        Assert.Contains("Commands:", console.Stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Main_UnknownCommand_PrintsErrorAndReturnsOne()
    {
        using var console = new ConsoleCapture();

        int exitCode = await Program.Main(["frobnicate"]);

        Assert.Equal(1, exitCode);
        Assert.Contains("Unknown command: frobnicate", console.Stderr, StringComparison.Ordinal);
    }
}
