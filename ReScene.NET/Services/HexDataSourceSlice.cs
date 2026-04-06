namespace ReScene.NET.Services;

/// <summary>
/// Provides a windowed view over an existing <see cref="IHexDataSource"/> without owning it.
/// </summary>
public sealed class HexDataSourceSlice(IHexDataSource inner, long offset, long length) : IHexDataSource
{
    /// <summary>
    /// Gets the length of this slice in bytes.
    /// </summary>
    public long Length { get; } = length;

    /// <inheritdoc />
    public int Read(long position, byte[] buffer, int bufferOffset, int count)
    {
        if (position < 0 || position >= Length)
        {
            return 0;
        }

        int available = (int)Math.Min(count, Length - position);
        return inner.Read(offset + position, buffer, bufferOffset, available);
    }

    public void Dispose()
    {
        // Does not own the inner source
    }
}
