using System.Windows.Media.Imaging;

namespace ReScene.NET.Helpers;

/// <summary>
/// Decodes image bytes to a frozen <see cref="BitmapSource"/>. Returns <see langword="null"/> when the
/// bytes are not a decodable image, so callers can treat "not an image" as a normal outcome.
/// </summary>
public static class ImageDecoder
{
    public static BitmapSource? TryDecode(byte[] data)
    {
        try
        {
            using var stream = new MemoryStream(data);
            BitmapFrame frame = BitmapFrame.Create(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
            frame.Freeze();
            return frame;
        }
        catch (Exception ex) when (ex is NotSupportedException or ArgumentException or FileFormatException or IOException or OverflowException)
        {
            return null;
        }
    }
}
