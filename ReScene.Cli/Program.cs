namespace ReScene.Cli;

/// <summary>
/// Entry point for the ReScene CLI. Routes to the appropriate subcommand.
/// </summary>
public static class Program
{
    /// <summary>
    /// Dispatches to the requested subcommand.
    /// </summary>
    /// <param name="args">
    /// The full argv from the command line.
    /// </param>
    /// <returns>
    /// 0 on success, non-zero on failure.
    /// </returns>
    public static async Task<int> Main(string[] args)
    {
        if (args.Length == 0)
        {
            return PrintUsage();
        }

        string command = args[0].ToLowerInvariant();
        string[] rest = args[1..];

        return command switch
        {
            "inspect" => Commands.InspectCommand.Run(rest),
            "verify" => Commands.VerifyCommand.Run(rest),
            "create" => await Commands.CreateCommand.RunAsync(rest),
            "extract" => Commands.ExtractCommand.Run(rest),
            "--help" or "-h" or "help" => PrintUsage(),
            _ => Unknown(command)
        };
    }

    private static int PrintUsage()
    {
        Console.WriteLine("Usage: rescene <command> [args]");
        Console.WriteLine();
        Console.WriteLine("Commands:");
        Console.WriteLine("  inspect <file>                List blocks in an SRR/SRS/RAR file");
        Console.WriteLine("  verify  <file.srr>            Validate SRR structural integrity");
        Console.WriteLine("  create  -o <out.srr> <rar>... Create an SRR from RAR volumes");
        Console.WriteLine("  extract -o <dir> <file.srr>   Extract stored files from an SRR");
        return 0;
    }

    private static int Unknown(string command)
    {
        Console.Error.WriteLine($"Unknown command: {command}");
        Console.Error.WriteLine("Run 'rescene --help' for usage.");
        return 1;
    }
}
