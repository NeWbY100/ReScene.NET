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

            Console.WriteLine($"{"Offset",-12} {"Type",-20} {"Size",10}  Name");
            Console.WriteLine(new string('-', 60));

            if (srr.HeaderBlock is not null)
            {
                PrintRow(srr.HeaderBlock.BlockPosition, "Header", srr.HeaderBlock.HeaderSize, srr.HeaderBlock.AppName ?? string.Empty);
            }

            foreach (var s in srr.StoredFiles)
            {
                PrintRow(s.BlockPosition, "StoredFile", s.HeaderSize + s.AddSize, s.FileName);
            }

            foreach (var o in srr.OsoHashBlocks)
            {
                PrintRow(o.BlockPosition, "OsoHash", o.HeaderSize, o.FileName);
            }

            foreach (var r in srr.RarFiles)
            {
                PrintRow(r.BlockPosition, "RarFile", r.HeaderSize + r.AddSize, r.FileName);
            }

            foreach (var p in srr.RarPaddingBlocks)
            {
                PrintRow(p.BlockPosition, "RarPadding", p.HeaderSize + p.AddSize, p.RarFileName);
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
