namespace ReScene.NET.Models;

public class RecentFileEntry
{
    public string FilePath { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;
    public DateTime LastOpened { get; set; }
}
