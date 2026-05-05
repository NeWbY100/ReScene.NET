using ReScene.SRR;

namespace ReScene.Cli.Commands;

/// <summary>
/// Creates an SRR file from one or more RAR volumes.
/// </summary>
public static class CreateCommand
{
    /// <summary>
    /// Runs the create command asynchronously.
    /// </summary>
    /// <param name="args">
    /// Positional arguments after the subcommand name.
    /// </param>
    /// <returns>
    /// 0 on success, 1 on usage error, 2 on creation failure.
    /// </returns>
    public static async Task<int> RunAsync(string[] args)
    {
        string? outPath = null;
        List<string> rarPaths = [];

        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "-o" && i + 1 < args.Length)
            {
                outPath = args[++i];
            }
            else
            {
                rarPaths.Add(args[i]);
            }
        }

        if (outPath is null || rarPaths.Count == 0)
        {
            Console.Error.WriteLine("Usage: rescene create -o <out.srr> <rar>...");
            return 1;
        }

        foreach (string rar in rarPaths)
        {
            if (!File.Exists(rar))
            {
                Console.Error.WriteLine($"RAR file not found: {rar}");
                return 2;
            }
        }

        try
        {
            var writer = new SRRWriter();
            SrrCreationResult result = await writer.CreateAsync(outPath, rarPaths);

            if (!result.Success)
            {
                Console.Error.WriteLine($"Create failed: {result.ErrorMessage}");
                return 2;
            }

            Console.WriteLine($"Created {outPath}");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Error: {ex.Message}");
            return 2;
        }
    }
}
