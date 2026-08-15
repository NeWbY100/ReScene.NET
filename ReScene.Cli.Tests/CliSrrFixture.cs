using System.Text;

namespace ReScene.Cli.Tests;

/// <summary>
/// Synthesizes minimal SRR files (header block + stored-file blocks) byte-for-byte the way
/// ReScene.Tests' SRRTestDataBuilder does, without validating stored names — hostile names
/// like <c>../evil.txt</c> can only come from raw bytes, since every real writer in the
/// library rejects them at creation time.
/// </summary>
internal static class CliSrrFixture
{
    public static string WriteSrr(string path, params (string Name, byte[] Data)[] storedFiles)
    {
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true))
        {
            WriteHeaderBlock(writer, "ReScene.Cli.Tests");
            foreach ((string name, byte[] data) in storedFiles)
            {
                WriteStoredFileBlock(writer, name, data);
            }
        }

        File.WriteAllBytes(path, stream.ToArray());
        return path;
    }

    private static void WriteHeaderBlock(BinaryWriter writer, string appName)
    {
        byte[] appNameBytes = Encoding.UTF8.GetBytes(appName);
        writer.Write((ushort)0x6969);                              // CRC sentinel
        writer.Write((byte)0x69);                                  // SRR header type
        writer.Write((ushort)0x0001);                              // app-name-present flag
        writer.Write((ushort)(7 + 2 + appNameBytes.Length));       // header size
        writer.Write((ushort)appNameBytes.Length);
        writer.Write(appNameBytes);
    }

    private static void WriteStoredFileBlock(BinaryWriter writer, string fileName, byte[] fileData)
    {
        byte[] nameBytes = Encoding.UTF8.GetBytes(fileName);
        writer.Write((ushort)0x6A6A);                              // CRC sentinel
        writer.Write((byte)0x6A);                                  // stored-file type
        writer.Write((ushort)0x0000);                              // flags
        writer.Write((ushort)(7 + 4 + 2 + nameBytes.Length));      // header size
        writer.Write((uint)fileData.Length);                       // data length (addSize)
        writer.Write((ushort)nameBytes.Length);
        writer.Write(nameBytes);
        writer.Write(fileData);
    }
}
