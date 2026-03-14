namespace ReScene.NET.Services;

public sealed class HexDataSourceSlice(IHexDataSource inner, long offset, long length) : IHexDataSource
{
    public long Length { get; } = length;

    public int Read(long position, byte[] buffer, int bufferOffset, int count)
    {
        if (position < 0 || position >= Length)
            return 0;

        int available = (int)Math.Min(count, Length - position);
        return inner.Read(offset + position, buffer, bufferOffset, available);
    }

    public void Dispose()
    {
        // Does not own the inner source
    }
}
