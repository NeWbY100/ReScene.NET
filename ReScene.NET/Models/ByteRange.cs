namespace ReScene.NET.Models;

public class ByteRange
{
    public string PropertyName { get; set; } = string.Empty;
    public long Offset { get; set; }
    public long Length { get; set; }
}
