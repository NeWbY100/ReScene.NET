# Tabbed File-Preview Dialog (Edit-SRR Wizard) — Design

**Date:** 2026-06-21
**Status:** Approved (brainstorming)

## Goal

The Edit-an-SRR wizard's **"Preview…"** button should work for *any* single
selected stored file (not just images) and open a popup with **Hex**, **Text**,
and — for images — **Image** tabs, so any embedded file can be previewed without
extracting it.

## Background

The wizard's stored-files panel (`Views/Wizards/StoredFilesManagePanel.xaml`,
DataContext = `SrrEditorViewModel`) has a **"Preview…"** button and a grid
double-click that currently run `PreviewStoredImageCommand`, gated by
`HasSingleImageSelection` (a single *image* selection). That command reads the
stored file's bytes (`ISrrEditingService.ReadStoredFileBytesAsync`) and calls
`IImagePreviewService.Preview(byte[] data, string fileName)`, which decodes the
image and shows the image-only `ImagePreviewWindow`.

The Inspector tab separately has its own image-only "View Image" button (also via
`IImagePreviewService`) and its own in-panel Hex/Text tabs.

Reusable pieces already exist:
- `Controls/HexViewControl` — renders an `IHexDataSource` (`ReScene.Hex.IHexDataSource`:
  `long Length` + `int Read(long position, byte[] buffer, int offset, int count)`),
  given `BlockOffset`/`BlockLength`/`BytesPerLine`.
- `Helpers/TextDecoder.Decode(IHexDataSource?, long length, Encoding, int maxBytes)`
  → `(string Text, bool Truncated)`.
- `Helpers/TextEncodingOptions.All` (UTF-8 first) + `TextEncodingOption` record
  (`ToString()` returns `DisplayName`).
- `Helpers/ImagePreviewSupport.IsSupported(fileName)` (.jpg/.jpeg/.png/.gif/.bmp).
- The themed `TabControl`/`TabItem` styles in `App.xaml`.
- `Helpers/FormatUtilities.FormatSize`, `Helpers/DarkTitleBar.Enable`.
- `ImagePreviewService` currently decodes images inline via
  `BitmapFrame.Create(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad)`
  + `Freeze()`.

There is **no DI container** — services are constructed in `MainWindowViewModel`
and faked in tests. UI dialogs go through abstractions (e.g. `IFileDialogService`,
`IImagePreviewService`) so view-models stay testable.

## Decisions (from brainstorming)

- **Scope: Edit-SRR wizard only.** The Inspector keeps its image-only "View Image"
  button and `IImagePreviewService` unchanged.
- **Preview is ungated** — enabled for any single selection (multi-select stays
  disabled).
- **Tabs:** Hex (always), Text (always), Image (only when the file is a supported,
  decodable image). **Default tab: Hex, always** (even for images).
- **Image tab is collapsed** for non-images / undecodable images (not shown-disabled).
- **Shared image decode:** extract a single `ImageDecoder.TryDecode` helper used by
  both the new `FilePreviewService` and the existing `ImagePreviewService`.
- No library/public-API change.

## Components (app-only — `ReScene.NET`)

### 1. `Services/ByteArrayDataSource.cs`

A production `IHexDataSource` over an in-memory buffer, so `HexViewControl` and
`TextDecoder` can read the preview file's bytes:

```csharp
public sealed class ByteArrayDataSource(byte[] data) : IHexDataSource
{
    public long Length => data.Length;
    public int Read(long position, byte[] buffer, int offset, int count); // clamped copy
    public void Dispose() { }
}
```

### 2. `Helpers/ImageDecoder.cs`

```csharp
public static class ImageDecoder
{
    /// <summary>Decodes image bytes to a frozen BitmapSource, or null if they are not a decodable image.</summary>
    public static BitmapSource? TryDecode(byte[] data);
}
```

Implementation = the existing decode (`BitmapFrame.Create(..., OnLoad)` + `Freeze()`)
wrapped in the established `catch (… NotSupportedException or ArgumentException or
FileFormatException or IOException or OverflowException)` returning `null`.

**`ImagePreviewService` is refactored** to call `ImageDecoder.TryDecode`; on `null`
it keeps its current behaviour (`IFileDialogService.ShowError("Could not display
image", …)` and returns) — no behaviour change for the Inspector path.

### 3. `Services/IFilePreviewService.cs` + `FilePreviewService.cs`

