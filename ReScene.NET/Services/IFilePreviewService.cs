namespace ReScene.NET.Services;

/// <summary>
/// Shows a tabbed (Hex / Text / Image) preview of a file's bytes in a popup window.
/// </summary>
public interface IFilePreviewService
{
    /// <summary>Opens the preview window for <paramref name="data"/>, titled with <paramref name="fileName"/>.</summary>
    public void Preview(byte[] data, string fileName);
}
