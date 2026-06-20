using System.Text;
using ReScene.Hex;
using ReScene.NET.Helpers;

namespace ReScene.NET.Tests;

public class TextDecoderTests
{
    // Minimal in-memory IHexDataSource over a byte[], for decoding tests.
    private sealed class ByteArrayDataSource(byte[] data) : IHexDataSource
    {
        public long Length => data.Length;

        public int Read(long position, byte[] buffer, int offset, int count)
        {
            if (position < 0 || position >= data.Length)
            {
                return 0;
            }

            int available = (int)Math.Min(count, data.Length - position);
            Array.Copy(data, position, buffer, offset, available);
            return available;
        }

        public void Dispose() { }
    }

    [Fact]
    public void Decode_Utf8_RoundTrips()
    {
        byte[] data = Encoding.UTF8.GetBytes("Héllo, NFO");
        var source = new ByteArrayDataSource(data);

        (string text, bool truncated) = TextDecoder.Decode(source, data.Length, Encoding.UTF8, 1024);

        Assert.Equal("Héllo, NFO", text);
        Assert.False(truncated);
    }

    [Fact]
    public void Decode_Utf16Le_RoundTrips()
    {
        byte[] data = Encoding.Unicode.GetBytes("Ωmega");
        var source = new ByteArrayDataSource(data);

        (string text, bool truncated) = TextDecoder.Decode(source, data.Length, Encoding.Unicode, 1024);

        Assert.Equal("Ωmega", text);
        Assert.False(truncated);
    }

    [Fact]
    public void Decode_Cp437_DecodesArtBytes()
    {
        var cp437 = TextEncodingOptions.All.First(e => e.DisplayName == "CP437 (DOS)").Encoding;
        byte[] data = [0xC9, 0xB0]; // ╔ ░
        var source = new ByteArrayDataSource(data);

        (string text, _) = TextDecoder.Decode(source, data.Length, cp437, 1024);

        Assert.Equal("╔░", text);
    }

    [Fact]
    public void Decode_LongerThanMax_TruncatesAndFlags()
    {
        byte[] data = Encoding.ASCII.GetBytes("ABCDEFGHIJ"); // 10 bytes
        var source = new ByteArrayDataSource(data);

        (string text, bool truncated) = TextDecoder.Decode(source, data.Length, Encoding.ASCII, 4);

        Assert.Equal("ABCD", text);
        Assert.True(truncated);
    }

    [Fact]
    public void Decode_NullSource_ReturnsEmpty()
    {
        (string text, bool truncated) = TextDecoder.Decode(null, 10, Encoding.UTF8, 1024);

        Assert.Equal(string.Empty, text);
        Assert.False(truncated);
    }

    [Fact]
    public void Decode_ZeroLength_ReturnsEmpty()
    {
        var source = new ByteArrayDataSource([1, 2, 3]);

        (string text, bool truncated) = TextDecoder.Decode(source, 0, Encoding.UTF8, 1024);

        Assert.Equal(string.Empty, text);
        Assert.False(truncated);
    }
}
