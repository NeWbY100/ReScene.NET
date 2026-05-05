using ReScene.SRR;

namespace ReScene.Cli.Commands;

/// <summary>
/// Lists blocks contained in an SRR file.
/// </summary>
public static class InspectCommand
{
    /// <summary>
    /// Runs the inspect command.
    /// </summary>
    /// <param name="args">
    /// Positional arguments after the subcommand name. Expected: a single file path.
    /// </param>
    /// <returns>
    /// 0 on success, 1 on usage error, 2 on load failure.
    /// </returns>
    public static int Run(string[] args)
    {
        if (args.Length != 1)
        {
            Console.Error.WriteLine("Usage: rescene inspect <file>");
            return 1;
        }

        string path = args[0];

        if (!File.Exists(path))
        {
            Console.Error.WriteLine($"File not found: {path}");
            return 2;
        }

        try
        {
            SRRFile srr = SRRFile.Load(path);

            List<(long Offset, string Type, long Size, string Name)> rows = [];

            if (srr.HeaderBlock is not null)
            {
                rows.Add((srr.HeaderBlock.BlockPosition, "Header", srr.HeaderBlock.HeaderSize, srr.HeaderBlock.AppName ?? string.Empty));
            }

            foreach (SrrStoredFileBlock stored in srr.StoredFiles)
            {
                rows.Add((stored.BlockPosition, "StoredFile", stored.HeaderSize + stored.AddSize, stored.FileName));
            }

            foreach (SrrOsoHashBlock oso in srr.OsoHashBlocks)
            {
                rows.Add((oso.BlockPosition, "OsoHash", oso.HeaderSize, oso.FileName));
            }

            foreach (SrrRarFileBlock rarFile in srr.RarFiles)
            {
                rows.Add((rarFile.BlockPosition, "RarFile", rarFile.HeaderSize + rarFile.AddSize, rarFile.FileName));
            }

            foreach (SrrRarPaddingBlock padding in srr.RarPaddingBlocks)
            {
                rows.Add((padding.BlockPosition, "RarPadding", padding.HeaderSize + padding.AddSize, padding.RarFileName));
            }

            rows.Sort((a, b) => a.Offset.CompareTo(b.Offset));

            Console.WriteLine($"{"Offset",-12} {"Type",-20} {"Size",10}  Name");
            Console.WriteLine(new string('-', 60));

            foreach (var row in rows)
            {
                PrintRow(row.Offset, row.Type, row.Size, row.Name);
            }

            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Error loading {path}: {ex.Message}");
            return 2;
        }
    }

    private static void PrintRow(long offset, string type, long size, string name)
    {
        Console.WriteLine($"0x{offset:X8}   {type,-20} {size,10:N0}  {name}");
    }
}
