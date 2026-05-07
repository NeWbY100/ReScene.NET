using ReScene.SRR;

namespace ReScene.Cli.Commands;

/// <summary>
/// Runs structural-integrity verification against an SRR file.
/// </summary>
public static class VerifyCommand
{
    /// <summary>
    /// Runs the verify command.
    /// </summary>
    /// <param name="args">
    /// Positional arguments after the subcommand name. Expected: a single SRR file path.
    /// </param>
    /// <returns>
    /// 0 on valid, 1 on usage error, 2 on invalid.
    /// </returns>
    public static int Run(string[] args)
    {
        if (args.Length != 1)
        {
            Console.Error.WriteLine("Usage: rescene verify <file.srr>");
            return 1;
        }

        try
        {
            SRRVerifyResult result = SRRVerifier.Verify(args[0]);

            Console.WriteLine($"Blocks scanned: {result.BlocksScanned}");
            Console.WriteLine($"File size: {result.FileSize:N0}");

            foreach (SRRVerifyIssue issue in result.Issues)
            {
                Console.WriteLine($"[{issue.Severity}] 0x{issue.Offset:X}: {issue.Message}");
            }

            Console.WriteLine(result.IsValid ? "OK" : "INVALID");
            return result.IsValid ? 0 : 2;
        }
        catch (FileNotFoundException ex)
        {
            Console.Error.WriteLine($"File not found: {ex.FileName}");
            return 2;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Error: {ex.Message}");
            return 2;
        }
    }
}
