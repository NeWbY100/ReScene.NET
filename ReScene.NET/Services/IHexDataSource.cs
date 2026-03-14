namespace ReScene.NET.Services;

public interface IHexDataSource : IDisposable
{
    long Length { get; }
    int Read(long position, byte[] buffer, int offset, int count);
}
