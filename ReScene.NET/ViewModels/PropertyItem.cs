using ReScene.NET.Models;

namespace ReScene.NET.ViewModels;

public class PropertyItem
{
    public string Name { get; set; } = string.Empty;
    public string Value { get; set; } = string.Empty;
    public ByteRange? ByteRange { get; set; }
    public bool HasByteRange => ByteRange != null;
    public bool IsIndented { get; set; }
    public bool IsDifferent { get; set; }
    public bool IsWarning { get; set; }
    public bool IsSeparator { get; set; }
}
