using System.Windows;
using System.Windows.Media.Imaging;
using ReScene.NET.Views;

namespace ReScene.NET.Services;

/// <summary>
/// Decodes image bytes via WPF imaging and shows them in an <see cref="ImagePreviewWindow"/>.
/// Decode failures are reported through <see cref="IFileDialogService.ShowError"/>.
/// </summary>
public class ImagePreviewService(IFileDialogService fileDialog) : IImagePreviewService
{
    private readonly IFileDialogService _fileDialog = fileDialog;

    /// <inheritdoc />
    public void Preview(byte[] data, string fileName)
    {
        BitmapSource image;
        try
        {
            using var stream = new MemoryStream(data);
            BitmapFrame frame = BitmapFrame.Create(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
            frame.Freeze();
            image = frame;
        }
        catch (Exception ex) when (ex is NotSupportedException or ArgumentException or FileFormatException or IOException or OverflowException)
        {
            _fileDialog.ShowError("Could not display image",
                $"\"{fileName}\" could not be decoded as an image.");
            return;
        }

        var window = new ImagePreviewWindow(image, fileName, data.Length)
        {
            Owner = ActiveWindow()
        };
        window.ShowDialog();
    }

    // The Edit-SRR wizard runs in its own modal window, so the owner must be the active
    // window — not always Application.Current.MainWindow.
    private static Window? ActiveWindow() =>
        Application.Current?.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive)
        ?? Application.Current?.MainWindow;
}
