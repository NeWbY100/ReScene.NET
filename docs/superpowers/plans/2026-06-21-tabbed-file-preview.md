# Tabbed File-Preview Dialog Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make the Edit-SRR wizard's "Preview…" work for any single selected file, opening a tabbed Hex/Text/Image preview window (Image tab only for images; default tab Hex).

**Architecture:** A `ByteArrayDataSource` wraps the file's bytes as an `IHexDataSource`; a shared `ImageDecoder.TryDecode` produces an optional `BitmapSource`; a testable `FilePreviewViewModel` drives the tabs (Hex via `HexViewControl`, Text via `TextDecoder`/`TextEncodingOptions`, Image when decoded); `FilePreviewService` shows the `FilePreviewWindow`. The wizard's `SrrEditorViewModel` switches from `IImagePreviewService` to `IFilePreviewService` and ungates the command. The Inspector is unchanged.

**Tech Stack:** .NET 10, WPF, CommunityToolkit.Mvvm (`[ObservableProperty]`), xUnit. App-only; no library/public-API change.

**Spec:** `docs/superpowers/specs/2026-06-21-tabbed-file-preview-design.md`

---

## CRITICAL build/test constraint (read before any `dotnet` command)

The user keeps the app running, which **locks `bin/`**. Therefore:
- **ALWAYS** build/test with `-p:BaseOutputPath=bin2/`.
- **NEVER** kill the running app.
- After the final task, delete every `bin2` folder:
  ```bash
  find E:/Projects/ReScene.NET -type d -name bin2 -prune -exec rm -rf {} + 2>/dev/null
  ```

**App-only** change — no `ReScene.Lib` (submodule) edits, no public-API change. All work and commits are in the **parent repo** `E:/Projects/ReScene.NET`. Git Bash on Windows. Commit messages end with:

```
Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>
```

Keep the `NeWbY100` gh account active.

## Branch setup (do once, before Task 1)

```bash
cd E:/Projects/ReScene.NET
git checkout -b feature/tabbed-file-preview
```

## Context the implementer needs

- `IHexDataSource` (namespace `ReScene.Hex`, defined in the library, referenced by the app): `long Length { get; }` + `int Read(long position, byte[] buffer, int offset, int count)` (returns bytes read; 0 at/after end).
- `Controls/HexViewControl` DPs used here: `DataSource` (`IHexDataSource`), `BlockOffset` (`long`), `BlockLength` (`long`), `BytesPerLine` (`int`).
- `Helpers/TextDecoder.Decode(IHexDataSource? source, long length, Encoding encoding, int maxBytes)` → `(string Text, bool Truncated)`.
- `Helpers/TextEncodingOptions.All` (`IReadOnlyList<TextEncodingOption>`, UTF-8 at index 0); `TextEncodingOption(string DisplayName, Encoding Encoding)` whose `ToString()` returns `DisplayName`.
- `Helpers/ImagePreviewSupport.IsSupported(string fileName)` → true for .jpg/.jpeg/.png/.gif/.bmp.
- `Helpers/FormatUtilities.FormatSize(long)`; `Helpers/DarkTitleBar.Enable(Window)`.
- `ViewModelBase : ObservableObject` (abstract, parameterless) — base for view-models.
- Converter `BoolToVisibility` (a `BooleanToVisibilityConverter`) is registered in `App.xaml` and resolvable from any Window via `{StaticResource BoolToVisibility}`.
- The themed `TabControl`/`TabItem` styles in `App.xaml` give an Auto tab-strip row + a `*` content row (content fills).
- The app project has `<Using Include="System.IO" />` and `ImplicitUsings` (so `System.Linq`, `System.IO`, etc. are available); `InternalsVisibleTo` exposes internals to `ReScene.NET.Tests`.
- `ImagePreviewService` currently decodes inline and is used by the Inspector via `IImagePreviewService`; it must keep working unchanged for the Inspector.

---

## File map

**Create**
- `ReScene.NET/Services/ByteArrayDataSource.cs` — `IHexDataSource` over a `byte[]`.
- `ReScene.NET/Helpers/ImageDecoder.cs` — `TryDecode(byte[]) → BitmapSource?`.
- `ReScene.NET/Services/IFilePreviewService.cs` + `Services/FilePreviewService.cs`.
- `ReScene.NET/ViewModels/FilePreviewViewModel.cs`.
- `ReScene.NET/Views/FilePreviewWindow.xaml` (+ `.xaml.cs`).
- `ReScene.NET.Tests/ByteArrayDataSourceTests.cs`, `ReScene.NET.Tests/FilePreviewViewModelTests.cs`.

