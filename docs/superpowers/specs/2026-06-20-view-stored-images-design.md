# View Embedded Images in SRR Stored Files — Design

**Date:** 2026-06-20
**Status:** Approved (brainstorming)

## Goal

Let users open embedded image files (JPG/JPEG, PNG, GIF, BMP) that are stored
inside an SRR file in a resizable popup preview window. Available from two
surfaces: the **Inspector** tab and the **Edit-SRR wizard's** stored-files panel.

## Background

An SRR stores arbitrary files (NFO, SFV, M3U, proof JPGs, …) as
`SRRStoredFileBlock`s. Each block carries `FileName`, `FileLength`, and
`DataOffset` (the byte offset of its data within the SRR). The library already
exposes `SRRFile.ExtractStoredFile(...)`, which writes a stored file to disk by
name. Today the only way to look at an embedded proof image is to extract it and
open it in an external viewer.

The app surfaces stored files in two places:

- **Inspector** (`InspectorViewModel` / `InspectorView`): a tree of blocks. When
  a stored-file node is selected, `_loadedFilePathInternal` holds the SRR path
  and `SelectedTreeNode.Tag` is the `SRRStoredFileBlock`. A Properties grid and
  Hex view fill the right-hand panel.
- **Edit-SRR wizard** (`SrrEditorViewModel` / `StoredFilesManagePanel`): a grid
  of `StoredFileInfo(Name, Size)` rows. Edits run against a temp **working copy**
  at `_workingCopyPath`; the row model has no offset, only a name.

There is **no DI container** — services are constructed manually in
`MainWindowViewModel`'s parameterless constructor and faked in unit tests. UI
dialogs go through `IFileDialogService` so view-models stay testable.

## Decisions

- **Formats:** JPG/JPEG, PNG, GIF, BMP — all decode natively via WPF imaging at
  no extra cost.
- **Surfacing:** a **"View Image" button**, not a context-menu item.
  - Inspector: docked right in the **Properties** panel header bar, visible only
    when the selected stored file is a supported image. Tree-node double-click is
    a secondary convenience that runs the same command.
  - Edit-SRR wizard: a **"Preview…"** button in the stored-files toolbar, enabled
    only for a single selected image. Grid double-click runs the same command.
- **Popup:** modal (`ShowDialog`), resizable, dark title bar (matches
  `AboutWindow`). Image shown fit-to-window (`Stretch=Uniform`) inside a
  `ScrollViewer`; initial size fits the image, capped to the working screen area.
  A thin status bar shows `filename • WIDTH×HEIGHT • size`.
- **MVP exclusions (YAGNI):** no zoom controls; preview is read-only (no
  "save as" — Extract already exists on both surfaces).
- **Architecture:** image bytes are read through the service layer; **no WPF
  imaging types leak into view-models**. Decoding lives behind an
  `IImagePreviewService` abstraction (same pattern as `IFileDialogService`), so
  view-models are unit-testable with a fake.

## Components

### 1. Library — `ReScene.Lib`

Add an in-memory counterpart to `ExtractStoredFile` on `SRRFile`
(`ReScene.Lib/ReScene/SRR/SRRFile.cs`):

```csharp
/// <summary>
/// Reads the first stored file whose name matches <paramref name="match"/> into
/// memory and returns its bytes, or <see langword="null"/> if no match is found.
/// </summary>
public byte[]? ReadStoredFile(string srrFilePath, Func<string, bool> match)
```

Behaviour mirrors `ExtractStoredFile`:

- Validates `srrFilePath` is non-empty and `match` is non-null.
- Finds the first `SRRStoredFileBlock` whose `FileName` matches.
- Returns `null` when no block matches.
- Reads `FileLength` bytes from `DataOffset` using the same bounds checks
  (`InvalidDataException` when the offset/length fall outside the SRR), reusing
  `StreamUtilities.CopyBytesStrict` / an equivalent strict read into a buffer.

This is the single tested place where embedded image bytes come from; the app
never re-implements offset slicing.

### 2. App service layer — `ReScene.NET`

**Extend `ISrrEditingService`** (`Services/ISrrEditingService.cs` +
`Services/SRREditingService.cs`):

```csharp
Task<byte[]?> ReadStoredFileBytesAsync(
    string srrFilePath, string storedName, CancellationToken ct = default);
```

Implementation routes to the new lib method via `Task.Run`, matching the existing
`ExtractStoredFileAsync` shape:

```csharp
public Task<byte[]?> ReadStoredFileBytesAsync(string srrFilePath, string storedName, CancellationToken ct = default)
    => Task.Run(() => SRRFile.Load(srrFilePath).ReadStoredFile(srrFilePath, name => name == storedName), ct);
```

Async because proof images can be a few MB; reading off the UI thread keeps the
window responsive.

**New `Helpers/ImagePreviewSupport.cs` (pure, no WPF):**

