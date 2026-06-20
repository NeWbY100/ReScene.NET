using System.Text;
using ReScene.NET.Services;

namespace ReScene.NET.Tests;

public class SRREditingServiceImageTests : TempDirTestBase
{
    /// <summary>
    /// Writes a minimal valid SRR: a header block (0x69) followed by one stored-file
    /// block (0x6A) carrying <paramref name="data"/>. Mirrors the on-disk layout the
    /// library parser expects.
    /// </summary>
    internal static string WriteMinimalSrr(string dir, string srrName, string storedName, byte[] data)
    {
        using var ms = new MemoryStream();
        using (var w = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true))
        {
            // SRR header block (no app name): CRC sentinel + type + flags + headerSize(7)
            w.Write((ushort)0x6969);
            w.Write((byte)0x69);
            w.Write((ushort)0x0000);
            w.Write((ushort)7);

            // Stored-file block: CRC + type + flags + headerSize + addSize + nameLen + name + data
            byte[] nameBytes = Encoding.UTF8.GetBytes(storedName);
            ushort headerSize = (ushort)(7 + 4 + 2 + nameBytes.Length);
            w.Write((ushort)0x6A6A);
            w.Write((byte)0x6A);
            w.Write((ushort)0x0000);
            w.Write(headerSize);
            w.Write((uint)data.Length);
            w.Write((ushort)nameBytes.Length);
            w.Write(nameBytes);
            w.Write(data);
        }

        string path = Path.Combine(dir, srrName);
        File.WriteAllBytes(path, ms.ToArray());
        return path;
    }

    [Fact]
    public async Task ReadStoredFileBytesAsync_ReturnsStoredBytes()
    {
        byte[] data = [0xAA, 0xBB, 0xCC, 0xDD];
        string srr = WriteMinimalSrr(TempDir, "svc.srr", "proof.jpg", data);

        var service = new SRREditingService();
        byte[]? bytes = await service.ReadStoredFileBytesAsync(srr, "proof.jpg");

        Assert.NotNull(bytes);
        Assert.Equal(data, bytes);
    }

    [Fact]
    public async Task ReadStoredFileBytesAsync_NoMatch_ReturnsNull()
    {
        string srr = WriteMinimalSrr(TempDir, "svc2.srr", "proof.jpg", [0x01]);

        var service = new SRREditingService();
        byte[]? bytes = await service.ReadStoredFileBytesAsync(srr, "absent.png");

        Assert.Null(bytes);
    }
}