**Modify**
- `ReScene.NET/Services/ImagePreviewService.cs` (use `ImageDecoder.TryDecode`).
- `ReScene.NET/ViewModels/SrrEditorViewModel.cs` (swap service, ungate, rename command).
- `ReScene.NET/Views/Wizards/StoredFilesManagePanel.xaml` + `.xaml.cs`.
- `ReScene.NET/ViewModels/MainWindowViewModel.cs` (construct `FilePreviewService`).
- `ReScene.NET.Tests/TestDoubles.cs` (add `RecordingFilePreviewService`).
- `ReScene.NET.Tests/SrrEditorViewModelTests.cs` (gating change + fake swap).

---

## Task 1: ByteArrayDataSource

**Files:**
- Create: `ReScene.NET/Services/ByteArrayDataSource.cs`
- Test: `ReScene.NET.Tests/ByteArrayDataSourceTests.cs`

- [ ] **Step 1: Write the failing tests**

Create `ReScene.NET.Tests/ByteArrayDataSourceTests.cs`:
```csharp
using ReScene.NET.Services;

namespace ReScene.NET.Tests;

public class ByteArrayDataSourceTests
{
    [Fact]
    public void Length_IsBufferLength()
    {
        using var source = new ByteArrayDataSource([1, 2, 3, 4]);
        Assert.Equal(4, source.Length);
    }

    [Fact]
    public void Read_ReturnsRequestedBytes()
    {
        using var source = new ByteArrayDataSource([10, 20, 30, 40, 50]);
        byte[] buffer = new byte[3];

        int read = source.Read(1, buffer, 0, 3);

        Assert.Equal(3, read);
        Assert.Equal(new byte[] { 20, 30, 40 }, buffer);
    }

    [Fact]
    public void Read_ClampsAtEnd()
    {
        using var source = new ByteArrayDataSource([10, 20, 30]);
        byte[] buffer = new byte[10];

        int read = source.Read(1, buffer, 0, 10);

        Assert.Equal(2, read);
        Assert.Equal(20, buffer[0]);
        Assert.Equal(30, buffer[1]);
    }

    [Fact]
    public void Read_PastEnd_ReturnsZero()
    {
        using var source = new ByteArrayDataSource([1, 2, 3]);
        byte[] buffer = new byte[4];

        Assert.Equal(0, source.Read(3, buffer, 0, 4));
        Assert.Equal(0, source.Read(99, buffer, 0, 4));
    }

    [Fact]
    public void Read_WritesAtBufferOffset()
    {
        using var source = new ByteArrayDataSource([7, 8]);
        byte[] buffer = new byte[4];

        int read = source.Read(0, buffer, 2, 2);

        Assert.Equal(2, read);
        Assert.Equal(new byte[] { 0, 0, 7, 8 }, buffer);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test ReScene.NET.Tests/ReScene.NET.Tests.csproj -p:BaseOutputPath=bin2/ --filter "FullyQualifiedName~ByteArrayDataSourceTests"`
Expected: FAIL to compile — `ByteArrayDataSource` does not exist.

- [ ] **Step 3: Implement the data source**

Create `ReScene.NET/Services/ByteArrayDataSource.cs`:
```csharp
using ReScene.Hex;

namespace ReScene.NET.Services;

/// <summary>
/// An <see cref="IHexDataSource"/> over an in-memory buffer, so the Hex view and text decoder
/// can read a file that has already been loaded into a <see cref="byte"/> array.
/// </summary>
public sealed class ByteArrayDataSource(byte[] data) : IHexDataSource
{
    /// <inheritdoc />
    public long Length => data.Length;

    /// <inheritdoc />
    public int Read(long position, byte[] buffer, int offset, int count)
    {
        if (position < 0 || position >= data.Length)
        {
            return 0;
        }

        int available = (int)Math.Min(count, data.Length - position);
        Array.Copy(data, position, buffer, offset, available);
        return available;
    }

    public void Dispose()
    {
        // Nothing to release — the buffer is owned by the caller.
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test ReScene.NET.Tests/ReScene.NET.Tests.csproj -p:BaseOutputPath=bin2/ --filter "FullyQualifiedName~ByteArrayDataSourceTests"`
Expected: PASS (5 tests). Inspect output for any NEW `warning CS`/`warning CA` and fix.

- [ ] **Step 5: Commit**

```bash
cd E:/Projects/ReScene.NET
git add ReScene.NET/Services/ByteArrayDataSource.cs ReScene.NET.Tests/ByteArrayDataSourceTests.cs
git commit -m "feat(preview): add ByteArrayDataSource (in-memory IHexDataSource)

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>"
```

