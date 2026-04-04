namespace ReScene.NET.Models;

/// <summary>
/// Represents a recently opened file displayed on the Home tab.
/// </summary>
public class RecentFileEntry
{
    /// <summary>Gets or sets the absolute path to the file.</summary>
    public string FilePath { get; set; } = string.Empty;

    /// <summary>Gets or sets the display-friendly file name.</summary>
    public string FileName { get; set; } = string.Empty;

    /// <summary>Gets or sets the date and time the file was last opened.</summary>
    public DateTime LastOpened
    {
        get; set;
    }
}
