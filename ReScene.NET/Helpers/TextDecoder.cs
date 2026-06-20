using System.Text;
using ReScene.Hex;

namespace ReScene.NET.Helpers;

/// <summary>
/// Decodes a region of an <see cref="IHexDataSource"/> as text. Pure and side-effect free so the
/// Inspector's Text view can be unit-tested without WPF.
/// </summary>
public static class TextDecoder
{
    /// <summary>
    /// Reads up to <paramref name="maxBytes"/> bytes from the start of <paramref name="source"/>
    /// (relative positions <c>0..</c>) and decodes them with <paramref name="encoding"/>.
    /// </summary>
    /// <returns>
    /// The decoded text, and whether the region was truncated (i.e. <paramref name="length"/> exceeded
    /// <paramref name="maxBytes"/>). Returns <c>("", false)</c> when there is nothing to read.
    /// </returns>
    public static (string Text, bool Truncated) Decode(
        IHexDataSource? source, long length, Encoding encoding, int maxBytes)
    {
        ArgumentNullException.ThrowIfNull(encoding);

        if (source is null || length <= 0 || maxBytes <= 0)
        {
            return (string.Empty, false);
        }

        int toRead = (int)Math.Min(length, maxBytes);
        byte[] buffer = new byte[toRead];

        int total = 0;
        while (total < toRead)
        {
            int read = source.Read(total, buffer, total, toRead - total);
            if (read <= 0)
            {
                break;
            }

            total += read;
        }

        string text = encoding.GetString(buffer, 0, total);
        return (text, length > maxBytes);
    }
}