---

## Task 2: Shared ImageDecoder + refactor ImagePreviewService

Extract the image-decode into a shared helper and adopt it in the existing `ImagePreviewService` (no behaviour change for the Inspector). Build-verified.

**Files:**
- Create: `ReScene.NET/Helpers/ImageDecoder.cs`
- Modify: `ReScene.NET/Services/ImagePreviewService.cs`

- [ ] **Step 1: Create the decoder helper**

Create `ReScene.NET/Helpers/ImageDecoder.cs`:
```csharp
using System.Windows.Media.Imaging;

namespace ReScene.NET.Helpers;

/// <summary>
/// Decodes image bytes to a frozen <see cref="BitmapSource"/>. Returns <see langword="null"/> when the
/// bytes are not a decodable image, so callers can treat "not an image" as a normal outcome.
/// </summary>
public static class ImageDecoder
{
    public static BitmapSource? TryDecode(byte[] data)
    {
        try
        {
            using var stream = new MemoryStream(data);
            BitmapFrame frame = BitmapFrame.Create(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
            frame.Freeze();
            return frame;
        }
        catch (Exception ex) when (ex is NotSupportedException or ArgumentException or FileFormatException or IOException or OverflowException)
        {
            return null;
        }
    }
}
```
(`MemoryStream`, `IOException`, `FileFormatException` come from `System.IO` — already an implicit using in the app project.)

- [ ] **Step 2: Refactor `ImagePreviewService` to use it**

In `ReScene.NET/Services/ImagePreviewService.cs`, replace the inline decode in `Preview` so it calls the helper, keeping the existing error-dialog behaviour. Add `using ReScene.NET.Helpers;` at the top. The `Preview` method becomes:
```csharp
    /// <inheritdoc />
    public void Preview(byte[] data, string fileName)
    {
        BitmapSource? image = ImageDecoder.TryDecode(data);
        if (image is null)
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
```
Leave `ActiveWindow()` and the rest of the class as-is. Remove any now-unused `using` (e.g. if `System.Windows.Media.Imaging` is still needed for the `BitmapSource` type, keep it; the `MemoryStream`/`BitmapFrame` references are gone). After editing, the file should still compile with 0 warnings — fix any IDE0005 unused-using the build reports.

- [ ] **Step 3: Build to verify it compiles**

Run: `dotnet build ReScene.NET/ReScene.NET.csproj -p:BaseOutputPath=bin2/`
Expected: `Build succeeded`, `0 Warning(s)`, `0 Error(s)`.

- [ ] **Step 4: Run the full app suite (the Inspector image path must still pass)**

Run: `dotnet test ReScene.NET.Tests/ReScene.NET.Tests.csproj -p:BaseOutputPath=bin2/`
Expected: all PASS (no behaviour change).

- [ ] **Step 5: Commit**

```bash
cd E:/Projects/ReScene.NET
git add ReScene.NET/Helpers/ImageDecoder.cs ReScene.NET/Services/ImagePreviewService.cs
git commit -m "refactor(preview): extract shared ImageDecoder.TryDecode

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>"
```

---

## Task 3: FilePreviewViewModel

**Files:**
- Create: `ReScene.NET/ViewModels/FilePreviewViewModel.cs`
- Test: `ReScene.NET.Tests/FilePreviewViewModelTests.cs`

- [ ] **Step 1: Write the failing tests**

Create `ReScene.NET.Tests/FilePreviewViewModelTests.cs` (the test project is `net10.0-windows` with WPF, so `BitmapSource` is available; `BitmapSource.Create` builds a dummy image without a UI thread):
```csharp
using System.Text;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ReScene.NET.ViewModels;

namespace ReScene.NET.Tests;

public class FilePreviewViewModelTests
{
    private static BitmapSource DummyImage()
        => BitmapSource.Create(2, 3, 96, 96, PixelFormats.Bgr24, null, new byte[2 * 3 * 3], 2 * 3);

    [Fact]
    public void NonImage_HasNoImageTab_AndDecodesText()
    {
        byte[] data = Encoding.ASCII.GetBytes("HELLO_NFO");
        var vm = new FilePreviewViewModel(data, "readme.nfo", image: null);

        Assert.False(vm.HasImageTab);
        Assert.Null(vm.Image);
        Assert.Equal(data.Length, vm.HexBlockLength);
        Assert.Equal("UTF-8", vm.SelectedEncoding.DisplayName);
        Assert.Equal("HELLO_NFO", vm.TextViewContent);
        Assert.False(vm.TextViewTruncated);
    }

    [Fact]
    public void Image_HasImageTab()
    {
        var vm = new FilePreviewViewModel([0x01, 0x02], "proof.jpg", image: DummyImage());

        Assert.True(vm.HasImageTab);
        Assert.NotNull(vm.Image);
    }

    [Fact]
    public void ChangingEncoding_Redecodes()
    {
        // 0xC9 → CP437 '╔' (U+2554) vs Latin-1 'É' (U+00C9).
        var vm = new FilePreviewViewModel([0xC9], "enc.bin", image: null);

        vm.SelectedEncoding = vm.TextEncodings.First(e => e.DisplayName == "CP437 (DOS)");
        Assert.Contains('╔', vm.TextViewContent);

        vm.SelectedEncoding = vm.TextEncodings.First(e => e.DisplayName == "ISO-8859-1 (Latin-1)");
        Assert.Contains('É', vm.TextViewContent);
        Assert.DoesNotContain('╔', vm.TextViewContent);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test ReScene.NET.Tests/ReScene.NET.Tests.csproj -p:BaseOutputPath=bin2/ --filter "FullyQualifiedName~FilePreviewViewModelTests"`
