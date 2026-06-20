using System.Windows;
using System.Windows.Media.Imaging;
using ReScene.NET.Helpers;

namespace ReScene.NET.Views;

public partial class ImagePreviewWindow : Window
{
    public ImagePreviewWindow(BitmapSource image, string fileName, long byteSize)
    {
        InitializeComponent();
        DataContext = new PreviewData(
            image,
            $"Image Preview — {fileName}",
            $"{fileName}  •  {image.PixelWidth}×{image.PixelHeight}  •  {FormatUtilities.FormatSize(byteSize)}");
        SizeToImage(image);
        SourceInitialized += (_, _) => DarkTitleBar.Enable(this);
    }

    // Fit the window to the image, capped to the working area (with a margin) and the window minimums.
    private void SizeToImage(BitmapSource image)
    {
        Rect work = SystemParameters.WorkArea;
        double maxW = work.Width - 80;
        double maxH = work.Height - 120;
        Width = Math.Clamp(image.PixelWidth + 40, MinWidth, maxW);
        Height = Math.Clamp(image.PixelHeight + 90, MinHeight, maxH);
    }

    private sealed record PreviewData(BitmapSource Image, string TitleText, string StatusText);
}
