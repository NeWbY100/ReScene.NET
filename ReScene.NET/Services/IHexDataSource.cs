namespace ReScene.NET.Services;

public interface IHexDataSource : IDisposable
{
    public long Length
    {
        get;
    }
    public int Read(long position, byte[] buffer, int offset, int count);
}