Expected: FAIL to compile — `FilePreviewViewModel` does not exist.

- [ ] **Step 3: Implement the view-model**

Create `ReScene.NET/ViewModels/FilePreviewViewModel.cs`:
```csharp
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

    public long HexBlockOffset => 0;

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
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test ReScene.NET.Tests/ReScene.NET.Tests.csproj -p:BaseOutputPath=bin2/ --filter "FullyQualifiedName~FilePreviewViewModelTests"`
Expected: PASS (3 tests). Inspect output for NEW `warning CS`/`warning CA` and fix.

- [ ] **Step 5: Commit**

```bash
cd E:/Projects/ReScene.NET
git add ReScene.NET/ViewModels/FilePreviewViewModel.cs ReScene.NET.Tests/FilePreviewViewModelTests.cs
git commit -m "feat(preview): add FilePreviewViewModel (hex/text/image tabs)

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>"
```

---

## Task 4: FilePreviewService + FilePreviewWindow

The UI service + tabbed window. Build-verified (no unit tests — WPF window/service, consistent with the existing windows).

**Files:**
- Create: `ReScene.NET/Services/IFilePreviewService.cs`, `Services/FilePreviewService.cs`
- Create: `ReScene.NET/Views/FilePreviewWindow.xaml` (+ `.xaml.cs`)

- [ ] **Step 1: Create the interface**

Create `ReScene.NET/Services/IFilePreviewService.cs`:
```csharp
namespace ReScene.NET.Services;

/// <summary>
/// Shows a tabbed (Hex / Text / Image) preview of a file's bytes in a popup window.
/// </summary>
public interface IFilePreviewService
{
    /// <summary>Opens the preview window for <paramref name="data"/>, titled with <paramref name="fileName"/>.</summary>
    public void Preview(byte[] data, string fileName);
}
```

- [ ] **Step 2: Create the service**

Create `ReScene.NET/Services/FilePreviewService.cs`:
```csharp
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
```

- [ ] **Step 3: Create the window XAML**

