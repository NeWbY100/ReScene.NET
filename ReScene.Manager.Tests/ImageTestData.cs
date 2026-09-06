using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace ReScene.Manager.Tests;

/// <summary>
/// Produces real, Skia-decoded images for the preview-window/service tests. A tiny
/// <see cref="RenderTargetBitmap"/> is drawn and saved to PNG bytes at runtime, then decoded back
/// through <see cref="Bitmap"/> — so tests exercise a genuine image (not a stub) that
/// <see cref="ReScene.App.Core.ViewModels.FilePreviewViewModel.HasImageTab"/> reports as present.
/// Must be called from an <c>[AvaloniaFact]</c> (needs the headless Skia platform).
/// </summary>
internal static class ImageTestData
{
    public static byte[] CreatePngBytes(int width, int height)
    {
        var target = new RenderTargetBitmap(new PixelSize(width, height), new Vector(96, 96));
        using (DrawingContext ctx = target.CreateDrawingContext())
        {
            ctx.FillRectangle(Brushes.SteelBlue, new Rect(0, 0, width, height));
        }

        using var buffer = new MemoryStream();
        target.Save(buffer, PngBitmapEncoderOptions.Default);
        return buffer.ToArray();
    }

    public static Bitmap CreateBitmap(int width, int height)
    {
        using var stream = new MemoryStream(CreatePngBytes(width, height));
        return new Bitmap(stream);
    }
}
