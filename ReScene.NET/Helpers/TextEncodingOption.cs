using System.Text;

namespace ReScene.NET.Helpers;

/// <summary>A selectable text encoding: a human-friendly name plus the backing <see cref="Encoding"/>.</summary>
public sealed record TextEncodingOption(string DisplayName, Encoding Encoding)
{
    // The themed ComboBox's selection box falls back to ToString() (DisplayMemberPath only drives
    // the dropdown items), so present the friendly name rather than the record's default rendering.
    public override string ToString() => DisplayName;
}

/// <summary>
/// The curated set of encodings offered by the Inspector's Text view. CP437 and Windows-1252 are not
/// built into .NET, so the CodePages provider is registered once before they are resolved.
/// </summary>
public static class TextEncodingOptions
{
    private static readonly Lazy<IReadOnlyList<TextEncodingOption>> _all = new(Build);

    /// <summary>The encodings in display order; UTF-8 (the default) is first.</summary>
    public static IReadOnlyList<TextEncodingOption> All => _all.Value;

    private static IReadOnlyList<TextEncodingOption> Build()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

        return
        [
            new TextEncodingOption("UTF-8", Encoding.UTF8),
            new TextEncodingOption("UTF-16 LE", Encoding.Unicode),
            new TextEncodingOption("UTF-16 BE", Encoding.BigEndianUnicode),
            new TextEncodingOption("ASCII", Encoding.ASCII),
            new TextEncodingOption("Windows-1252", Encoding.GetEncoding(1252)),
            new TextEncodingOption("ISO-8859-1 (Latin-1)", Encoding.Latin1),
            new TextEncodingOption("CP437 (DOS)", Encoding.GetEncoding(437)),
        ];
    }
}