Create `ReScene.NET/Views/FilePreviewWindow.xaml`:
```xml
<Window x:Class="ReScene.NET.Views.FilePreviewWindow"
        xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
        xmlns:controls="clr-namespace:ReScene.NET.Controls"
        Title="{Binding TitleText}"
        Width="880" Height="620" MinWidth="360" MinHeight="240"
        WindowStartupLocation="CenterOwner"
        ResizeMode="CanResize"
        ShowInTaskbar="False"
        Background="{DynamicResource WindowBackground}"
        Foreground="{DynamicResource ForegroundPrimary}"
        FontFamily="{DynamicResource UIFontFamily}">
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

    <TabControl>
      <!-- Hex tab (first → default) -->
      <TabItem Header="Hex">
        <DockPanel>
          <StackPanel DockPanel.Dock="Top" Orientation="Horizontal"
                      HorizontalAlignment="Right" Margin="4,4,4,2">
            <TextBlock Text="Bytes/Row:" VerticalAlignment="Center"
                       Foreground="{DynamicResource ForegroundSecondary}"
                       FontSize="{DynamicResource FontSizeCaption}"
                       Margin="0,0,4,0" />
            <ComboBox SelectedValue="{Binding HexBytesPerLine}"
                      SelectedValuePath="Content"
                      IsEditable="True"
                      Text="{Binding HexBytesPerLine, UpdateSourceTrigger=LostFocus}"
                      Width="52" FontSize="{DynamicResource FontSizeCaption}"
                      VerticalAlignment="Center" Padding="4,1">
              <ComboBoxItem Content="8" />
              <ComboBoxItem Content="16" />
              <ComboBoxItem Content="24" />
              <ComboBoxItem Content="32" />
              <ComboBoxItem Content="48" />
              <ComboBoxItem Content="64" />
            </ComboBox>
          </StackPanel>
          <controls:HexViewControl DataSource="{Binding HexDataSource}"
                                   BlockOffset="{Binding HexBlockOffset}"
                                   BlockLength="{Binding HexBlockLength}"
                                   BytesPerLine="{Binding HexBytesPerLine}" />
        </DockPanel>
      </TabItem>

      <!-- Text tab -->
      <TabItem Header="Text">
        <DockPanel>
          <StackPanel DockPanel.Dock="Top" Orientation="Horizontal"
                      HorizontalAlignment="Right" Margin="4,4,4,2">
            <TextBlock Text="Encoding:" VerticalAlignment="Center"
                       Foreground="{DynamicResource ForegroundSecondary}"
                       FontSize="{DynamicResource FontSizeCaption}"
                       Margin="0,0,4,0" />
            <ComboBox ItemsSource="{Binding TextEncodings}"
                      SelectedItem="{Binding SelectedEncoding}"
                      DisplayMemberPath="DisplayName"
                      Width="160" FontSize="{DynamicResource FontSizeCaption}"
                      VerticalAlignment="Center" Padding="4,1" />
            <CheckBox Content="Word wrap"
                      IsChecked="{Binding TextWordWrap}"
                      VerticalAlignment="Center"
                      Margin="10,0,0,0" />
          </StackPanel>
          <Border DockPanel.Dock="Bottom"
                  Visibility="{Binding TextViewTruncated, Converter={StaticResource BoolToVisibility}}"
                  Background="{DynamicResource SurfaceBackground}"
                  BorderBrush="{DynamicResource BorderSeparator}"
                  BorderThickness="0,1,0,0"
                  Padding="6,3">
            <TextBlock Text="Large file — showing the first 1 MB as text."
                       Foreground="{DynamicResource ForegroundSecondary}"
                       FontSize="{DynamicResource FontSizeCaption}" />
          </Border>
          <TextBox Text="{Binding TextViewContent, Mode=OneWay}"
                   IsReadOnly="True"
                   FontFamily="{DynamicResource MonoFontFamily}"
                   FontSize="{DynamicResource FontSizeCaption}"
                   VerticalScrollBarVisibility="Auto"
                   HorizontalScrollBarVisibility="Auto"
                   Padding="6"
                   BorderThickness="0">
            <TextBox.Style>
              <Style TargetType="TextBox" BasedOn="{StaticResource {x:Type TextBox}}">
                <Setter Property="TextWrapping" Value="NoWrap" />
                <Style.Triggers>
                  <DataTrigger Binding="{Binding TextWordWrap}" Value="True">
                    <Setter Property="TextWrapping" Value="Wrap" />
                  </DataTrigger>
                </Style.Triggers>
              </Style>
            </TextBox.Style>
          </TextBox>
        </DockPanel>
      </TabItem>

      <!-- Image tab (collapsed unless an image decoded) -->
      <TabItem Header="Image"
               Visibility="{Binding HasImageTab, Converter={StaticResource BoolToVisibility}}">
        <ScrollViewer HorizontalScrollBarVisibility="Auto"
                      VerticalScrollBarVisibility="Auto">
          <Image Source="{Binding Image}"
                 Stretch="Uniform"
                 StretchDirection="DownOnly"
                 SnapsToDevicePixels="True" />
        </ScrollViewer>
      </TabItem>
    </TabControl>
  </DockPanel>
</Window>
```

- [ ] **Step 4: Create the window code-behind**

Create `ReScene.NET/Views/FilePreviewWindow.xaml.cs`:
```csharp
using System.Windows;
using ReScene.NET.Helpers;
using ReScene.NET.ViewModels;

namespace ReScene.NET.Views;

public partial class FilePreviewWindow : Window
{
    public FilePreviewWindow(FilePreviewViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        SourceInitialized += (_, _) => DarkTitleBar.Enable(this);
    }
}
```

- [ ] **Step 5: Build to verify it compiles**

Run: `dotnet build ReScene.NET/ReScene.NET.csproj -p:BaseOutputPath=bin2/`
Expected: `Build succeeded`, `0 Warning(s)`, `0 Error(s)`. Fix any new warnings.

- [ ] **Step 6: Commit**

