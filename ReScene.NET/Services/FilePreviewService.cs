using System.Windows;
using System.Windows.Media.Imaging;
using ReScene.NET.Helpers;
using ReScene.NET.ViewModels;
using ReScene.NET.Views;

namespace ReScene.NET.Services;

/// <summary>
/// Decodes the image (when applicable) and shows the file's bytes in a <see cref="FilePreviewWindow"/>.
/// </summary>
public class FilePreviewService : IFilePreviewService
{
    /// <inheritdoc />
    public void Preview(byte[] data, string fileName)
    {
        BitmapSource? image = ImagePreviewSupport.IsSupported(fileName)
            ? ImageDecoder.TryDecode(data)
            : null;

        var window = new FilePreviewWindow(new FilePreviewViewModel(data, fileName, image))
        {
            Owner = ActiveWindow()
        };
        window.ShowDialog();
    }

    // The Edit-SRR wizard runs in its own modal window, so the owner must be the active window.
    private static Window? ActiveWindow() =>
        Application.Current?.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive)
        ?? Application.Current?.MainWindow;
}
