# View Embedded Images in SRR Stored Files — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Let users open embedded image files (JPG/JPEG, PNG, GIF, BMP) stored inside an SRR in a resizable popup preview window, from both the Inspector tab and the Edit-SRR wizard.

**Architecture:** A new in-memory `SRRFile.ReadStoredFile` (library) supplies the bytes; an `ISrrEditingService.ReadStoredFileBytesAsync` wraps it; a pure `ImagePreviewSupport` helper gates which files get a "View Image"/"Preview" affordance; an `IImagePreviewService` abstraction decodes the bytes and shows an `ImagePreviewWindow`. View-models orchestrate (read → preview) and never touch WPF imaging types, so they stay unit-testable behind a fake preview service.

**Tech Stack:** .NET 10, WPF, CommunityToolkit.Mvvm (`[ObservableProperty]`/`[RelayCommand]`), xUnit. Library multitargets `net8.0;net10.0`.

**Spec:** `docs/superpowers/specs/2026-06-20-view-stored-images-design.md`

---

## CRITICAL build/test constraint (read before any `dotnet` command)

The user keeps the app running, which **locks `bin/`**. Therefore:

- **ALWAYS** build and test with `-p:BaseOutputPath=bin2/`.
- **NEVER** kill the running app.
- After finishing (final task), delete every `bin2` folder:
  ```bash
  find E:/Projects/ReScene.NET -type d -name bin2 -prune -exec rm -rf {} + 2>/dev/null
  ```

`ReScene.Lib` is a **nested git submodule** at `E:/Projects/ReScene.NET/ReScene.Lib`. Library changes (Task 1) are committed **inside the submodule**, then the parent repo's gitlink is bumped. All other tasks commit in the parent repo. Commit messages end with:

```
Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>
```

Keep the `NeWbY100` gh account active (do not switch accounts).

---

## File map

**Create**
- `ReScene.NET/Helpers/ImagePreviewSupport.cs` — pure extension gate.
- `ReScene.NET/Services/IImagePreviewService.cs` — preview UI abstraction.
- `ReScene.NET/Services/ImagePreviewService.cs` — decodes + shows the window; error dialog on failure.
- `ReScene.NET/Views/ImagePreviewWindow.xaml` (+ `.xaml.cs`) — the popup.

**Modify (library — submodule)**
- `ReScene.Lib/ReScene/SRR/SRRFile.cs` — add `ReadStoredFile`.

**Modify (app — parent)**
- `ReScene.NET/Services/ISrrEditingService.cs` + `Services/SRREditingService.cs` — add `ReadStoredFileBytesAsync`.
- `ReScene.NET/ViewModels/InspectorViewModel.cs` — ctor param, `IsImagePreviewAvailable`, `PreviewStoredImageCommand`.
- `ReScene.NET/Views/InspectorView.xaml` + `InspectorView.xaml.cs` — header button + double-click.
- `ReScene.NET/ViewModels/SrrEditorViewModel.cs` — ctor param, `HasSingleImageSelection`, `PreviewStoredImageCommand`.
- `ReScene.NET/Views/Wizards/StoredFilesManagePanel.xaml` + `.xaml.cs` — toolbar button + double-click.
- `ReScene.NET/ViewModels/MainWindowViewModel.cs` — construct `ImagePreviewService`, pass to the three VMs.

**Tests**
- `ReScene.Lib/ReScene.Tests/SRRFileTests.cs` — `ReadStoredFile` coverage (submodule).
- `ReScene.NET.Tests/TestDoubles.cs` — `RecordingImagePreviewService`.
- `ReScene.NET.Tests/ImagePreviewSupportTests.cs` (new) — truth table.
- `ReScene.NET.Tests/InspectorViewModelImageTests.cs` (new) — Inspector command behavior.
- `ReScene.NET.Tests/SrrEditorViewModelTests.cs` — extend fake + add preview tests.
- `ReScene.NET.Tests/InspectorViewModelMkvTests.cs` — update stub + factory for the new ctor param/interface member.

---

## Task 1: Library — `SRRFile.ReadStoredFile`

In-memory twin of the existing `ExtractStoredFile` (same name-matching and bounds checks), returning bytes instead of writing a file.

**Files:**
- Modify: `ReScene.Lib/ReScene/SRR/SRRFile.cs` (add method after `ExtractStoredFile`, around line 611)
- Test: `ReScene.Lib/ReScene.Tests/SRRFileTests.cs`

- [ ] **Step 1: Write the failing tests**

Append these tests inside the `SRRFileTests` class (it already `: TempDirTestBase` and `using ReScene.SRR;` / `using System.Text;` are present). `SRRTestDataBuilder` is the existing internal synthetic-SRR builder.

```csharp
    #region ReadStoredFile Tests

    [Fact]
    public void ReadStoredFile_ReturnsExactBytes()
    {
        byte[] data = [0x10, 0x20, 0x30, 0x40, 0x50];
        string path = new SRRTestDataBuilder()
            .AddSRRHeader()
            .AddStoredFile("proof.jpg", data)
            .BuildToFile(TempDir, "with_image.srr");

        var srr = SRRFile.Load(path);
        byte[]? bytes = srr.ReadStoredFile(path, name => name == "proof.jpg");

        Assert.NotNull(bytes);
        Assert.Equal(data, bytes);
    }

    [Fact]
    public void ReadStoredFile_NoMatch_ReturnsNull()
    {
        string path = new SRRTestDataBuilder()
            .AddSRRHeader()
            .AddStoredFile("proof.jpg", [0x01, 0x02])
            .BuildToFile(TempDir, "no_match.srr");

        var srr = SRRFile.Load(path);
        byte[]? bytes = srr.ReadStoredFile(path, name => name == "missing.png");

        Assert.Null(bytes);
    }

    [Fact]
    public void ReadStoredFile_EmptyStoredFile_ReturnsEmptyArray()
    {
        string path = new SRRTestDataBuilder()
            .AddSRRHeader()
            .AddStoredFile("empty.bmp", [])
            .BuildToFile(TempDir, "empty_stored.srr");

        var srr = SRRFile.Load(path);
        byte[]? bytes = srr.ReadStoredFile(path, name => name == "empty.bmp");

        Assert.NotNull(bytes);
        Assert.Empty(bytes);
    }

    [Fact]
    public void ReadStoredFile_OffsetBeyondFile_ThrowsInvalidData()
    {
        string path = new SRRTestDataBuilder()
            .AddSRRHeader()
            .AddStoredFile("proof.jpg", [0x01, 0x02, 0x03])
            .BuildToFile(TempDir, "oob.srr");

        var srr = SRRFile.Load(path);
        // Corrupt the parsed block so its data range falls outside the file.
        srr.StoredFiles[0].DataOffset = long.MaxValue / 2;

        Assert.Throws<InvalidDataException>(() =>
            srr.ReadStoredFile(path, name => name == "proof.jpg"));
    }

    #endregion
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test ReScene.Lib/ReScene.Tests/ReScene.Tests.csproj -p:BaseOutputPath=bin2/ --filter "FullyQualifiedName~ReadStoredFile"`
Expected: FAIL to compile — `SRRFile` has no `ReadStoredFile`.