```bash
cd E:/Projects/ReScene.NET
git add ReScene.NET/Services/IFilePreviewService.cs ReScene.NET/Services/FilePreviewService.cs ReScene.NET/Views/FilePreviewWindow.xaml ReScene.NET/Views/FilePreviewWindow.xaml.cs
git commit -m "feat(preview): add FilePreviewService and tabbed FilePreviewWindow

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>"
```

---

## Task 5: Wire the Edit-SRR wizard to the file preview

Switch `SrrEditorViewModel` from `IImagePreviewService` to `IFilePreviewService`, ungate the command to any single selection, rename it, and update the panel, `MainWindowViewModel`, and the tests.

**Files:**
- Modify: `ReScene.NET/ViewModels/SrrEditorViewModel.cs`
- Modify: `ReScene.NET/Views/Wizards/StoredFilesManagePanel.xaml` + `.xaml.cs`
- Modify: `ReScene.NET/ViewModels/MainWindowViewModel.cs`
- Modify: `ReScene.NET.Tests/TestDoubles.cs`, `ReScene.NET.Tests/SrrEditorViewModelTests.cs`

- [ ] **Step 1: Add the `RecordingFilePreviewService` test double**

In `ReScene.NET.Tests/TestDoubles.cs`, add (alongside `RecordingImagePreviewService`, which stays for the Inspector tests):
```csharp
/// <summary>Records every <see cref="IFilePreviewService.Preview"/> call for assertions.</summary>
public sealed class RecordingFilePreviewService : ReScene.NET.Services.IFilePreviewService
{
    public List<(byte[] Data, string FileName)> Calls { get; } = [];

    public void Preview(byte[] data, string fileName) => Calls.Add((data, fileName));
}
```

- [ ] **Step 2: Update the failing wizard tests**

In `ReScene.NET.Tests/SrrEditorViewModelTests.cs`:

(a) `TestSrrEditorViewModel` constructor (currently takes `IImagePreviewService imagePreview`) — change the parameter and base call to the file-preview service:
```csharp
    private sealed class TestSrrEditorViewModel(ISrrEditingService srrEditing, IFileDialogService fileDialog, ITempDirectoryService tempDir, IFilePreviewService filePreview)
        : SrrEditorViewModel(srrEditing, fileDialog, tempDir, filePreview)
    {
```
(Keep the rest of the subclass — `DummyWorkingPath`, the overrides — unchanged.)

(b) `CreateVm` — pass a throwaway file-preview service:
```csharp
        return new TestSrrEditorViewModel(editing, dialog, new NoOpTempDirectoryService(), new RecordingFilePreviewService());
```

(c) `CreateImageVm` and `WithSelectedStored` — change the `out RecordingImagePreviewService preview` parameter type to `out RecordingFilePreviewService preview`, and `preview = new RecordingFilePreviewService();` inside `CreateImageVm`. (Rename of the helpers is optional; keeping the names is fine.)

(d) In `EditCommands_DisabledWithoutSelection`, change the preview assertion:
```csharp
        Assert.False(vm.PreviewStoredFileCommand.CanExecute(null));
```

(e) Replace the four preview tests with these (note the non-image case now **enables**, and all references use `PreviewStoredFileCommand`):
```csharp
    // ── Preview command ─────────────────────────────────────

    [Fact]
    public void PreviewCommand_SingleImageSelected_IsEnabled()
    {
        TestSrrEditorViewModel vm = WithSelectedStored("proof.jpg", out _, out _);
        Assert.True(vm.PreviewStoredFileCommand.CanExecute(null));
    }

    [Fact]
    public void PreviewCommand_SingleNonImageSelected_IsEnabled()
    {
        TestSrrEditorViewModel vm = WithSelectedStored("readme.nfo", out _, out _);
        Assert.True(vm.PreviewStoredFileCommand.CanExecute(null));
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

        Assert.False(vm.PreviewStoredFileCommand.CanExecute(null));
    }

    [Fact]
    public async Task PreviewCommand_ForwardsBytesAndName()
    {
        TestSrrEditorViewModel vm = WithSelectedStored("readme.nfo", out FakeSrrEditingService editing, out RecordingFilePreviewService preview);
        editing.BytesToReturn = [0x09, 0x08, 0x07];

        await vm.PreviewStoredFileCommand.ExecuteAsync(null);

        (byte[] data, string fileName) = Assert.Single(preview.Calls);
        Assert.Equal(new byte[] { 0x09, 0x08, 0x07 }, data);
        Assert.Equal("readme.nfo", fileName);
        Assert.NotNull(editing.LastRead);
        Assert.Equal((TestSrrEditorViewModel.DummyWorkingPath, "readme.nfo"), editing.LastRead.Value);
    }
```

