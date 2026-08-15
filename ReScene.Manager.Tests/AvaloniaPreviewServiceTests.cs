using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using ReScene.App.Core.Services;
using ReScene.Manager.Services;
using ReScene.Manager.Views;

namespace ReScene.Manager.Tests;

/// <summary>
/// Unit tests for the decode-decision branches of <see cref="AvaloniaFilePreviewService"/> and
/// <see cref="AvaloniaImagePreviewService"/>. The owner resolver returns <see langword="null"/> so no
/// live window is required (the services fall back to <c>Show()</c>); the actual popup is the
/// controller's Phase-4 launch-smoke. Fakes record which collaborator each branch invoked.
/// </summary>
public class AvaloniaPreviewServiceTests
{
    private sealed class RecordingImageLoader : IImageLoader
    {
        public int StreamLoadCount { get; private set; }
        public object? StreamResult { get; init; }

        public object? Load(string path) => null;

        public object? Load(Stream stream)
        {
            StreamLoadCount++;
            return StreamResult;
        }
    }

    private sealed class RecordingFileDialog : IFileDialogService
    {
        public (string Title, string Message)? LastError { get; private set; }

        public void ShowError(string title, string message) => LastError = (title, message);

        public void ShowWarning(string title, string message) { }
        public void ShowInfo(string title, string message) { }
        public bool Confirm(string title, string message) => false;
        public Task<string?> OpenFileAsync(string title, IReadOnlyList<string> filters, string? initialPath = null) => Task.FromResult<string?>(null);
        public Task<IReadOnlyList<string>> OpenFilesAsync(string title, IReadOnlyList<string> filters, string? initialPath = null) => Task.FromResult<IReadOnlyList<string>>([]);
        public Task<string?> SaveFileAsync(string title, string defaultExtension, IReadOnlyList<string> filters, string? defaultFileName = null) => Task.FromResult<string?>(null);
        public Task<string?> OpenFolderAsync(string title, string? initialPath = null) => Task.FromResult<string?>(null);
        public Task<bool> ShowConfirmAsync(string title, string message) => Task.FromResult(false);
        public Task<string?> PromptForTextAsync(string title, string message, string initialValue) => Task.FromResult<string?>(null);
    }

    // ── AvaloniaFilePreviewService ─────────────────────────────────────────

    [AvaloniaFact]
    public void FilePreview_UnsupportedExtension_SkipsImageDecode()
    {
        var loader = new RecordingImageLoader();
        var service = new AvaloniaFilePreviewService(loader, static () => null);

        service.Preview([1, 2, 3, 4], "notes.txt");

        Assert.Equal(0, loader.StreamLoadCount); // ImagePreviewSupport gated the decode out
    }

    [AvaloniaFact]
    public void FilePreview_SupportedExtension_DecodesImage()
    {
        Bitmap image = ImageTestData.CreateBitmap(6, 4);
        var loader = new RecordingImageLoader { StreamResult = image };
        var service = new AvaloniaFilePreviewService(loader, static () => null);

        service.Preview(ImageTestData.CreatePngBytes(6, 4), "poster.png");

        Assert.Equal(1, loader.StreamLoadCount); // supported extension → decode attempted
    }

    // ── AvaloniaImagePreviewService ────────────────────────────────────────

    [AvaloniaFact]
    public void ImagePreview_DecodeFailure_ShowsErrorAndOpensNothing()
    {
        var loader = new RecordingImageLoader { StreamResult = null }; // undecodable
        var dialog = new RecordingFileDialog();
        var service = new AvaloniaImagePreviewService(loader, dialog, static () => null);

        service.Preview([0xDE, 0xAD, 0xBE, 0xEF], "broken.png");

        Assert.Equal(1, loader.StreamLoadCount);
        Assert.NotNull(dialog.LastError);
        Assert.Equal("Could not display image", dialog.LastError.Value.Title);
        Assert.Contains("broken.png", dialog.LastError.Value.Message, StringComparison.Ordinal);
    }

    [AvaloniaFact]
    public void ImagePreview_Decodes_ShowsWindow_NoError()
    {
        Bitmap image = ImageTestData.CreateBitmap(8, 8);
        var loader = new RecordingImageLoader { StreamResult = image };
        var dialog = new RecordingFileDialog();
        var service = new AvaloniaImagePreviewService(loader, dialog, static () => null);

        service.Preview(ImageTestData.CreatePngBytes(8, 8), "cover.png");

        Assert.Equal(1, loader.StreamLoadCount);
        Assert.Null(dialog.LastError); // success path never reports an error
    }

    // ── Modal path (F4): with a visible owner the preview opens as an owned modal dialog ──
    // ShowDialog(owner) requires a visible owner (it throws otherwise), so these tests also prove the
    // service takes the modal branch rather than the null-owner Show() fallback above.

    [AvaloniaFact]
    public void FilePreview_WithVisibleOwner_OpensOwnedModalPreview()
    {
        var owner = new Window();
        owner.Show();
        Dispatcher.UIThread.RunJobs();

        Bitmap image = ImageTestData.CreateBitmap(6, 4);
        var loader = new RecordingImageLoader { StreamResult = image };
        var service = new AvaloniaFilePreviewService(loader, () => owner);

        service.Preview(ImageTestData.CreatePngBytes(6, 4), "poster.png");
        Dispatcher.UIThread.RunJobs();

        Assert.Single(owner.OwnedWindows.OfType<FilePreviewWindow>());
    }

    [AvaloniaFact]
    public void ImagePreview_WithVisibleOwner_OpensOwnedModalPreview()
    {
        var owner = new Window();
        owner.Show();
        Dispatcher.UIThread.RunJobs();

        Bitmap image = ImageTestData.CreateBitmap(8, 8);
        var loader = new RecordingImageLoader { StreamResult = image };
        var dialog = new RecordingFileDialog();
        var service = new AvaloniaImagePreviewService(loader, dialog, () => owner);

        service.Preview(ImageTestData.CreatePngBytes(8, 8), "cover.png");
        Dispatcher.UIThread.RunJobs();

        Assert.Null(dialog.LastError);
        Assert.Single(owner.OwnedWindows.OfType<ImagePreviewWindow>());
    }
}