```csharp
public interface IFilePreviewService
{
    /// <summary>Shows a tabbed (Hex/Text/Image) preview of the file's bytes.</summary>
    public void Preview(byte[] data, string fileName);
}
```

`FilePreviewService.Preview`:
1. `BitmapSource? image = ImagePreviewSupport.IsSupported(fileName) ? ImageDecoder.TryDecode(data) : null;`
2. `var window = new FilePreviewWindow(new FilePreviewViewModel(data, fileName, image)) { Owner = ActiveWindow() };`
3. `window.ShowDialog();`

`ActiveWindow()` = the active window, falling back to `MainWindow` (same helper
pattern as `ImagePreviewService`, because the wizard runs in its own modal window).

### 4. `ViewModels/FilePreviewViewModel.cs`

Constructed with `(byte[] data, string fileName, BitmapSource? image)` — the image
is decoded by the service and passed in, so the view-model holds no WPF-decode logic
and is unit-testable.

State:
- `IHexDataSource HexDataSource` = `new ByteArrayDataSource(data)`; `long HexBlockLength = data.Length`; `long HexBlockOffset = 0`; `[ObservableProperty] int HexBytesPerLine = 16`.
- `IReadOnlyList<TextEncodingOption> TextEncodings = TextEncodingOptions.All`; `[ObservableProperty] TextEncodingOption SelectedEncoding = TextEncodingOptions.All[0]`; `[ObservableProperty] bool TextWordWrap`; `[ObservableProperty] string TextViewContent`; `[ObservableProperty] bool TextViewTruncated`. `OnSelectedEncodingChanged` recomputes the text. Text is decoded eagerly in the constructor (the buffer is already in memory) and on encoding change, via `TextDecoder.Decode(HexDataSource, HexBlockLength, SelectedEncoding.Encoding, 1 MB)`.
- `BitmapSource? Image = image`; `bool HasImageTab => Image is not null`.
- `string TitleText` = `"Preview — {fileName}"`; `string StatusText` = `"{fileName}  •  {FormatUtilities.FormatSize(data.Length)}"` (plus `• W×H` when an image).
- Default selected tab = Hex (the XAML lists Hex first and selects it by default; no special wiring needed).

### 5. `Views/FilePreviewWindow.xaml` (+ `.xaml.cs`)

- Resizable `Window`, dark title bar on `SourceInitialized`, `WindowBackground` /
  `ForegroundPrimary` / `UIFontFamily` / `ShowInTaskbar="False"` (matching the other
  windows), `Title="{Binding TitleText}"`, a bottom status bar bound to `StatusText`.
- A themed `TabControl` (Hex first, so it is the default), each tab's content:
  - **Hex:** a small top toolbar with a Bytes/Row `ComboBox` (bound to `HexBytesPerLine`,
    items 8/16/24/32/48/64) + the `HexViewControl` (`DataSource`/`BlockOffset`/
    `BlockLength`/`BytesPerLine` bound).
  - **Text:** Encoding `ComboBox` (`TextEncodings`/`SelectedEncoding`,
    `DisplayMemberPath="DisplayName"`) + Word-wrap `CheckBox` + read-only monospace
    `TextBox` (`TextViewContent`, `TextWrapping` via a `TextWordWrap` `DataTrigger`,
    `BorderThickness=0`, scrollbars Auto) + a truncation note `TextBlock` shown when
    `TextViewTruncated`.
  - **Image:** a `ScrollViewer` hosting an `Image` (`Source="{Binding Image}"`,
    `Stretch="Uniform"`, `StretchDirection="DownOnly"`). The whole **Image `TabItem`'s
    `Visibility`** is bound to `HasImageTab` via `BoolToVisibility` (collapsed when no image).
- Initial window size ~880×620, clamped to the working area with minimums (e.g.
  Min 360×240).

### 6. Edit-SRR wiring

`ViewModels/SrrEditorViewModel.cs`:
- Constructor param `IImagePreviewService imagePreview` → `IFilePreviewService filePreview`
  (field `_filePreview`).
- Remove `HasSingleImageSelection`; the preview command's `CanExecute` becomes the
  existing `HasSingleSelection` (any single selection).
- Rename `PreviewStoredImageAsync`/`PreviewStoredImageCommand` →
  `PreviewStoredFileAsync`/`PreviewStoredFileCommand`. Body unchanged except it calls
  `_filePreview.Preview(bytes, name)`.
