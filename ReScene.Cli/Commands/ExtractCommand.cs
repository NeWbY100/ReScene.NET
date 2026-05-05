using ReScene.SRR;

namespace ReScene.Cli.Commands;

/// <summary>
/// Extracts stored files from an SRR to a directory.
/// </summary>
public static class ExtractCommand
{
    private const int CopyBufferSize = 64 * 1024;

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
            else if (srrPath is null)
            {
                srrPath = args[i];
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
            SRRFile srr = SRRFile.Load(srrPath);

            using FileStream input = File.OpenRead(srrPath);
            byte[] buffer = new byte[CopyBufferSize];

            foreach (var stored in srr.StoredFiles)
            {
                string outPath = ResolveSafeOutputPath(outDir, stored.FileName);
                Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);

                input.Position = stored.DataOffset;

                using FileStream output = new(outPath, FileMode.Create, FileAccess.Write);

                long remaining = stored.FileLength;
                while (remaining > 0)
                {
                    int toRead = (int)Math.Min(buffer.Length, remaining);
                    int read = input.Read(buffer, 0, toRead);

                    if (read <= 0)
                    {
                        break;
                    }

                    output.Write(buffer, 0, read);
                    remaining -= read;
                }

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

    private static string ResolveSafeOutputPath(string outDir, string storedName)
    {
        string normalized = storedName.Replace('\\', '/');
        string[] parts = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);

        List<string> safeParts = [];
        foreach (string part in parts)
        {
            if (part == ".." || Path.IsPathRooted(part))
            {
                continue;
            }

            safeParts.Add(part);
        }

        if (safeParts.Count == 0)
        {
            safeParts.Add("file.bin");
        }

        return Path.Combine([outDir, .. safeParts]);
    }
}
