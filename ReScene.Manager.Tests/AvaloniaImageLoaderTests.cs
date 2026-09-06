using Avalonia;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using ReScene.Manager.Services;

namespace ReScene.Manager.Tests;

/// <summary>
/// Headless tests for <see cref="AvaloniaImageLoader"/>. A PNG is produced at runtime by rendering a
/// tiny <see cref="RenderTargetBitmap"/> and saving it to a <see cref="MemoryStream"/>, then decoded
/// back through the loader — proving a real Skia decode round-trip and the null-on-failure contract.
/// </summary>
public class AvaloniaImageLoaderTests
{
    private static byte[] CreatePngBytes(int width, int height)
    {
        var target = new RenderTargetBitmap(new PixelSize(width, height), new Vector(96, 96));
        using (DrawingContext ctx = target.CreateDrawingContext())
        {
            ctx.FillRectangle(Brushes.Red, new Rect(0, 0, width, height));
        }

        using var buffer = new MemoryStream();
        target.Save(buffer, PngBitmapEncoderOptions.Default);
        return buffer.ToArray();
    }

    [AvaloniaFact]
    public void Load_Stream_DecodesPng_ToBitmapWithExpectedPixelSize()
    {
        byte[] png = CreatePngBytes(6, 4);
        var loader = new AvaloniaImageLoader();

        using var stream = new MemoryStream(png);
        object? result = loader.Load(stream);

        Bitmap bitmap = Assert.IsAssignableFrom<Bitmap>(result);
        Assert.Equal(new PixelSize(6, 4), bitmap.PixelSize);
    }

    [AvaloniaFact]
    public void Load_Stream_GarbageBytes_ReturnsNull()
    {
        var loader = new AvaloniaImageLoader();
        using var stream = new MemoryStream([0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08]);

        Assert.Null(loader.Load(stream));
    }

    [Fact]
    public void Load_Stream_Null_Throws()
    {
        var loader = new AvaloniaImageLoader();

        Assert.Throws<ArgumentNullException>(() => loader.Load((Stream)null!));
    }

    [AvaloniaFact]
    public void Load_Path_MissingFile_ReturnsNull()
    {
        var loader = new AvaloniaImageLoader();
        string missing = Path.Combine(Path.GetTempPath(), $"rescene-no-such-image-{Guid.NewGuid():N}.png");

        Assert.Null(loader.Load(missing));
    }

    [AvaloniaFact]
    public void Load_Path_DecodesPng_FromTempFile()
    {
        byte[] png = CreatePngBytes(5, 3);
        string path = Path.Combine(Path.GetTempPath(), $"rescene-image-{Guid.NewGuid():N}.png");
        File.WriteAllBytes(path, png);
        try
        {
            var loader = new AvaloniaImageLoader();
            Bitmap bitmap = Assert.IsAssignableFrom<Bitmap>(loader.Load(path));
            Assert.Equal(new PixelSize(5, 3), bitmap.PixelSize);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
