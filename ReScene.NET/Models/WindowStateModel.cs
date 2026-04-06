namespace ReScene.NET.Models;

/// <summary>
/// Persisted window position, size, and UI state.
/// </summary>
public class WindowStateModel
{
    public double Left { get; set; }
    public double Top { get; set; }
    public double Width { get; set; } = 1280;
    public double Height { get; set; } = 900;
    public bool IsMaximized { get; set; } = true;
    public int SelectedTabIndex { get; set; }
}