- `SetSelection` notifies `PreviewStoredFileCommand.NotifyCanExecuteChanged()`.

`Views/Wizards/StoredFilesManagePanel.xaml` + `.xaml.cs`: the "Preview…" button and
the grid double-click bind to / invoke `PreviewStoredFileCommand`.

`ViewModels/MainWindowViewModel.cs`: construct `new FilePreviewService(...)` and pass
it to the `SrrEditorViewModel`. Keep `ImagePreviewService` for the Inspector.

## Data flow

```
select one stored file → HasSingleSelection true → Preview… enabled
click Preview… / double-click row
        │  ReadStoredFileBytesAsync(workingCopy, name) → byte[]
        ▼
IFilePreviewService.Preview(bytes, name)
        │  image = IsSupported(name) ? ImageDecoder.TryDecode(bytes) : null
        ▼
new FilePreviewWindow(new FilePreviewViewModel(bytes, name, image)).ShowDialog()
        ├─ Hex tab  : HexViewControl over ByteArrayDataSource(bytes)   [default]
        ├─ Text tab : TextDecoder.Decode(bytes, encoding, 1 MB)
        └─ Image tab: shown only when image != null
```

## Error handling / edge cases

- **Non-image / undecodable image:** `image == null` → Image tab collapsed; Hex/Text
  work. No error dialog (the file is still previewable).
- **Read returns null** (no matching stored file): the wizard logs "Could not read …"
  and does not open the window (same as today).
- **Text >1 MB:** decoded up to the cap; `TextViewTruncated` shows the note.
- **Invalid bytes for the chosen encoding:** replacement chars, never throws.

## Testing

**`ByteArrayDataSourceTests` (`ReScene.NET.Tests`):**
- `Read` returns the right bytes / count; clamps at the end; `Length` is the buffer length.

**`FilePreviewViewModelTests` (`ReScene.NET.Tests`):**
- Non-image (`image: null`): `HasImageTab` false; `TextViewContent` decodes the bytes;
  `HexBlockLength == data.Length`; default encoding is UTF-8.
- Image (pass a dummy frozen `BitmapSource` via `BitmapSource.Create`): `HasImageTab` true.
- Changing `SelectedEncoding` re-decodes `TextViewContent` (CP437 vs Latin-1 on a 0xC9
  byte, like the existing Inspector test).

**`SrrEditorViewModelTests` (update):**
- The preview command is enabled for a single selection of **any** file (the existing
  "non-image disabled" expectation is replaced with "non-image enabled"); disabled for
  multi-select; forwards the right bytes + name to a fake `IFilePreviewService`
  (new `RecordingFilePreviewService` test double).

`ImageDecoder`, `FilePreviewService`, and `FilePreviewWindow` are **not** unit-tested
(WPF, consistent with the existing windows/services).

## Files

**Create**
- `ReScene.NET/Services/ByteArrayDataSource.cs`
- `ReScene.NET/Helpers/ImageDecoder.cs`
- `ReScene.NET/Services/IFilePreviewService.cs`
- `ReScene.NET/Services/FilePreviewService.cs`
- `ReScene.NET/ViewModels/FilePreviewViewModel.cs`
- `ReScene.NET/Views/FilePreviewWindow.xaml` (+ `.xaml.cs`)
- `ReScene.NET.Tests/ByteArrayDataSourceTests.cs`
- `ReScene.NET.Tests/FilePreviewViewModelTests.cs`

**Modify**
- `ReScene.NET/Services/ImagePreviewService.cs` (use `ImageDecoder.TryDecode`)
- `ReScene.NET/ViewModels/SrrEditorViewModel.cs`
- `ReScene.NET/Views/Wizards/StoredFilesManagePanel.xaml` + `.xaml.cs`
- `ReScene.NET/ViewModels/MainWindowViewModel.cs`
- `ReScene.NET.Tests/SrrEditorViewModelTests.cs` (gating + fake service)
- `ReScene.NET.Tests/TestDoubles.cs` (add `RecordingFilePreviewService`)

## Out of scope (YAGNI)

- Hex search within the preview popup (the Inspector has it).
- Bringing the tabbed preview to the Inspector (kept image-only).
- Editing, encoding persistence across sessions, auto-encoding-detection.
- A custom virtualized text renderer (the 1 MB TextBox cap covers stored files).
