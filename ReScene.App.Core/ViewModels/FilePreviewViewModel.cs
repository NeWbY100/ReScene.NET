using CommunityToolkit.Mvvm.ComponentModel;
using ReScene.App.Core.Helpers;
using ReScene.App.Core.Services;
using ReScene.Hex;
namespace ReScene.App.Core.ViewModels;

/// <summary>
/// Drives the tabbed file-preview window: a Hex view over the file's bytes, a Text view with a
/// selectable encoding, and (when the bytes decode as an image) an Image view. The image is decoded
/// by the caller (via <c>IImageLoader</c>) and passed in as an opaque platform-native object, so
/// this view-model holds no WPF-decode logic and is unit-testable.
/// </summary>
public partial class FilePreviewViewModel : ViewModelBase
{
    private const int TextViewMaxBytes = 1024 * 1024; // 1 MB

    /// <param name="data">The file's raw bytes, for the Hex view and text decoder.</param>
    /// <param name="fileName">The file's name, shown in the title and status line.</param>
    /// <param name="image">
    /// The decoded, platform-native image (WPF <c>BitmapSource</c>), or <see langword="null"/> when
    /// the file is not a decodable image. Bound directly onto the View's <c>Image.Source</c>.
    /// </param>
    /// <param name="imageWidth">Pixel width of <paramref name="image"/> (for the status line); ignored when null.</param>
    /// <param name="imageHeight">Pixel height of <paramref name="image"/> (for the status line); ignored when null.</param>
    public FilePreviewViewModel(byte[] data, string fileName, object? image, int? imageWidth = null, int? imageHeight = null)
    {
        ArgumentNullException.ThrowIfNull(data);

        HexDataSource = new ByteArrayDataSource(data);
        HexBlockLength = data.Length;
        Image = image;

        string size = FormatUtilities.FormatSize(data.Length);
        TitleText = $"Preview — {fileName}";
        StatusText = image is not null
            ? $"{fileName}  •  {imageWidth}×{imageHeight}  •  {size}"
            : $"{fileName}  •  {size}";

        UpdateTextView();
    }

    /// <summary>The file's bytes, for the Hex view and text decoder.</summary>
    public IHexDataSource HexDataSource { get; }

    public long HexBlockLength { get; }

    [ObservableProperty]
    public partial int HexBytesPerLine { get; set; } = 16;

    public IReadOnlyList<TextEncodingOption> TextEncodings { get; } = TextEncodingOptions.All;

    [ObservableProperty]
    public partial TextEncodingOption SelectedEncoding { get; set; } = TextEncodingOptions.All[0];

    [ObservableProperty]
    public partial bool TextWordWrap { get; set; }

    [ObservableProperty]
    public partial string TextViewContent { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool TextViewTruncated { get; set; }

    /// <summary>
    /// The decoded, platform-native image (opaque to App.Core), or <see langword="null"/> when the
    /// file is not a decodable image. Bound straight onto the View's <c>Image.Source</c>.
    /// </summary>
    public object? Image { get; }

    public bool HasImageTab => Image is not null;

    public string TitleText { get; }

    public string StatusText { get; }

    partial void OnSelectedEncodingChanged(TextEncodingOption value) => UpdateTextView();

    private void UpdateTextView()
    {
        (TextViewContent, TextViewTruncated) = TextDecoder.Decode(
            HexDataSource, HexBlockLength, SelectedEncoding.Encoding, TextViewMaxBytes);
    }
}
