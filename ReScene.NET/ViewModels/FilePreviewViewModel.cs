using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using ReScene.Hex;
using ReScene.NET.Helpers;
using ReScene.NET.Services;

namespace ReScene.NET.ViewModels;

/// <summary>
/// Drives the tabbed file-preview window: a Hex view over the file's bytes, a Text view with a
/// selectable encoding, and (when the bytes decode as an image) an Image view. The image is decoded
/// by the caller and passed in, so this view-model holds no WPF-decode logic and is unit-testable.
/// </summary>
public partial class FilePreviewViewModel : ViewModelBase
{
    private const int TextViewMaxBytes = 1024 * 1024; // 1 MB

    public FilePreviewViewModel(byte[] data, string fileName, BitmapSource? image)
    {
        ArgumentNullException.ThrowIfNull(data);

        HexDataSource = new ByteArrayDataSource(data);
        HexBlockLength = data.Length;
        Image = image;

        string size = FormatUtilities.FormatSize(data.Length);
        TitleText = $"Preview — {fileName}";
        StatusText = image is not null
            ? $"{fileName}  •  {image.PixelWidth}×{image.PixelHeight}  •  {size}"
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

    /// <summary>The decoded image, or <see langword="null"/> when the file is not a decodable image.</summary>
    public BitmapSource? Image { get; }

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
