using ReScene.NET.Helpers;

namespace ReScene.NET.Tests;

public class TextEncodingOptionsTests
{
    [Fact]
    public void All_StartsWithUtf8_AndHasSevenEntries()
    {
        var all = TextEncodingOptions.All;

        Assert.Equal(7, all.Count);
        Assert.Equal("UTF-8", all[0].DisplayName);
        Assert.Contains(all, e => e.DisplayName == "CP437 (DOS)");
        Assert.Contains(all, e => e.DisplayName == "ISO-8859-1 (Latin-1)");
    }

    [Fact]
    public void Cp437_DecodesBoxDrawingBytes()
    {
        var cp437 = TextEncodingOptions.All.First(e => e.DisplayName == "CP437 (DOS)").Encoding;

        // CP437: 0xC9 = ╔ (U+2554), 0xB0 = ░ (U+2591)
        string text = cp437.GetString([0xC9, 0xB0]);

        Assert.Equal("╔░", text);
    }

    [Fact]
    public void AllEntries_HaveNonNullEncoding()
    {
        Assert.All(TextEncodingOptions.All, e => Assert.NotNull(e.Encoding));
    }

    [Fact]
    public void ToString_ReturnsDisplayName()
    {
        // The encoding ComboBox's selection box renders via ToString(), so it must be the
        // friendly name — not the record's default "TextEncodingOption { ... }" form.
        var utf8 = TextEncodingOptions.All[0];

        Assert.Equal("UTF-8", utf8.ToString());
        Assert.All(TextEncodingOptions.All, e => Assert.Equal(e.DisplayName, e.ToString()));
    }
}
