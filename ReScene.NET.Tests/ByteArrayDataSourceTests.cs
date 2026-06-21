using ReScene.NET.Services;

namespace ReScene.NET.Tests;

public class ByteArrayDataSourceTests
{
    [Fact]
    public void Length_IsBufferLength()
    {
        using var source = new ByteArrayDataSource([1, 2, 3, 4]);
        Assert.Equal(4, source.Length);
    }

    [Fact]
    public void Read_ReturnsRequestedBytes()
    {
        using var source = new ByteArrayDataSource([10, 20, 30, 40, 50]);
        byte[] buffer = new byte[3];

        int read = source.Read(1, buffer, 0, 3);

        Assert.Equal(3, read);
        Assert.Equal(new byte[] { 20, 30, 40 }, buffer);
    }

    [Fact]
    public void Read_ClampsAtEnd()
    {
        using var source = new ByteArrayDataSource([10, 20, 30]);
        byte[] buffer = new byte[10];

        int read = source.Read(1, buffer, 0, 10);

        Assert.Equal(2, read);
        Assert.Equal(20, buffer[0]);
        Assert.Equal(30, buffer[1]);
    }

    [Fact]
    public void Read_PastEnd_ReturnsZero()
    {
        using var source = new ByteArrayDataSource([1, 2, 3]);
        byte[] buffer = new byte[4];

        Assert.Equal(0, source.Read(3, buffer, 0, 4));
        Assert.Equal(0, source.Read(99, buffer, 0, 4));
    }

    [Fact]
    public void Read_WritesAtBufferOffset()
    {
        using var source = new ByteArrayDataSource([7, 8]);
        byte[] buffer = new byte[4];

        int read = source.Read(0, buffer, 2, 2);

        Assert.Equal(2, read);
        Assert.Equal(new byte[] { 0, 0, 7, 8 }, buffer);
    }
}