- [ ] **Step 3: Implement `ReadStoredFile`**

In `ReScene.Lib/ReScene/SRR/SRRFile.cs`, add this method immediately after `ExtractStoredFile` (just before the final closing brace of the class). `StreamUtilities` is `internal` in the same assembly and exposes `ReadExactly(BinaryReader, int)`.

```csharp
    /// <summary>
    /// Reads the first stored file whose name matches <paramref name="match"/> into memory
    /// and returns its bytes, or <c>null</c> if no matching file is found. This is the
    /// in-memory counterpart of <see cref="ExtractStoredFile"/>.
    /// </summary>
    /// <param name="srrFilePath">The path to the SRR file containing the stored data.</param>
    /// <param name="match">A predicate matching the desired file by stored name.</param>
    /// <returns>The stored file's raw bytes, or <c>null</c> if no match was found.</returns>
    /// <exception cref="ArgumentException">Thrown when <paramref name="srrFilePath"/> is null or empty.</exception>
    /// <exception cref="InvalidDataException">Thrown when the stored file's data range is outside the SRR file bounds.</exception>
    public byte[]? ReadStoredFile(string srrFilePath, Func<string, bool> match)
    {
        if (string.IsNullOrWhiteSpace(srrFilePath))
        {
            throw new ArgumentException("SRR file path is required.", nameof(srrFilePath));
        }

        ArgumentNullException.ThrowIfNull(match);

        SRRStoredFileBlock? storedFile = null;
        foreach (SRRStoredFileBlock stored in StoredFiles)
        {
            if (match(stored.FileName))
            {
                storedFile = stored;
                break;
            }
        }

        if (storedFile == null)
        {
            return null;
        }

        using FileStream fs = new(srrFilePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        long dataOffset = storedFile.DataOffset;
        long dataLength = storedFile.FileLength;

        if (dataOffset < 0 || dataOffset > fs.Length)
        {
            throw new InvalidDataException("Stored file data offset is outside the SRR file bounds.");
        }

        long dataEnd = dataOffset + dataLength;
        if (dataEnd < dataOffset || dataEnd > fs.Length)
        {
            throw new InvalidDataException("Stored file length exceeds SRR file bounds.");
        }

        fs.Seek(dataOffset, SeekOrigin.Begin);
        using BinaryReader reader = new(fs);
        return StreamUtilities.ReadExactly(reader, (int)dataLength);
    }
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test ReScene.Lib/ReScene.Tests/ReScene.Tests.csproj -p:BaseOutputPath=bin2/ --filter "FullyQualifiedName~ReadStoredFile"`
Expected: PASS (4 tests). Confirm there are **no** `warning CA`/`warning CS` lines in the output (the lib has `AnalysisLevel=latest-All`).

- [ ] **Step 5: Commit (submodule, then bump parent gitlink)**

```bash
cd E:/Projects/ReScene.NET/ReScene.Lib
git add ReScene/SRR/SRRFile.cs ReScene.Tests/SRRFileTests.cs
git commit -m "feat(srr): add SRRFile.ReadStoredFile for in-memory stored-file reads

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>"
cd E:/Projects/ReScene.NET
git add ReScene.Lib
git commit -m "chore: bump ReScene.Lib (ReadStoredFile)

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>"
```

---

## Task 2: Service — `ReadStoredFileBytesAsync`

Adds the async read to the editing service and updates the two existing test doubles so the solution still compiles.

**Files:**
- Modify: `ReScene.NET/Services/ISrrEditingService.cs`
- Modify: `ReScene.NET/Services/SRREditingService.cs`
- Modify: `ReScene.NET.Tests/InspectorViewModelMkvTests.cs` (stub) and `ReScene.NET.Tests/SrrEditorViewModelTests.cs` (fake) — implement the new interface member.
- Test: `ReScene.NET.Tests/SRREditingServiceImageTests.cs` (new)

- [ ] **Step 1: Add the interface member**

In `ReScene.NET/Services/ISrrEditingService.cs`, add to the interface (after `ExtractStoredFileAsync`):

```csharp
    /// <summary>
    /// Reads the bytes of the first stored file whose name equals <paramref name="storedName"/>.
    /// </summary>
    /// <param name="srrFilePath">Path to the SRR file to read from.</param>
    /// <param name="storedName">The stored file name to match.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The stored file's bytes, or <see langword="null"/> if no match was found.</returns>
    public Task<byte[]?> ReadStoredFileBytesAsync(string srrFilePath, string storedName, CancellationToken ct = default);
```

- [ ] **Step 2: Implement in `SRREditingService`**

In `ReScene.NET/Services/SRREditingService.cs`, add (after `ExtractStoredFileAsync`):

```csharp
    /// <inheritdoc />
    public Task<byte[]?> ReadStoredFileBytesAsync(string srrFilePath, string storedName, CancellationToken ct = default)
        => Task.Run(() => SRRFile.Load(srrFilePath).ReadStoredFile(srrFilePath, name => name == storedName), ct);
```

- [ ] **Step 3: Update the two existing test doubles so they implement the new member**

In `ReScene.NET.Tests/InspectorViewModelMkvTests.cs`, add to `StubSrrEditingService` (alongside the other `throw new NotSupportedException();` members):

```csharp
        public Task<byte[]?> ReadStoredFileBytesAsync(string srrFilePath, string storedName, CancellationToken ct = default) => throw new NotSupportedException();
```