- [ ] **Step 3: Run the wizard tests to verify they fail**

Run: `dotnet test ReScene.NET.Tests/ReScene.NET.Tests.csproj -p:BaseOutputPath=bin2/ --filter "FullyQualifiedName~SrrEditorViewModelTests"`
Expected: FAIL to compile — `SrrEditorViewModel` has no `IFilePreviewService` constructor and no `PreviewStoredFileCommand` yet.

- [ ] **Step 4: Update `SrrEditorViewModel`**

In `ReScene.NET/ViewModels/SrrEditorViewModel.cs`:

(a) Change the primary constructor's last parameter and backing field from the image service to the file-preview service:
```csharp
public partial class SrrEditorViewModel(ISrrEditingService srrEditing, IFileDialogService fileDialog, ITempDirectoryService tempDir, IFilePreviewService filePreview) : ViewModelBase
{
    private readonly ISrrEditingService _srrEditing = srrEditing;
    private readonly IFileDialogService _fileDialog = fileDialog;
    private readonly ITempDirectoryService _tempDir = tempDir;
    private readonly IFilePreviewService _filePreview = filePreview;
```
(Keep the other three fields exactly; only the fourth changes.)

(b) Delete the `HasSingleImageSelection` method (the `HasSingleSelection` method just above it stays):
```csharp
    // DELETE these lines:
    //   /// <summary>True when exactly one selected stored file is a previewable image.</summary>
    //   private bool HasSingleImageSelection()
    //       => SelectedStoredFiles.Count == 1 && ImagePreviewSupport.IsSupported(SelectedStoredFiles[0].Name);
```

(c) Replace the preview command (currently `[RelayCommand(CanExecute = nameof(HasSingleImageSelection))] private async Task PreviewStoredImageAsync()`) with:
```csharp
    [RelayCommand(CanExecute = nameof(HasSingleSelection))]
    private async Task PreviewStoredFileAsync()
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

            _filePreview.Preview(bytes, name);
        }
        catch (Exception ex)
        {
            Log($"Preview failed for \"{name}\": {ex.Message}");
        }
    }
```

(d) In `SetSelection`, rename the notify call:
```csharp
        PreviewStoredFileCommand.NotifyCanExecuteChanged();
```

(e) Remove the now-unused `using ReScene.NET.Helpers;` at the top of the file (it was only used by `ImagePreviewSupport`). If the build later reports it is still needed, keep it — but `AppendLogEntry` comes from `ViewModelBase`, not Helpers, so it should be removable.

- [ ] **Step 5: Update the wizard panel**

In `ReScene.NET/Views/Wizards/StoredFilesManagePanel.xaml`, change the "Preview…" button's command binding from `PreviewStoredImageCommand` to `PreviewStoredFileCommand` (find `Command="{Binding PreviewStoredImageCommand}"` on the `<Button Content="Preview…" …>` and rename it).

In `ReScene.NET/Views/Wizards/StoredFilesManagePanel.xaml.cs`, update `StoredFilesGrid_MouseDoubleClick` to use the renamed command (and update the comment from "image row" to "row"):
```csharp
    // Double-clicking a row opens the preview, mirroring the Preview… button.
    private void StoredFilesGrid_MouseDoubleClick(object _, MouseButtonEventArgs e)
    {
        // Only act on double-clicks that land on a data row (not column headers or empty space).
        if (e.OriginalSource is not DependencyObject source
            || FindAncestor<DataGridRow>(source) is null)
        {
            return;
        }

        if (DataContext is SrrEditorViewModel vm && vm.PreviewStoredFileCommand.CanExecute(null))
        {
            vm.PreviewStoredFileCommand.Execute(null);
            e.Handled = true;
        }
    }
```

- [ ] **Step 6: Wire `MainWindowViewModel`**

In `ReScene.NET/ViewModels/MainWindowViewModel.cs`, after the existing `var imagePreviewService = new ImagePreviewService(fileDialog);` line, add:
```csharp
        var filePreviewService = new FilePreviewService();
```
Then change the `SrrEditorViewModel` construction (the Beginner shell, currently passing `imagePreviewService`) to pass `filePreviewService`:
```csharp
            SrrEditor = new SrrEditorViewModel(srrEditingService, fileDialog, tempDir, filePreviewService),
```
Leave the `Inspector = new InspectorViewModel(..., imagePreviewService, ...)` line unchanged (the Inspector keeps the image service). If a search shows any other `new SrrEditorViewModel(` call site, update it to pass `filePreviewService` too.

