namespace ReScene.NET.Services;

/// <summary>
/// Shows an embedded image's bytes in a popup preview window. Abstracted so view-models can
/// request a preview without referencing WPF imaging types (and so tests can verify the call).
/// </summary>
public interface IImagePreviewService
{
    /// <summary>
    /// Decodes <paramref name="data"/> and shows it in a modal preview window titled with
    /// <paramref name="fileName"/>. On decode failure, shows an error dialog and opens nothing.
    /// </summary>
    public void Preview(byte[] data, string fileName);
}