In `ReScene.NET.Tests/SrrEditorViewModelTests.cs`, add to `FakeSrrEditingService` these members (the recording fields support Task 6's tests):

```csharp
        /// <summary>Scripted bytes returned by <see cref="ReadStoredFileBytesAsync"/>.</summary>
        public byte[]? BytesToReturn { get; set; }

        /// <summary>The (path, name) of the last read request.</summary>
        public (string Path, string Name)? LastRead { get; private set; }

        public Task<byte[]?> ReadStoredFileBytesAsync(string srrFilePath, string storedName, CancellationToken ct = default)
        {
            Calls.Add(nameof(ReadStoredFileBytesAsync));
            LastRead = (srrFilePath, storedName);
            return Task.FromResult(BytesToReturn);
        }
```

- [ ] **Step 4: Write the failing service test**

Create `ReScene.NET.Tests/SRREditingServiceImageTests.cs`. This exercises the real service against a synthetic SRR written by the local helper below (the lib's internal builder is not visible here, so we write the minimal SRR bytes directly — header block 0x69 + one stored-file block 0x6A, matching the format in `SRRTestDataBuilder`).

```csharp
using System.Text;
using ReScene.NET.Services;

namespace ReScene.NET.Tests;

public class SRREditingServiceImageTests : TempDirTestBase
{
    /// <summary>
    /// Writes a minimal valid SRR: a header block (0x69) followed by one stored-file
    /// block (0x6A) carrying <paramref name="data"/>. Mirrors the on-disk layout the
    /// library parser expects.
    /// </summary>
    internal static string WriteMinimalSrr(string dir, string srrName, string storedName, byte[] data)
    {
        using var ms = new MemoryStream();
        using (var w = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true))
        {
            // SRR header block (no app name): CRC sentinel + type + flags + headerSize(7)
            w.Write((ushort)0x6969);
            w.Write((byte)0x69);
            w.Write((ushort)0x0000);
            w.Write((ushort)7);

            // Stored-file block: CRC + type + flags + headerSize + addSize + nameLen + name + data
            byte[] nameBytes = Encoding.UTF8.GetBytes(storedName);
            ushort headerSize = (ushort)(7 + 4 + 2 + nameBytes.Length);
            w.Write((ushort)0x6A6A);
            w.Write((byte)0x6A);
            w.Write((ushort)0x0000);
            w.Write(headerSize);
            w.Write((uint)data.Length);
            w.Write((ushort)nameBytes.Length);
            w.Write(nameBytes);
            w.Write(data);
        }

        string path = Path.Combine(dir, srrName);
        File.WriteAllBytes(path, ms.ToArray());
        return path;
    }

    [Fact]
    public async Task ReadStoredFileBytesAsync_ReturnsStoredBytes()
    {
        byte[] data = [0xAA, 0xBB, 0xCC, 0xDD];
        string srr = WriteMinimalSrr(TempDir, "svc.srr", "proof.jpg", data);

        var service = new SRREditingService();
        byte[]? bytes = await service.ReadStoredFileBytesAsync(srr, "proof.jpg");

        Assert.NotNull(bytes);
        Assert.Equal(data, bytes);
    }

    [Fact]
    public async Task ReadStoredFileBytesAsync_NoMatch_ReturnsNull()
    {
        string srr = WriteMinimalSrr(TempDir, "svc2.srr", "proof.jpg", [0x01]);

        var service = new SRREditingService();
        byte[]? bytes = await service.ReadStoredFileBytesAsync(srr, "absent.png");

        Assert.Null(bytes);
    }
}
```

- [ ] **Step 5: Run the service tests to verify they pass**

Run: `dotnet test ReScene.NET.Tests/ReScene.NET.Tests.csproj -p:BaseOutputPath=bin2/ --filter "FullyQualifiedName~SRREditingServiceImageTests"`
Expected: PASS (2 tests). (This also proves the interface/stub/fake additions compile.)

- [ ] **Step 6: Commit**

```bash
git add ReScene.NET/Services/ISrrEditingService.cs ReScene.NET/Services/SRREditingService.cs ReScene.NET.Tests/SRREditingServiceImageTests.cs ReScene.NET.Tests/InspectorViewModelMkvTests.cs ReScene.NET.Tests/SrrEditorViewModelTests.cs
git commit -m "feat(services): add ReadStoredFileBytesAsync for in-memory stored-file reads

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>"
```

---

## Task 3: Pure helper — `ImagePreviewSupport`

**Files:**
- Create: `ReScene.NET/Helpers/ImagePreviewSupport.cs`
- Test: `ReScene.NET.Tests/ImagePreviewSupportTests.cs` (new)

- [ ] **Step 1: Write the failing tests**

Create `ReScene.NET.Tests/ImagePreviewSupportTests.cs`:

```csharp
using ReScene.NET.Helpers;

namespace ReScene.NET.Tests;

public class ImagePreviewSupportTests
{
    [Theory]
    [InlineData("proof.jpg")]
    [InlineData("proof.jpeg")]
    [InlineData("cover.png")]
    [InlineData("anim.gif")]
    [InlineData("pic.bmp")]
    [InlineData("PROOF.JPG")]
    [InlineData("Cover.PnG")]
    [InlineData("folder/sub/extraproof-2011.jpg")]
    public void IsSupported_ImageExtensions_True(string name)
        => Assert.True(ImagePreviewSupport.IsSupported(name));

    [Theory]
    [InlineData("readme.nfo")]
    [InlineData("files.sfv")]
    [InlineData("notes.txt")]
    [InlineData("playlist.m3u")]
    [InlineData("noextension")]
    [InlineData("")]
    [InlineData("trailingdot.")]
    public void IsSupported_NonImage_False(string name)
        => Assert.False(ImagePreviewSupport.IsSupported(name));
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test ReScene.NET.Tests/ReScene.NET.Tests.csproj -p:BaseOutputPath=bin2/ --filter "FullyQualifiedName~ImagePreviewSupportTests"`
Expected: FAIL to compile — `ImagePreviewSupport` does not exist.

- [ ] **Step 3: Implement the helper**

Create `ReScene.NET/Helpers/ImagePreviewSupport.cs`:

```csharp
namespace ReScene.NET.Helpers;

/// <summary>
/// Decides which stored files are previewable as images. Pure (no WPF), so it can gate the
/// "View Image" / "Preview" affordances in view-models and be unit-tested directly.
/// </summary>
public static class ImagePreviewSupport
{
    private static readonly HashSet<string> SupportedExtensions =
        new(StringComparer.OrdinalIgnoreCase) { ".jpg", ".jpeg", ".png", ".gif", ".bmp" };

    /// <summary>
    /// Returns <see langword="true"/> when <paramref name="fileName"/> has a supported image
    /// extension (.jpg, .jpeg, .png, .gif, .bmp), case-insensitively.
    /// </summary>
    public static bool IsSupported(string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return false;
        }

        string ext = Path.GetExtension(fileName);
        return ext.Length > 0 && SupportedExtensions.Contains(ext);
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test ReScene.NET.Tests/ReScene.NET.Tests.csproj -p:BaseOutputPath=bin2/ --filter "FullyQualifiedName~ImagePreviewSupportTests"`
Expected: PASS (15 cases).

- [ ] **Step 5: Commit**

```bash
git add ReScene.NET/Helpers/ImagePreviewSupport.cs ReScene.NET.Tests/ImagePreviewSupportTests.cs
git commit -m "feat(helpers): add ImagePreviewSupport image-extension gate

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>"
```

---

## Task 4: Preview UI service + window

The decode-and-show plumbing. Not unit-tested (consistent with About/Settings/Prompt windows); verified by build.

**Files:**
- Create: `ReScene.NET/Services/IImagePreviewService.cs`
- Create: `ReScene.NET/Services/ImagePreviewService.cs`
- Create: `ReScene.NET/Views/ImagePreviewWindow.xaml`
- Create: `ReScene.NET/Views/ImagePreviewWindow.xaml.cs`

- [ ] **Step 1: Create the interface**

Create `ReScene.NET/Services/IImagePreviewService.cs`:

```csharp
namespace ReScene.NET.Services;

/// <summary>
/// Shows an embedded image's bytes in a popup preview window. Abstracted so view-models can
/// request a preview without referencing WPF imaging types (and so tests can verify the call).
/// </summary>
public interface IImagePreviewService
{
    /// <summary>
    /// Decodes <paramref name="data"/> and shows it in a modal preview window titled with
    /// <paramref name="fileName"/>. On decode failure, shows an error dialog and opens nothing.
    /// </summary>
    public void Preview(byte[] data, string fileName);
}
```

- [ ] **Step 2: Create the service**

Create `ReScene.NET/Services/ImagePreviewService.cs`:

```csharp
using System.IO;
using System.Linq;
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
```

- [ ] **Step 3: Create the window XAML**

Create `ReScene.NET/Views/ImagePreviewWindow.xaml`:

```xml
<Window x:Class="ReScene.NET.Views.ImagePreviewWindow"
        xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
        Title="{Binding TitleText}"
        Width="800" Height="600" MinWidth="240" MinHeight="180"
        WindowStartupLocation="CenterOwner"
        ResizeMode="CanResize"
        Background="{DynamicResource SurfaceBackground}">
  <DockPanel>
    <Border DockPanel.Dock="Bottom"
            Background="{DynamicResource SurfaceBackground}"
            BorderBrush="{DynamicResource BorderSeparator}"
            BorderThickness="0,1,0,0"
            Padding="8,4">
      <TextBlock Text="{Binding StatusText}"
                 Foreground="{DynamicResource ForegroundSecondary}"
                 FontSize="{DynamicResource FontSizeCaption}"
                 TextTrimming="CharacterEllipsis" />
    </Border>

    <ScrollViewer HorizontalScrollBarVisibility="Auto"
                  VerticalScrollBarVisibility="Auto">
      <Image Source="{Binding Image}"
             Stretch="Uniform"
             StretchDirection="DownOnly"
             SnapsToDevicePixels="True" />
    </ScrollViewer>
  </DockPanel>
</Window>
```

- [ ] **Step 4: Create the window code-behind**

Create `ReScene.NET/Views/ImagePreviewWindow.xaml.cs`. `DarkTitleBar.Enable` and `FormatUtilities.FormatSize` already exist in `ReScene.NET.Helpers` (used by `AboutWindow` and `StoredFileInfo` respectively).

```csharp
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
```

- [ ] **Step 5: Build to verify it compiles**

Run: `dotnet build ReScene.NET/ReScene.NET.csproj -p:BaseOutputPath=bin2/`
Expected: Build succeeded, `0 Error(s)`. Confirm no new `warning CS`/`warning CA` lines attributable to the new files.

- [ ] **Step 6: Commit**

```bash
git add ReScene.NET/Services/IImagePreviewService.cs ReScene.NET/Services/ImagePreviewService.cs ReScene.NET/Views/ImagePreviewWindow.xaml ReScene.NET/Views/ImagePreviewWindow.xaml.cs
git commit -m "feat(ui): add image preview service and popup window

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>"
```

---

## Task 5: Inspector wiring + tests

Add the shared recording fake, wire the Inspector VM/view, and wire `MainWindowViewModel`.

**Files:**
- Modify: `ReScene.NET.Tests/TestDoubles.cs` (add `RecordingImagePreviewService`)
- Modify: `ReScene.NET/ViewModels/InspectorViewModel.cs`
- Modify: `ReScene.NET/Views/InspectorView.xaml` + `InspectorView.xaml.cs`
- Modify: `ReScene.NET/ViewModels/MainWindowViewModel.cs`
- Modify: `ReScene.NET.Tests/InspectorViewModelMkvTests.cs` (factory now needs the new ctor arg)
- Test: `ReScene.NET.Tests/InspectorViewModelImageTests.cs` (new)

- [ ] **Step 1: Add the shared recording fake**

In `ReScene.NET.Tests/TestDoubles.cs`, add (top-level, alongside `NoOpFileDialogService`):

```csharp
/// <summary>Records every <see cref="IImagePreviewService.Preview"/> call for assertions.</summary>
public sealed class RecordingImagePreviewService : ReScene.NET.Services.IImagePreviewService
{
    public List<(byte[] Data, string FileName)> Calls { get; } = [];

    public void Preview(byte[] data, string fileName) => Calls.Add((data, fileName));
}
```

- [ ] **Step 2: Write the failing Inspector tests**

Create `ReScene.NET.Tests/InspectorViewModelImageTests.cs`. It builds a real, parseable minimal SRR with the helper from Task 2 (`SRREditingServiceImageTests.WriteMinimalSrr`), loads it through the real `InspectorViewModel.LoadFile` (which parses with the library), then drives the preview command with a fake editing service that returns scripted bytes.

```csharp
using ReScene.NET.Models;
using ReScene.NET.Services;
using ReScene.NET.ViewModels;
using ReScene.SRR;

namespace ReScene.NET.Tests;

public class InspectorViewModelImageTests : TempDirTestBase
{
    // Editing service that only serves ReadStoredFileBytesAsync; other members are unused here.
    private sealed class FakeReadEditingService : ISrrEditingService
    {
        public byte[]? BytesToReturn { get; set; }
        public (string Path, string Name)? LastRead { get; private set; }

        public void AddStoredFiles(string srrFilePath, IReadOnlyList<(string StoredName, string FilePath)> files) => throw new NotSupportedException();
        public void RemoveStoredFiles(string srrFilePath, IReadOnlyList<string> storedNames) => throw new NotSupportedException();
        public Task RenameStoredFileAsync(string srrPath, string oldName, string newName, CancellationToken ct = default) => throw new NotSupportedException();
        public Task MoveStoredFileAsync(string srrPath, string storedName, int offset, CancellationToken ct = default) => throw new NotSupportedException();
        public IReadOnlyList<StoredFileInfo> GetStoredFiles(string srrFilePath) => throw new NotSupportedException();
        public Task<string?> ExtractStoredFileAsync(string srrFilePath, string outputDirectory, string storedName, CancellationToken ct = default) => throw new NotSupportedException();

        public Task<byte[]?> ReadStoredFileBytesAsync(string srrFilePath, string storedName, CancellationToken ct = default)
        {
            LastRead = (srrFilePath, storedName);
            return Task.FromResult(BytesToReturn);
        }
    }

    private sealed class StubVerifyService : ISrrVerifyService
    {
        public Task<SRRVerifyResult> VerifyAsync(string srrFilePath, CancellationToken ct = default) => throw new NotSupportedException();
    }

    private sealed class StubPropertyExportService : IPropertyExportService
    {
        public Task ExportSelectedAsync(string outputPath, TreeNodeViewModel node, IEnumerable<PropertyItem> properties, CancellationToken ct = default) => throw new NotSupportedException();
        public Task ExportTreeAsync(string outputPath, IEnumerable<TreeNodeViewModel> roots, CancellationToken ct = default) => throw new NotSupportedException();
    }

    private static InspectorViewModel CreateVm(FakeReadEditingService editing, RecordingImagePreviewService preview) =>
        new(new NoOpFileDialogService(), editing, new StubVerifyService(), new StubPropertyExportService(), preview);

    private InspectorViewModel LoadWithStored(string storedName, FakeReadEditingService editing, RecordingImagePreviewService preview)
    {
        string srr = SRREditingServiceImageTests.WriteMinimalSrr(TempDir, "inspect.srr", storedName, [0x00]);
        InspectorViewModel vm = CreateVm(editing, preview);
        vm.LoadFile(srr);
        vm.SelectedTreeNode = vm.TreeRoots.Flatten()
            .First(n => n.Tag is SRRStoredFileBlock b && b.FileName == storedName);
        return vm;
    }

    [Fact]
    public void SelectImageStoredFile_MakesPreviewAvailable()
    {
        using InspectorViewModel vm = LoadWithStored("proof.jpg", new FakeReadEditingService(), new RecordingImagePreviewService());

        Assert.True(vm.IsImagePreviewAvailable);
        Assert.True(vm.PreviewStoredImageCommand.CanExecute(null));
    }

    [Fact]
    public void SelectNonImageStoredFile_PreviewUnavailable()
    {
        using InspectorViewModel vm = LoadWithStored("readme.nfo", new FakeReadEditingService(), new RecordingImagePreviewService());

        Assert.False(vm.IsImagePreviewAvailable);
        Assert.False(vm.PreviewStoredImageCommand.CanExecute(null));
    }

    [Fact]
    public async Task PreviewCommand_ForwardsBytesAndName()
    {
        var editing = new FakeReadEditingService { BytesToReturn = [0x01, 0x02, 0x03] };
        var preview = new RecordingImagePreviewService();
        using InspectorViewModel vm = LoadWithStored("proof.jpg", editing, preview);

        await vm.PreviewStoredImageCommand.ExecuteAsync(null);

        (byte[] data, string fileName) = Assert.Single(preview.Calls);
        Assert.Equal(new byte[] { 0x01, 0x02, 0x03 }, data);
        Assert.Equal("proof.jpg", fileName);
        Assert.Equal("proof.jpg", editing.LastRead!.Value.Name);
    }
}
```

- [ ] **Step 3: Run the Inspector tests to verify they fail**

Run: `dotnet test ReScene.NET.Tests/ReScene.NET.Tests.csproj -p:BaseOutputPath=bin2/ --filter "FullyQualifiedName~InspectorViewModelImageTests"`
Expected: FAIL to compile — `InspectorViewModel` has no `IsImagePreviewAvailable` / `PreviewStoredImageCommand`, and the 5-arg constructor does not exist.

- [ ] **Step 4: Wire the Inspector view-model**

In `ReScene.NET/ViewModels/InspectorViewModel.cs`:

(a) Add `using ReScene.NET.Helpers;` is already present. Add the constructor parameter and backing field. Change the primary constructor signature (line 18) to insert `IImagePreviewService imagePreviewService` **before** the optional settings param:

```csharp
public partial class InspectorViewModel(IFileDialogService fileDialog, ISrrEditingService srrEditingService, ISrrVerifyService verifyService, IPropertyExportService propertyExportService, IImagePreviewService imagePreviewService, IAppSettingsService? settingsService = null) : ViewModelBase, IDisposable
```

Add the field next to the other `private readonly` fields (after `_propertyExportService`):

```csharp
    private readonly IImagePreviewService _imagePreviewService = imagePreviewService;
```

(b) Add the computed availability property next to `IsStoredFileSelected` (after line 100):

```csharp
    /// <summary>
    /// Gets whether the selected stored file is a previewable image.
    /// </summary>
    public bool IsImagePreviewAvailable =>
        IsSRRFileLoaded()
        && SelectedTreeNode?.Tag is SRRStoredFileBlock block
        && ImagePreviewSupport.IsSupported(block.FileName);
```

(c) Add the command (place it near the other stored-file commands, e.g. after `RemoveStoredFileFromSRR`):

```csharp
    [RelayCommand(CanExecute = nameof(IsImagePreviewAvailable))]
    private async Task PreviewStoredImageAsync()
    {
        if (SelectedTreeNode?.Tag is not SRRStoredFileBlock stored
            || string.IsNullOrEmpty(_loadedFilePathInternal))
        {
            return;
        }

        try
        {
            byte[]? bytes = await _sRREditingService.ReadStoredFileBytesAsync(_loadedFilePathInternal, stored.FileName);
            if (bytes is null)
            {
                StatusMessage = $"Could not read stored file: {stored.FileName}";
                return;
            }

            _imagePreviewService.Preview(bytes, stored.FileName);
        }
        catch (Exception ex)
        {
            StatusMessage = $"Error previewing image: {ex.Message}";
        }
    }
```

(d) Notify the property/command wherever `IsStoredFileSelected` is already raised:

In `OnSelectedTreeNodeChanged` (after the existing `OnPropertyChanged(nameof(IsStoredFileSelected));` near line 294) add:

```csharp
        OnPropertyChanged(nameof(IsImagePreviewAvailable));
        PreviewStoredImageCommand.NotifyCanExecuteChanged();
```

In `LoadFile` (after the existing `OnPropertyChanged(nameof(IsStoredFileSelected));` near line 226) add the same two lines.

In `CloseFile` (after the existing `OnPropertyChanged(nameof(IsStoredFileSelected));` near line 80) add the same two lines.

- [ ] **Step 5: Wire the Inspector view**

In `ReScene.NET/Views/InspectorView.xaml`, replace the **Properties** panel header `Border` (the block at lines ~175-177):

```xml
            <Border DockPanel.Dock="Top" Style="{StaticResource PanelHeaderBar}">
              <TextBlock Style="{StaticResource PanelHeaderText}" Text="Properties" />
            </Border>
```

with:

```xml
            <Border DockPanel.Dock="Top" Style="{StaticResource PanelHeaderBar}">
              <DockPanel>
                <Button DockPanel.Dock="Right" Content="View Image"
                        Command="{Binding PreviewStoredImageCommand}"
                        Style="{StaticResource GhostButton}"
                        MinWidth="80" Margin="4,0,0,0"
                        Visibility="{Binding IsImagePreviewAvailable, Converter={StaticResource BoolToVisibility}}" />
                <TextBlock Style="{StaticResource PanelHeaderText}" Text="Properties" />
              </DockPanel>
            </Border>
```

Add a double-click handler to the `TreeView` (the element starting at line ~73). Add this attribute to the existing `<TreeView ...>` opening tag:

```xml
                    MouseDoubleClick="OnTreeViewMouseDoubleClick"
```

In `ReScene.NET/Views/InspectorView.xaml.cs`, add the handler (place after `TreeView_SelectedItemChanged`):

```csharp
    // Double-clicking an image stored-file node opens the preview, mirroring the header button.
    private void OnTreeViewMouseDoubleClick(object _, MouseButtonEventArgs e)
    {
        if (DataContext is InspectorViewModel vm && vm.PreviewStoredImageCommand.CanExecute(null))
        {
            vm.PreviewStoredImageCommand.Execute(null);
        }
    }
```

(`MouseButtonEventArgs` is in `System.Windows.Input`, already imported.)

- [ ] **Step 6: Update the MKV test factory for the new ctor arg**

In `ReScene.NET.Tests/InspectorViewModelMkvTests.cs`, update `CreateViewModel` (line ~52) to pass a recording preview service:

```csharp
    private static InspectorViewModel CreateViewModel() => new(
        new NoOpFileDialogService(), new StubSrrEditingService(),
        new StubSrrVerifyService(), new StubPropertyExportService(),
        new RecordingImagePreviewService());
```

- [ ] **Step 7: Wire `MainWindowViewModel`**

In `ReScene.NET/ViewModels/MainWindowViewModel.cs`, in the full constructor body, just after `_appSettingsService = appSettingsService;` (line ~163), construct the service:

```csharp
        var imagePreviewService = new ImagePreviewService(fileDialog);
```

Then update the `Inspector` construction (line ~167) to pass it before `appSettingsService`:

```csharp
        Inspector = new InspectorViewModel(fileDialog, srrEditingService, srrVerifyService, propertyExportService, imagePreviewService, appSettingsService);
```

(The two `SrrEditorViewModel` constructions are updated in Task 6, which reuses `imagePreviewService`.)

- [ ] **Step 8: Run the Inspector tests + full app suite to verify pass**

Run: `dotnet test ReScene.NET.Tests/ReScene.NET.Tests.csproj -p:BaseOutputPath=bin2/`
Expected: PASS — all app tests, including the 3 new `InspectorViewModelImageTests` and the unchanged MKV tests. (`MainWindowViewModel` builds with the new wiring even though Task 6's SrrEditor change is pending — the Beginner shell's `SrrEditorViewModel` will be updated next; if the build fails here because that line lacks the new arg, complete Step 1-3 of Task 6 first. To avoid the cross-task break, do Step 7 of this task and Task 6's VM/wiring together if running inline.)

> Note for inline execution: Steps 7 here and Task 6's MainWindowViewModel edits both touch the same constructor. If you hit a compile error from the not-yet-updated `SrrEditorViewModel(...)` calls, proceed into Task 6 before re-running tests.

- [ ] **Step 9: Commit**

```bash
git add ReScene.NET/ViewModels/InspectorViewModel.cs ReScene.NET/Views/InspectorView.xaml ReScene.NET/Views/InspectorView.xaml.cs ReScene.NET/ViewModels/MainWindowViewModel.cs ReScene.NET.Tests/TestDoubles.cs ReScene.NET.Tests/InspectorViewModelImageTests.cs ReScene.NET.Tests/InspectorViewModelMkvTests.cs
git commit -m "feat(inspector): View Image button for embedded image stored files

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>"
```

---

## Task 6: Edit-SRR wizard wiring + tests

**Files:**
- Modify: `ReScene.NET/ViewModels/SrrEditorViewModel.cs`
- Modify: `ReScene.NET/Views/Wizards/StoredFilesManagePanel.xaml` + `.xaml.cs`
- Modify: `ReScene.NET/ViewModels/MainWindowViewModel.cs` (two SrrEditor constructions)
- Modify: `ReScene.NET.Tests/SrrEditorViewModelTests.cs` (ctor seam + new tests; fake already extended in Task 2)

- [ ] **Step 1: Write the failing wizard tests**

In `ReScene.NET.Tests/SrrEditorViewModelTests.cs`:

(a) Update `TestSrrEditorViewModel`'s primary constructor (line ~146) to take and forward the preview service:

```csharp
    private sealed class TestSrrEditorViewModel(ISrrEditingService srrEditing, IFileDialogService fileDialog, ITempDirectoryService tempDir, IImagePreviewService imagePreview)
        : SrrEditorViewModel(srrEditing, fileDialog, tempDir, imagePreview)
    {
```

(b) Update the existing `CreateVm` (line ~168) to pass a throwaway preview service so existing callers are unaffected:

```csharp
    private static TestSrrEditorViewModel CreateVm(
        out FakeSrrEditingService editing,
        out FakeFileDialogService dialog)
    {
        editing = new FakeSrrEditingService();
        dialog = new FakeFileDialogService();
        return new TestSrrEditorViewModel(editing, dialog, new NoOpTempDirectoryService(), new RecordingImagePreviewService());
    }
```

(c) Add a preview-aware factory and the new tests (anywhere inside the class, e.g. at the end before the closing brace):

```csharp
    private static TestSrrEditorViewModel CreateImageVm(
        out FakeSrrEditingService editing,
        out RecordingImagePreviewService preview)
    {
        editing = new FakeSrrEditingService();
        preview = new RecordingImagePreviewService();
        return new TestSrrEditorViewModel(editing, new FakeFileDialogService(), new NoOpTempDirectoryService(), preview);
    }

    private static TestSrrEditorViewModel WithSelectedStored(
        string storedName, out FakeSrrEditingService editing, out RecordingImagePreviewService preview)
    {
        TestSrrEditorViewModel vm = CreateImageVm(out editing, out preview);
        editing.StoredFileNames.Add(storedName);
        vm.SourcePath = @"X:\src.srr";
        vm.EnsureWorkingCopy();              // builds the dummy working copy + reloads the list
        vm.SetSelection([vm.StoredFiles.First(f => f.Name == storedName)]);
        return vm;
    }

    // ── Preview command ─────────────────────────────────────

    [Fact]
    public void PreviewCommand_SingleImageSelected_IsEnabled()
    {
        TestSrrEditorViewModel vm = WithSelectedStored("proof.jpg", out _, out _);
        Assert.True(vm.PreviewStoredImageCommand.CanExecute(null));
    }

    [Fact]
    public void PreviewCommand_NonImageSelected_IsDisabled()
    {
        TestSrrEditorViewModel vm = WithSelectedStored("readme.nfo", out _, out _);
        Assert.False(vm.PreviewStoredImageCommand.CanExecute(null));
    }

    [Fact]
    public void PreviewCommand_MultipleSelected_IsDisabled()
    {
        TestSrrEditorViewModel vm = CreateImageVm(out FakeSrrEditingService editing, out _);
        editing.StoredFileNames.Add("a.jpg");
        editing.StoredFileNames.Add("b.jpg");
        vm.SourcePath = @"X:\src.srr";
        vm.EnsureWorkingCopy();
        vm.SetSelection([vm.StoredFiles[0], vm.StoredFiles[1]]);

        Assert.False(vm.PreviewStoredImageCommand.CanExecute(null));
    }

    [Fact]
    public async Task PreviewCommand_ForwardsBytesAndName()
    {
        TestSrrEditorViewModel vm = WithSelectedStored("proof.jpg", out FakeSrrEditingService editing, out RecordingImagePreviewService preview);
        editing.BytesToReturn = [0x09, 0x08, 0x07];

        await vm.PreviewStoredImageCommand.ExecuteAsync(null);

        (byte[] data, string fileName) = Assert.Single(preview.Calls);
        Assert.Equal(new byte[] { 0x09, 0x08, 0x07 }, data);
        Assert.Equal("proof.jpg", fileName);
        Assert.Equal((TestSrrEditorViewModel.DummyWorkingPath, "proof.jpg"), editing.LastRead!.Value);
    }
```

- [ ] **Step 2: Run the wizard tests to verify they fail**

Run: `dotnet test ReScene.NET.Tests/ReScene.NET.Tests.csproj -p:BaseOutputPath=bin2/ --filter "FullyQualifiedName~SrrEditorViewModelTests.PreviewCommand"`
Expected: FAIL to compile — `SrrEditorViewModel` has no 4-arg constructor and no `PreviewStoredImageCommand`.

- [ ] **Step 3: Wire the Edit-SRR view-model**

In `ReScene.NET/ViewModels/SrrEditorViewModel.cs`:

(a) Add `using ReScene.NET.Helpers;` to the using block (for `ImagePreviewSupport`).

(b) Change the primary constructor (line ~16) to add the preview service:

```csharp
public partial class SrrEditorViewModel(ISrrEditingService srrEditing, IFileDialogService fileDialog, ITempDirectoryService tempDir, IImagePreviewService imagePreview) : ViewModelBase
{
    private readonly ISrrEditingService _srrEditing = srrEditing;
    private readonly IFileDialogService _fileDialog = fileDialog;
    private readonly ITempDirectoryService _tempDir = tempDir;
    private readonly IImagePreviewService _imagePreview = imagePreview;
```

(c) Add the can-execute predicate next to `HasSingleSelection` (line ~289):

```csharp
    /// <summary>True when exactly one selected stored file is a previewable image.</summary>
    private bool HasSingleImageSelection()
        => SelectedStoredFiles.Count == 1 && ImagePreviewSupport.IsSupported(SelectedStoredFiles[0].Name);
```

(d) Add the command (place after `ExtractStoredFileAsync`, before `BuildExtractStatus`):

```csharp
    [RelayCommand(CanExecute = nameof(HasSingleImageSelection))]
    private async Task PreviewStoredImageAsync()
    {
        if (_workingCopyPath is null || SelectedStoredFiles.Count != 1)
        {
            return;
        }

        string name = SelectedStoredFiles[0].Name;

        try
        {
            byte[]? bytes = await _srrEditing.ReadStoredFileBytesAsync(_workingCopyPath, name);
            if (bytes is null)
            {
                Log($"Could not read \"{name}\" to preview.");
                return;
            }

            _imagePreview.Preview(bytes, name);
        }
        catch (Exception ex)
        {
            Log($"Preview failed for \"{name}\": {ex.Message}");
        }
    }
```

(e) In `SetSelection` (line ~213), add the command notification alongside the others:

```csharp
        PreviewStoredImageCommand.NotifyCanExecuteChanged();
```

- [ ] **Step 4: Wire the wizard view**

In `ReScene.NET/Views/Wizards/StoredFilesManagePanel.xaml`, add a "Preview…" button to the toolbar `StackPanel`. Insert it after the "Extract…" button (line ~15):

```xml
      <Button Content="Preview…" Command="{Binding PreviewStoredImageCommand}"
              Style="{StaticResource GhostButton}" Padding="10,4" Margin="0,0,4,0" />
```

Add a double-click handler to the `DataGrid` (`StoredFilesGrid`, line ~22) by adding this attribute to its opening tag:

```xml
              MouseDoubleClick="StoredFilesGrid_MouseDoubleClick"
```

In `ReScene.NET/Views/Wizards/StoredFilesManagePanel.xaml.cs`, add the handler (the class already has access to `DataContext as SrrEditorViewModel`):

```csharp
    // Double-clicking an image row opens the preview, mirroring the Preview… button.
    private void StoredFilesGrid_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (DataContext is SrrEditorViewModel vm && vm.PreviewStoredImageCommand.CanExecute(null))
        {
            vm.PreviewStoredImageCommand.Execute(null);
        }
    }
```

- [ ] **Step 5: Update `MainWindowViewModel` SrrEditor constructions**

In `ReScene.NET/ViewModels/MainWindowViewModel.cs`, pass the `imagePreviewService` created in Task 5 to both `SrrEditorViewModel` instances:

The Beginner shell construction (line ~188):

```csharp
            SrrEditor = new SrrEditorViewModel(srrEditingService, fileDialog, tempDir, imagePreviewService),
```

(There is one `SrrEditorViewModel` construction in this file — the Beginner shell. If a search reveals any other `new SrrEditorViewModel(` call site, add `imagePreviewService` as the 4th argument there too.)

- [ ] **Step 6: Run the wizard tests + full app suite to verify pass**

Run: `dotnet test ReScene.NET.Tests/ReScene.NET.Tests.csproj -p:BaseOutputPath=bin2/`
Expected: PASS — all app tests including the 4 new `PreviewCommand` tests and all pre-existing `SrrEditorViewModelTests`.

- [ ] **Step 7: Commit**

```bash
git add ReScene.NET/ViewModels/SrrEditorViewModel.cs ReScene.NET/Views/Wizards/StoredFilesManagePanel.xaml ReScene.NET/Views/Wizards/StoredFilesManagePanel.xaml.cs ReScene.NET/ViewModels/MainWindowViewModel.cs ReScene.NET.Tests/SrrEditorViewModelTests.cs
git commit -m "feat(edit-srr): Preview button for embedded image stored files

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>"
```

---

## Task 7: Full verification + cleanup

**Files:** none (verification only)

- [ ] **Step 1: Build the whole app (release-style) to confirm no warnings**

Run: `dotnet build ReScene.NET.slnx -p:BaseOutputPath=bin2/`
Expected: `Build succeeded`, `0 Error(s)`. Review the output for any new `warning CS` or `warning CA` lines and fix them if attributable to this work. (Grep the entire output for `warning`, not just `warning CS` — analyzer warnings are `warning CA`.)

- [ ] **Step 2: Run all tests (lib + app)**

Run both:
```bash
dotnet test ReScene.Lib/ReScene.Tests/ReScene.Tests.csproj -p:BaseOutputPath=bin2/
dotnet test ReScene.NET.Tests/ReScene.NET.Tests.csproj -p:BaseOutputPath=bin2/
```
Expected: both suites PASS, 0 failures.

- [ ] **Step 3: Delete all `bin2` folders**

Run: `find E:/Projects/ReScene.NET -type d -name bin2 -prune -exec rm -rf {} + 2>/dev/null`
Expected: no output; `git status` clean except intended changes already committed.

- [ ] **Step 4: Manual smoke test (ask the user)**

Since the app is running, ask the user to confirm in-app:
1. Inspector → load an SRR with a proof `.jpg` → select it → a **"View Image"** button appears in the Properties header → click it → the image opens in a resizable window with `name • W×H • size` in the status bar. Double-clicking the node does the same. Selecting a `.nfo` hides the button.
2. Edit-SRR wizard → manage step → select a stored image → **"Preview…"** enables → opens the same window. Double-click works. Non-image / multi-select disables it.
3. A deliberately non-decodable file with an image extension shows the "Could not display image" error and opens no window.

---

## Self-review notes

- **Spec coverage:** lib `ReadStoredFile` (Task 1); `ReadStoredFileBytesAsync` (Task 2); `ImagePreviewSupport` (Task 3); `IImagePreviewService`/`ImagePreviewService`/`ImagePreviewWindow` (Task 4); Inspector "View Image" button + double-click + wiring (Task 5); Edit-SRR "Preview…" button + double-click + wiring (Task 6); MainWindowViewModel wiring (Tasks 5+6); all tests from the spec's testing section are present. MVP exclusions (no zoom, read-only, modal) honored.
- **Type/name consistency:** `ReadStoredFile(string, Func<string,bool>)` → `ReadStoredFileBytesAsync(string, string, CancellationToken)` → VM commands `PreviewStoredImageCommand` (both VMs) → `IImagePreviewService.Preview(byte[], string)` → `ImagePreviewWindow(BitmapSource, string, long)`. Inspector gate property `IsImagePreviewAvailable`; wizard gate method `HasSingleImageSelection`. `RecordingImagePreviewService` shared via `TestDoubles.cs`; `WriteMinimalSrr` shared from `SRREditingServiceImageTests`.
- **Cross-task build coupling:** `MainWindowViewModel` is edited in both Task 5 (Inspector + service construction) and Task 6 (SrrEditor args). For subagent-driven execution the tasks run in order so this resolves at Task 6; the note in Task 5 Step 8 flags the transient state.