```csharp
public static class ImagePreviewSupport
{
    // .jpg .jpeg .png .gif .bmp
    public static bool IsSupported(string fileName);
}
```

Case-insensitive extension check. Pure and fully unit-tested.

**New `IImagePreviewService` / `ImagePreviewService` (`Services/`):**

```csharp
public interface IImagePreviewService
{
    /// <summary>Decodes the bytes and shows them in a modal preview window.
    /// On decode failure, shows an error dialog and does not open a window.</summary>
    void Preview(byte[] data, string fileName);
}
```

`ImagePreviewService` takes an `IFileDialogService` (for the error path). `Preview`:

1. Decodes via `BitmapFrame.Create(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad)` over a `MemoryStream(data)`, then `Freeze()` so the stream can be disposed and the image is safe to hand to the window.
2. On any decode exception, calls `IFileDialogService.ShowError("Could not display image", ...)` and returns.
3. On success, creates `ImagePreviewWindow`, sets `Owner` to the active window
   (`Application.Current.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive) ?? Application.Current?.MainWindow`),
   and calls `ShowDialog()`. Active-window (not always `MainWindow`) owner is
   required because the Edit wizard runs in its own modal window.

Registered in `MainWindowViewModel`'s manual wiring and injected into both
affected view-models.

### 3. Preview window — `Views/ImagePreviewWindow.xaml` (+ `.xaml.cs`)