- [ ] **Step 7: Run the wizard tests + full suite**

Run: `dotnet test ReScene.NET.Tests/ReScene.NET.Tests.csproj -p:BaseOutputPath=bin2/`
Expected: all PASS (the updated `SrrEditorViewModelTests`, plus the unchanged Inspector tests that still use `RecordingImagePreviewService`). Inspect for NEW `warning CS`/`warning CA` (especially an IDE0005 unused-using in `SrrEditorViewModel`) and fix.

- [ ] **Step 8: Commit**

```bash
cd E:/Projects/ReScene.NET
git add ReScene.NET/ViewModels/SrrEditorViewModel.cs ReScene.NET/Views/Wizards/StoredFilesManagePanel.xaml ReScene.NET/Views/Wizards/StoredFilesManagePanel.xaml.cs ReScene.NET/ViewModels/MainWindowViewModel.cs ReScene.NET.Tests/TestDoubles.cs ReScene.NET.Tests/SrrEditorViewModelTests.cs
git commit -m "feat(edit-srr): Preview any stored file via the tabbed file-preview dialog

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>"
```

---

## Task 6: Full verification + cleanup

**Files:** none (verification only)

- [ ] **Step 1: Build the whole solution (warning sweep)**

Run: `dotnet build ReScene.NET.slnx -p:BaseOutputPath=bin2/`
Expected: `Build succeeded`, `0 Error(s)`. Grep the full output for `warning` (both `warning CS` and `warning CA`) and fix any attributable to this work.

- [ ] **Step 2: Run all tests (lib + app)**

```bash
dotnet test ReScene.Lib/ReScene.Tests/ReScene.Tests.csproj -p:BaseOutputPath=bin2/
dotnet test ReScene.NET.Tests/ReScene.NET.Tests.csproj -p:BaseOutputPath=bin2/
```
Expected: both PASS, 0 failures. (Lib is unchanged.)

- [ ] **Step 3: Delete all `bin2` folders**

Run: `find E:/Projects/ReScene.NET -type d -name bin2 -prune -exec rm -rf {} + 2>/dev/null`
Expected: no output; `git status` clean except intended (already-committed) changes.

- [ ] **Step 4: Manual smoke test (ask the user)**

Since the app is running, ask the user to confirm after a rebuild/restart:
1. Edit-SRR wizard → Manage step → select a single **.nfo** → "Preview…" enabled → opens on the **Hex** tab; the **Text** tab shows the NFO (try **CP437** for box-art); there is **no Image tab**.
2. Select a single **proof .jpg** → "Preview…" → opens on **Hex**; the **Image** tab is present and shows the image; Text/Hex also work.
3. Multi-select → "Preview…" disabled. Double-clicking a row opens the same dialog.
4. The Inspector's "View Image" still works as before (image-only).

---

## Self-review notes

- **Spec coverage:** `ByteArrayDataSource` (Task 1); shared `ImageDecoder` + `ImagePreviewService` refactor (Task 2); `FilePreviewViewModel` with hex/text/image state, default Hex, lazy-free eager text decode, encoding re-decode (Task 3); `IFilePreviewService`/`FilePreviewService`/`FilePreviewWindow` with collapsed Image tab + 1 MB text cap + Bytes/Row + word-wrap (Task 4); wizard ungating + rename + wiring + Inspector untouched (Task 5); verification (Task 6). All spec test cases present (data-source reads; VM tab-availability/text/encoding; wizard any-file gating + forwarding).
- **Type/name consistency:** `ByteArrayDataSource(byte[])`; `ImageDecoder.TryDecode(byte[]) → BitmapSource?`; `IFilePreviewService.Preview(byte[], string)`; `FilePreviewViewModel(byte[], string, BitmapSource?)` with `HexDataSource`/`HexBlockOffset`/`HexBlockLength`/`HexBytesPerLine`/`TextEncodings`/`SelectedEncoding`/`TextWordWrap`/`TextViewContent`/`TextViewTruncated`/`Image`/`HasImageTab`/`TitleText`/`StatusText`; wizard command `PreviewStoredFileCommand` gated by `HasSingleSelection`; test double `RecordingFilePreviewService`.
- **Constructor coupling:** Task 5 changes `SrrEditorViewModel`'s 4th ctor parameter; the same task updates `MainWindowViewModel` and all `SrrEditorViewModelTests` factories, so the build stays green within the task. The Inspector's `InspectorViewModel` keeps `IImagePreviewService` (unchanged), and `MainWindowViewModel` constructs both services.
- **No library/submodule change:** entirely in the parent repo; public-API snapshot baseline unaffected.
