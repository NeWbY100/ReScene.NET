namespace ReScene.NET.Helpers;

/// <summary>
/// Decides which stored files are previewable as images. Pure (no WPF), so it can gate the
/// "View Image" / "Preview" affordances in view-models and be unit-tested directly.
/// </summary>
public static class ImagePreviewSupport
{
    private static readonly HashSet<string> _supportedExtensions =
        new(StringComparer.OrdinalIgnoreCase) { ".jpg", ".jpeg", ".png", ".gif", ".bmp" };

    /// <summary>
    /// Returns <see langword="true"/> when <paramref name="fileName"/> has a supported image
    /// extension (.jpg, .jpeg, .png, .gif, .bmp), case-insensitively.
    /// </summary>
    public static bool IsSupported(string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return false;
        }

        string ext = Path.GetExtension(fileName);
        return ext.Length > 0 && _supportedExtensions.Contains(ext);
    }
}
