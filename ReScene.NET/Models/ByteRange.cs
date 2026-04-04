namespace ReScene.NET.Models;

/// <summary>
/// Represents a byte range within a file, used to highlight hex selections in the inspector.
/// </summary>
public class ByteRange
{
    /// <summary>Gets or sets the name of the property this range corresponds to.</summary>
    public string PropertyName { get; set; } = string.Empty;

    /// <summary>Gets or sets the starting byte offset within the file.</summary>
    public long Offset
    {
        get; set;
    }

    /// <summary>Gets or sets the length of the byte range.</summary>
    public long Length
    {
        get; set;
    }
}
