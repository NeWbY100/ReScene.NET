using System.IO.MemoryMappedFiles;

namespace ReScene.NET.Services;

/// <summary>
/// Provides read-only hex data access to a file via memory-mapped I/O.
/// </summary>
public sealed class MemoryMappedDataSource : IHexDataSource
{
    private readonly MemoryMappedFile _mmf;
    private readonly MemoryMappedViewAccessor _accessor;

    /// <summary>
    /// Gets the total length of the mapped file in bytes.
    /// </summary>
    public long Length
    {
        get;
    }

    /// <summary>
    /// Opens the specified file as a read-only memory-mapped data source.
    /// </summary>
    /// <param name="filePath">
    /// Path to the file to map.
    /// </param>
    public MemoryMappedDataSource(string filePath)
    {
        Length = new FileInfo(filePath).Length;
        _mmf = MemoryMappedFile.CreateFromFile(filePath, FileMode.Open, null, 0, MemoryMappedFileAccess.Read);
        _accessor = _mmf.CreateViewAccessor(0, Length, MemoryMappedFileAccess.Read);
    }

    /// <inheritdoc />
    public int Read(long position, byte[] buffer, int offset, int count)
    {
        if (position < 0 || position >= Length)
        {
            return 0;
        }

        int available = (int)Math.Min(count, Length - position);
        return _accessor.ReadArray(position, buffer, offset, available);
    }

    public void Dispose()
    {
        _accessor.Dispose();
        _mmf.Dispose();
    }
}
