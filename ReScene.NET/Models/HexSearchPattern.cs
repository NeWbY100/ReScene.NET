using System.Globalization;
using System.Text;

namespace ReScene.NET.Models;

/// <summary>
/// Represents a parsed hex or ASCII search pattern, with the raw bytes
/// and a display-friendly representation of the original input.
/// </summary>
public sealed class HexSearchPattern
{
    public byte[] Bytes { get; }

    public string DisplayText { get; }

    public bool IsHex { get; }

    private HexSearchPattern(byte[] bytes, string displayText, bool isHex)
    {
        Bytes = bytes;
        DisplayText = displayText;
        IsHex = isHex;
    }

    /// <summary>
    /// Attempts to parse the given input string into a <see cref="HexSearchPattern"/>.
    /// Returns <see langword="null"/> if the input is null or whitespace, or if hex
    /// parsing fails (odd length, non-hex characters).
    /// </summary>
    /// <param name="input">
    /// The raw search text entered by the user.
    /// </param>
    /// <param name="asHex">
    /// When <see langword="true"/>, the input is interpreted as hex pairs (spaces and
    /// dashes are stripped before parsing). When <see langword="false"/>, the input is
    /// treated as a UTF-8 string.
    /// </param>
    /// <returns>
    /// A <see cref="HexSearchPattern"/> on success, or <see langword="null"/> on failure.
    /// </returns>
    public static HexSearchPattern? TryParse(string input, bool asHex)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return null;
        }

        if (asHex)
        {
            string stripped = input
                .Replace(" ", string.Empty, StringComparison.Ordinal)
                .Replace("-", string.Empty, StringComparison.Ordinal);

            if (stripped.Length == 0 || stripped.Length % 2 != 0)
            {
                return null;
            }

            byte[] bytes = new byte[stripped.Length / 2];

            for (int i = 0; i < bytes.Length; i++)
            {
                ReadOnlySpan<char> pair = stripped.AsSpan(i * 2, 2);

                if (!byte.TryParse(pair, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out byte b))
                {
                    return null;
                }

                bytes[i] = b;
            }

            return new HexSearchPattern(bytes, input, true);
        }
        else
        {
            byte[] bytes = Encoding.UTF8.GetBytes(input);
            return new HexSearchPattern(bytes, input, false);
        }
    }
}
