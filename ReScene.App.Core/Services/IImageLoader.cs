namespace ReScene.App.Core.Services;

/// <summary>
/// Loads an image into a platform-native object (WPF <c>BitmapSource</c> / later Avalonia
/// <c>Bitmap</c>). The value is opaque to App.Core and is bound directly to an Image control's
/// Source by the View.
/// </summary>
public interface IImageLoader
{
    /// <summary>Loads the image at <paramref name="path"/>, or <see langword="null"/> if it is not a decodable image.</summary>
    public object? Load(string path);

    /// <summary>Loads the image from <paramref name="stream"/>, or <see langword="null"/> if it is not a decodable image.</summary>
    public object? Load(Stream stream);
}