- Constructor `ImagePreviewWindow(ImageSource image, string fileName)` (mirrors
  `AboutWindow`'s constructor-injection style); sets a small DataContext record
  with the image, title text, and status text.
- Enables the dark title bar on `SourceInitialized`:
  `DarkTitleBar.Enable(this)`.
- Layout: `DockPanel` with a bottom status `TextBlock`
  (`filename • {PixelWidth}×{PixelHeight} • {size}`) and a `ScrollViewer`
  (both scrollbars `Auto`) hosting an `Image` with `Stretch="Uniform"` and
  `StretchDirection="DownOnly"`.
- `ResizeMode="CanResize"`, `WindowStartupLocation="CenterOwner"`. Initial
  `Width`/`Height` derived from the image's pixel size, clamped to
  `SystemParameters.WorkArea` (with a small margin) and a sensible minimum.

### 4. Wiring — Inspector

`InspectorViewModel` (`ViewModels/InspectorViewModel.cs`):

- Add constructor parameter `IImagePreviewService imagePreviewService` (store it).
- Add computed `bool IsImagePreviewAvailable` =>
  `IsSRRFileLoaded() && SelectedTreeNode?.Tag is SRRStoredFileBlock b && ImagePreviewSupport.IsSupported(b.FileName)`.
- Raise `OnPropertyChanged(nameof(IsImagePreviewAvailable))` everywhere
  `IsStoredFileSelected` is already raised (in `OnSelectedTreeNodeChanged`,
  `LoadFile`, `CloseFile`) and notify the command's `CanExecute`.
- Add `[RelayCommand(CanExecute = nameof(IsImagePreviewAvailable))] async Task PreviewStoredImageAsync()`:
  reads bytes via `_sRREditingService.ReadStoredFileBytesAsync(_loadedFilePathInternal!, block.FileName)`;
  if non-null, calls `_imagePreviewService.Preview(bytes, block.FileName)`; on
  exception sets `StatusMessage`.

`InspectorView.xaml`:

- Change the **Properties** panel header `Border` from a bare `TextBlock` to a
  `DockPanel` (same as the Verify/Hex headers) with a right-docked
  `Button Content="View Image"` (`GhostButton`), bound to
  `PreviewStoredImageCommand`, `Visibility` bound to `IsImagePreviewAvailable`
  via `BoolToVisibility`.

`InspectorView.xaml.cs`:

- Add a `TreeView` double-click handler (`MouseDoubleClick`) that, when
  `vm.PreviewStoredImageCommand.CanExecute(null)`, executes it. (Secondary
  convenience; the button is the primary affordance.)

### 5. Wiring — Edit-SRR wizard

`SrrEditorViewModel` (`ViewModels/SrrEditorViewModel.cs`):

- Add constructor parameter `IImagePreviewService imagePreviewService`.
- Add `bool HasSingleImageSelection()` =>
  `SelectedStoredFiles.Count == 1 && ImagePreviewSupport.IsSupported(SelectedStoredFiles[0].Name)`.
- Notify `PreviewStoredImageCommand.NotifyCanExecuteChanged()` inside
  `SetSelection` alongside the other commands.
- Add `[RelayCommand(CanExecute = nameof(HasSingleImageSelection))] async Task PreviewStoredImageAsync()`:
  reads bytes via `_srrEditing.ReadStoredFileBytesAsync(_workingCopyPath!, name)`,
  then `_imagePreviewService.Preview(bytes, name)`; logs on failure via `Log(...)`.

`StoredFilesManagePanel.xaml`:

- Add a `Button Content="Preview…"` (`GhostButton`) to the toolbar `StackPanel`,
  bound to `PreviewStoredImageCommand`.

`StoredFilesManagePanel.xaml.cs`:

- Add a grid `MouseDoubleClick` handler that runs `PreviewStoredImageCommand` when
  it can execute.

### 6. Manual wiring & test factories

- `MainWindowViewModel`: construct one `ImagePreviewService(fileDialog)` and pass
  it to both `InspectorViewModel` and every `SrrEditorViewModel` instance
  (Advanced/Beginner). Update both constructor signatures.
- Test view-model factories (`InspectorViewModelMkvTests`,
  `SrrEditorViewModelTests`) get a `FakeImagePreviewService` recording
  `(data, fileName)` calls.

## Data flow

```
User selects stored image  ──▶ VM.IsImagePreviewAvailable / HasSingleImageSelection ▶ button enabled/visible
User clicks "View Image" / double-clicks
        │
        ▼
VM.PreviewStoredImageAsync
        │  ReadStoredFileBytesAsync(srrPath|workingCopy, name)
        ▼
ISrrEditingService ──▶ SRRFile.ReadStoredFile (offset/length slice, bounds-checked) ──▶ byte[]
        │
        ▼
IImagePreviewService.Preview(bytes, name)
        │  decode → BitmapFrame (OnLoad, Frozen)
        ├─ failure ─▶ IFileDialogService.ShowError("Could not display image")
        └─ success ─▶ new ImagePreviewWindow(image, name).ShowDialog()  (Owner = active window)
```

## Error handling / edge cases

- **Unsupported extension:** button hidden (Inspector) / disabled (wizard); the
  command's `CanExecute` is false.
- **Image extension but corrupt/undecodable bytes:** decode throws →
  `ShowError("Could not display image", …)`; no window opens.
- **No matching stored block / read returns null:** command sets a status/log
  message; no window.
- **Out-of-bounds offset/length:** `ReadStoredFile` throws `InvalidDataException`
  (same guard as `ExtractStoredFile`); surfaced as a status/log message.
- **Very large image:** initial window size clamped to the work area; scrollbars
  handle overflow.

## Testing

**Library (`ReScene.Tests`):**
- `ReadStoredFile` returns the exact bytes of a stored file (round-trip against a
  synthetic SRR via `SRRTestDataBuilder`).
- Returns `null` when no name matches.
- Throws `InvalidDataException` on an offset/length outside the SRR (parity with
  `ExtractStoredFile`).

**App — pure (`ReScene.NET.Tests`):**
- `ImagePreviewSupport.IsSupported` truth table: `.jpg/.jpeg/.png/.gif/.bmp`
  (incl. uppercase) → true; `.nfo/.sfv/.txt`, no extension, empty string → false.

**App — view-models (`ReScene.NET.Tests`, fakes only, no WPF):**
- Inspector: after loading an SRR and selecting an image stored block,
  `IsImagePreviewAvailable` is true and `PreviewStoredImageCommand.CanExecute`;
  for a non-image block it is false. Executing the command calls
  `FakeImagePreviewService.Preview` with the right filename and the bytes from the
  fake editing service.
- SrrEditor: `PreviewStoredImageCommand` is enabled only when exactly one image
  row is selected; executing forwards the right filename/bytes to the fake
  preview service.

The `ImagePreviewService` and `ImagePreviewWindow` are **not** unit-tested
(consistent with the untested About/Settings/Prompt windows).

## Files

**Create**
- `ReScene.NET/Helpers/ImagePreviewSupport.cs`
- `ReScene.NET/Services/IImagePreviewService.cs`
- `ReScene.NET/Services/ImagePreviewService.cs`
- `ReScene.NET/Views/ImagePreviewWindow.xaml` (+ `.xaml.cs`)

**Modify**
- `ReScene.Lib/ReScene/SRR/SRRFile.cs` (add `ReadStoredFile`)
- `ReScene.NET/Services/ISrrEditingService.cs` + `SRREditingService.cs`
- `ReScene.NET/ViewModels/InspectorViewModel.cs`
- `ReScene.NET/Views/InspectorView.xaml` + `InspectorView.xaml.cs`
- `ReScene.NET/ViewModels/SrrEditorViewModel.cs`
- `ReScene.NET/Views/Wizards/StoredFilesManagePanel.xaml` + `.xaml.cs`
- `ReScene.NET/ViewModels/MainWindowViewModel.cs` (wiring)

**Tests**
- `ReScene.Lib/ReScene.Tests/` — `ReadStoredFile` coverage
- `ReScene.NET.Tests/` — `ImagePreviewSupport` truth table; Inspector & SrrEditor
  preview-command tests; `FakeImagePreviewService`

## Out of scope

- Zoom / pan controls in the preview window.
- "Save as" from the preview (use existing Extract).
- Non-image stored-file previewers (text/NFO viewers, etc.).
- Create-SRR wizard surface (excluded per scope decision; sources still live on
  disk there).
