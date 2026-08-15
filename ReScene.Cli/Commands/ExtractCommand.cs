using ReScene.SRR;

namespace ReScene.Cli.Commands;

/// <summary>
/// Extracts stored files from an SRR to a directory, preserving each entry's relative
/// directory structure. Delegates to <see cref="SRRFile.ExtractStoredFiles"/>, whose
/// validate-before-write contract refuses an SRR carrying a hostile stored name (rooted, or
/// containing "." / ".." segments) outright — the command fails with nothing written —
/// instead of silently rewriting the name the way this command's original hand-rolled copy
/// loop did.
/// </summary>
public static class ExtractCommand
{
    /// <summary>
    /// Runs the extract command.
    /// </summary>
    /// <param name="args">
    /// Positional arguments after the subcommand name.
    /// </param>
    /// <returns>
    /// 0 on success, 1 on usage error, 2 on extraction failure.
    /// </returns>
    public static int Run(string[] args)
    {
        string? outDir = null;
        string? srrPath = null;

        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "-o" && i + 1 < args.Length)
            {
                outDir = args[++i];
            }
            else
            {
                srrPath ??= args[i];
            }
        }

        if (outDir is null || srrPath is null)
        {
            Console.Error.WriteLine("Usage: rescene extract -o <dir> <file.srr>");
            return 1;
        }

        if (!File.Exists(srrPath))
        {
            Console.Error.WriteLine($"SRR file not found: {srrPath}");
            return 2;
        }

        try
        {
            Directory.CreateDirectory(outDir);
            var srr = SRRFile.Load(srrPath);
            _ = srr.ExtractStoredFiles(srrPath, outDir);

            // ExtractStoredFiles is all-or-throw, so reaching this line means every stored
            // file was written.
            foreach (SRRStoredFileBlock stored in srr.StoredFiles)
            {
                Console.WriteLine($"Extracted {stored.FileName} ({stored.FileLength:N0} bytes)");
            }

            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Error: {ex.Message}");
            return 2;
        }
    }
}
