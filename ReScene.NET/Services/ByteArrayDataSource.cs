using ReScene.Hex;

namespace ReScene.NET.Services;

/// <summary>
/// An <see cref="IHexDataSource"/> over an in-memory buffer, so the Hex view and text decoder
/// can read a file that has already been loaded into a <see cref="byte"/> array.
/// </summary>
public sealed class ByteArrayDataSource(byte[] data) : IHexDataSource
{
    /// <inheritdoc />
    public long Length => data.Length;

    /// <inheritdoc />
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

    public void Dispose()
    {
        // Nothing to release — the buffer is owned by the caller.
    }
}
