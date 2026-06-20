# Inspector Text View Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add a "Text" view alongside the Inspector's Hex view that decodes the currently-selected block as text with a user-selectable encoding.

**Architecture:** A pure `TextDecoder` reads the selected block from the existing `IHexDataSource` slice and decodes it with a chosen `Encoding`; a `TextEncodingOptions` provider supplies the curated encoding list (registering the CodePages provider for CP437/Windows-1252). `InspectorViewModel` exposes the toggle/encoding/wrap/content state and decodes lazily (only in Text mode). The bottom Inspector panel gains a Hex/Text toggle that switches between the existing `HexViewControl` and a read-only monospace `TextBox`.

**Tech Stack:** .NET 10, WPF, CommunityToolkit.Mvvm (`[ObservableProperty]`), xUnit. New package: `System.Text.Encoding.CodePages`.

**Spec:** `docs/superpowers/specs/2026-06-20-inspector-text-view-design.md`

---

## CRITICAL build/test constraint (read before any `dotnet` command)

The user keeps the app running, which **locks `bin/`**. Therefore:
- **ALWAYS** build/test with `-p:BaseOutputPath=bin2/`.
- **NEVER** kill the running app.
- After the final task, delete every `bin2` folder:
  ```bash
  find E:/Projects/ReScene.NET -type d -name bin2 -prune -exec rm -rf {} + 2>/dev/null
  ```

This is an **app-only** change — no `ReScene.Lib` (submodule) edits, no public-API change. All work and commits are in the **parent repo** `E:/Projects/ReScene.NET`. The feature branch is created in setup (below). This is Git Bash on Windows. Commit messages end with:

```
Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>
```

Keep the `NeWbY100` gh account active.

## Branch setup (do once, before Task 1)

```bash
cd E:/Projects/ReScene.NET
git checkout -b feature/inspector-text-view
```

## Context the implementer needs

- `IHexDataSource` (namespace `ReScene.Hex`, defined in the library, referenced by the app):
  ```csharp
  public interface IHexDataSource : IDisposable
  {
      long Length { get; }
      int Read(long position, byte[] buffer, int offset, int count);
  }
  ```
  `Read` returns the number of bytes read (0 at/after end). `InspectorViewModel.HexDataSource` is a `HexDataSourceSlice` over the file that reads **relative** positions `0..HexBlockLength`.
- `InspectorViewModel` (`ReScene.NET/ViewModels/InspectorViewModel.cs`): a `public partial class` using CommunityToolkit.Mvvm. It already has `[ObservableProperty]` `HexDataSource` (`IHexDataSource?`), `HexBlockOffset` (`long`), `HexBlockLength` (`long`). It raises hex-block changes from `OnSelectedTreeNodeChanged`, `LoadFile`, and `CloseFile`. The app project has `<Using Include="System.IO" />` and `ImplicitUsings`.
- Converters registered in `App.xaml`: `BoolToVisibility` (`BooleanToVisibilityConverter`) and `InverseBoolToVisibility`. The app uses `MonoFontFamily`, `FontSizeCaption`, `ForegroundSecondary`, `SurfaceBackground`, `BorderSeparator`, `GhostButton`, `PanelHeaderBar`, `PanelHeaderText` dynamic resources.
- Test infra (`ReScene.NET.Tests`): `TempDirTestBase` (exposes `TempDir`), `NoOpFileDialogService`, `RecordingImagePreviewService` (in `TestDoubles.cs`). `InspectorViewModelImageTests.cs` has reusable private stubs (`FakeReadEditingService`, `StubVerifyService`, `StubPropertyExportService`), a `CreateVm(FakeReadEditingService, RecordingImagePreviewService)` factory, and `SRREditingServiceImageTests.WriteMinimalSrr(dir, srrName, storedName, data)` (internal static) for building a parseable SRR with one stored file.

---

## File map

**Create**
- `ReScene.NET/Helpers/TextEncodingOption.cs` — `TextEncodingOption` record + `TextEncodingOptions` provider (curated list; registers CodePages provider).
- `ReScene.NET/Helpers/TextDecoder.cs` — pure decode of an `IHexDataSource` region with an `Encoding`.
- `ReScene.NET.Tests/TextEncodingOptionsTests.cs`
- `ReScene.NET.Tests/TextDecoderTests.cs` (includes a byte-array `IHexDataSource` test double)

**Modify**
- `ReScene.NET/ReScene.NET.csproj` — add `System.Text.Encoding.CodePages`.
- `ReScene.NET/ViewModels/InspectorViewModel.cs` — toggle/encoding/wrap/content state + lazy decode.
- `ReScene.NET/Views/InspectorView.xaml` — Hex/Text toggle + Text content.
- `ReScene.NET.Tests/InspectorViewModelImageTests.cs` — add Text-view VM tests (reuses its stubs/factory).

---

## Task 1: Encoding options provider + package

**Files:**
- Modify: `ReScene.NET/ReScene.NET.csproj`
- Create: `ReScene.NET/Helpers/TextEncodingOption.cs`
- Test: `ReScene.NET.Tests/TextEncodingOptionsTests.cs`

- [ ] **Step 1: Add the CodePages package**

Run (modifies the csproj via the CLI, per the project's package-management convention):
```bash
cd E:/Projects/ReScene.NET
dotnet add ReScene.NET/ReScene.NET.csproj package System.Text.Encoding.CodePages --version 9.0.4
```
Expected: the package is added; a `<PackageReference Include="System.Text.Encoding.CodePages" Version="9.0.4" />` appears in `ReScene.NET.csproj`.

- [ ] **Step 2: Write the failing tests**

Create `ReScene.NET.Tests/TextEncodingOptionsTests.cs`:
```csharp
using ReScene.NET.Helpers;

namespace ReScene.NET.Tests;

public class TextEncodingOptionsTests
{
    [Fact]
    public void All_StartsWithUtf8_AndHasSevenEntries()
    {
        var all = TextEncodingOptions.All;

        Assert.Equal(7, all.Count);
        Assert.Equal("UTF-8", all[0].DisplayName);
        Assert.Contains(all, e => e.DisplayName == "CP437 (DOS)");
        Assert.Contains(all, e => e.DisplayName == "ISO-8859-1 (Latin-1)");
    }

    [Fact]
    public void Cp437_DecodesBoxDrawingBytes()
    {
        var cp437 = TextEncodingOptions.All.First(e => e.DisplayName == "CP437 (DOS)").Encoding;

        // CP437: 0xC9 = ╔ (U+2554), 0xB0 = ░ (U+2591)
        string text = cp437.GetString([0xC9, 0xB0]);

        Assert.Equal("╔░", text);
    }

    [Fact]
    public void AllEntries_HaveNonNullEncoding()
    {
        Assert.All(TextEncodingOptions.All, e => Assert.NotNull(e.Encoding));
    }
}
```

- [ ] **Step 3: Run tests to verify they fail**

Run: `dotnet test ReScene.NET.Tests/ReScene.NET.Tests.csproj -p:BaseOutputPath=bin2/ --filter "FullyQualifiedName~TextEncodingOptionsTests"`
Expected: FAIL to compile — `TextEncodingOptions` does not exist.

- [ ] **Step 4: Implement the provider**

Create `ReScene.NET/Helpers/TextEncodingOption.cs`:
```csharp
using System.Text;

namespace ReScene.NET.Helpers;

/// <summary>A selectable text encoding: a human-friendly name plus the backing <see cref="Encoding"/>.</summary>
public sealed record TextEncodingOption(string DisplayName, Encoding Encoding);

/// <summary>
/// The curated set of encodings offered by the Inspector's Text view. CP437 and Windows-1252 are not
/// built into .NET, so the CodePages provider is registered once before they are resolved.
/// </summary>
public static class TextEncodingOptions
{
    private static readonly Lazy<IReadOnlyList<TextEncodingOption>> _all = new(Build);

    /// <summary>The encodings in display order; UTF-8 (the default) is first.</summary>
    public static IReadOnlyList<TextEncodingOption> All => _all.Value;

    private static IReadOnlyList<TextEncodingOption> Build()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

        return
        [
            new TextEncodingOption("UTF-8", Encoding.UTF8),
            new TextEncodingOption("UTF-16 LE", Encoding.Unicode),
            new TextEncodingOption("UTF-16 BE", Encoding.BigEndianUnicode),
            new TextEncodingOption("ASCII", Encoding.ASCII),
            new TextEncodingOption("Windows-1252", Encoding.GetEncoding(1252)),
            new TextEncodingOption("ISO-8859-1 (Latin-1)", Encoding.Latin1),
            new TextEncodingOption("CP437 (DOS)", Encoding.GetEncoding(437)),
        ];
    }
}
```

- [ ] **Step 5: Run tests to verify they pass**

Run: `dotnet test ReScene.NET.Tests/ReScene.NET.Tests.csproj -p:BaseOutputPath=bin2/ --filter "FullyQualifiedName~TextEncodingOptionsTests"`
Expected: PASS (3 tests). Inspect output for any NEW `warning CS`/`warning CA` and fix.

- [ ] **Step 6: Commit**

```bash
cd E:/Projects/ReScene.NET
git add ReScene.NET/ReScene.NET.csproj ReScene.NET/Helpers/TextEncodingOption.cs ReScene.NET.Tests/TextEncodingOptionsTests.cs
git commit -m "feat(inspector): add curated text-encoding options (incl. CP437)

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>"
```

---

## Task 2: TextDecoder

**Files:**
- Create: `ReScene.NET/Helpers/TextDecoder.cs`
- Test: `ReScene.NET.Tests/TextDecoderTests.cs`

- [ ] **Step 1: Write the failing tests**

Create `ReScene.NET.Tests/TextDecoderTests.cs`. It includes a tiny byte-array `IHexDataSource` test double:
```csharp
using System.Text;
using ReScene.Hex;
using ReScene.NET.Helpers;

namespace ReScene.NET.Tests;

public class TextDecoderTests
{
    // Minimal in-memory IHexDataSource over a byte[], for decoding tests.
    private sealed class ByteArrayDataSource(byte[] data) : IHexDataSource
    {
        public long Length => data.Length;

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

        public void Dispose() { }
    }

    [Fact]
    public void Decode_Utf8_RoundTrips()
    {
        byte[] data = Encoding.UTF8.GetBytes("Héllo, NFO");
        var source = new ByteArrayDataSource(data);

        (string text, bool truncated) = TextDecoder.Decode(source, data.Length, Encoding.UTF8, 1024);

        Assert.Equal("Héllo, NFO", text);
        Assert.False(truncated);
    }

    [Fact]
    public void Decode_Utf16Le_RoundTrips()
    {
        byte[] data = Encoding.Unicode.GetBytes("Ωmega");
        var source = new ByteArrayDataSource(data);

        (string text, bool truncated) = TextDecoder.Decode(source, data.Length, Encoding.Unicode, 1024);

        Assert.Equal("Ωmega", text);
        Assert.False(truncated);
    }

    [Fact]
    public void Decode_Cp437_DecodesArtBytes()
    {
        var cp437 = TextEncodingOptions.All.First(e => e.DisplayName == "CP437 (DOS)").Encoding;
        byte[] data = [0xC9, 0xB0]; // ╔ ░
        var source = new ByteArrayDataSource(data);

        (string text, _) = TextDecoder.Decode(source, data.Length, cp437, 1024);

        Assert.Equal("╔░", text);
    }

    [Fact]
    public void Decode_LongerThanMax_TruncatesAndFlags()
    {
        byte[] data = Encoding.ASCII.GetBytes("ABCDEFGHIJ"); // 10 bytes
        var source = new ByteArrayDataSource(data);

        (string text, bool truncated) = TextDecoder.Decode(source, data.Length, Encoding.ASCII, 4);

        Assert.Equal("ABCD", text);
        Assert.True(truncated);
    }

    [Fact]
    public void Decode_NullSource_ReturnsEmpty()
    {
        (string text, bool truncated) = TextDecoder.Decode(null, 10, Encoding.UTF8, 1024);

        Assert.Equal(string.Empty, text);
        Assert.False(truncated);
    }

    [Fact]
    public void Decode_ZeroLength_ReturnsEmpty()
    {
        var source = new ByteArrayDataSource([1, 2, 3]);

        (string text, bool truncated) = TextDecoder.Decode(source, 0, Encoding.UTF8, 1024);

        Assert.Equal(string.Empty, text);
        Assert.False(truncated);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test ReScene.NET.Tests/ReScene.NET.Tests.csproj -p:BaseOutputPath=bin2/ --filter "FullyQualifiedName~TextDecoderTests"`
Expected: FAIL to compile — `TextDecoder` does not exist.

- [ ] **Step 3: Implement the decoder**

Create `ReScene.NET/Helpers/TextDecoder.cs`:
```csharp
using System.Text;
using ReScene.Hex;

namespace ReScene.NET.Helpers;

/// <summary>
/// Decodes a region of an <see cref="IHexDataSource"/> as text. Pure and side-effect free so the
/// Inspector's Text view can be unit-tested without WPF.
/// </summary>
public static class TextDecoder
{
    /// <summary>
    /// Reads up to <paramref name="maxBytes"/> bytes from the start of <paramref name="source"/>
    /// (relative positions <c>0..</c>) and decodes them with <paramref name="encoding"/>.
    /// </summary>
    /// <returns>
    /// The decoded text, and whether the region was truncated (i.e. <paramref name="length"/> exceeded
    /// <paramref name="maxBytes"/>). Returns <c>("", false)</c> when there is nothing to read.
    /// </returns>
    public static (string Text, bool Truncated) Decode(
        IHexDataSource? source, long length, Encoding encoding, int maxBytes)
    {
        ArgumentNullException.ThrowIfNull(encoding);

        if (source is null || length <= 0 || maxBytes <= 0)
        {
            return (string.Empty, false);
        }

        int toRead = (int)Math.Min(length, maxBytes);
        byte[] buffer = new byte[toRead];

        int total = 0;
        while (total < toRead)
        {
            int read = source.Read(total, buffer, total, toRead - total);
            if (read <= 0)
            {
                break;
            }

            total += read;
        }

        string text = encoding.GetString(buffer, 0, total);
        return (text, length > maxBytes);
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test ReScene.NET.Tests/ReScene.NET.Tests.csproj -p:BaseOutputPath=bin2/ --filter "FullyQualifiedName~TextDecoderTests"`
Expected: PASS (6 tests). Inspect output for NEW `warning CS`/`warning CA` and fix.

- [ ] **Step 5: Commit**

```bash
cd E:/Projects/ReScene.NET
git add ReScene.NET/Helpers/TextDecoder.cs ReScene.NET.Tests/TextDecoderTests.cs
git commit -m "feat(inspector): add TextDecoder for decoding a data-source region

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>"
```

---

## Task 3: InspectorViewModel wiring

**Files:**
- Modify: `ReScene.NET/ViewModels/InspectorViewModel.cs`
- Test: `ReScene.NET.Tests/InspectorViewModelImageTests.cs`

- [ ] **Step 1: Write the failing tests**

Append these tests to the `InspectorViewModelImageTests` class in `ReScene.NET.Tests/InspectorViewModelImageTests.cs` (reusing its `FakeReadEditingService`, `StubVerifyService`, `StubPropertyExportService`, `CreateVm`, and `SRREditingServiceImageTests.WriteMinimalSrr`). Add `using System.Text;` to the file's usings if not already present.

```csharp
    [Fact]
    public void TextView_FreshVm_DefaultsToUtf8Inactive()
    {
        using InspectorViewModel vm = CreateVm(new FakeReadEditingService(), new RecordingImagePreviewService());

        Assert.False(vm.IsTextViewActive);
        Assert.True(vm.IsHexViewActive);
        Assert.False(vm.TextWordWrap);
        Assert.Equal("UTF-8", vm.SelectedEncoding.DisplayName);
        Assert.Equal(string.Empty, vm.TextViewContent);
    }

    [Fact]
    public void TextView_WhenActivated_DecodesSelectedBlock()
    {
        byte[] payload = Encoding.ASCII.GetBytes("MARKER_TEXT_12345");
        string srr = SRREditingServiceImageTests.WriteMinimalSrr(TempDir, "note.srr", "note.nfo", payload);

        using InspectorViewModel vm = CreateVm(new FakeReadEditingService(), new RecordingImagePreviewService());
        vm.LoadFile(srr);
        vm.SelectedTreeNode = vm.TreeRoots.Flatten()
            .First(n => n.Tag is SRRStoredFileBlock b && b.FileName == "note.nfo");

        vm.IsTextViewActive = true;

        // The selected block region (stored file) decodes to text containing the payload.
        Assert.Contains("MARKER_TEXT_12345", vm.TextViewContent, StringComparison.Ordinal);
    }

    [Fact]
    public void TextView_ChangingEncoding_Redecodes()
    {
        // 0xC9 → CP437 '╔' (U+2554) vs Latin-1 'É' (U+00C9): proves a re-decode on encoding change.
        byte[] payload = [0xC9];
        string srr = SRREditingServiceImageTests.WriteMinimalSrr(TempDir, "enc.srr", "enc.bin", payload);

        using InspectorViewModel vm = CreateVm(new FakeReadEditingService(), new RecordingImagePreviewService());
        vm.LoadFile(srr);
        vm.SelectedTreeNode = vm.TreeRoots.Flatten()
            .First(n => n.Tag is SRRStoredFileBlock b && b.FileName == "enc.bin");
        vm.IsTextViewActive = true;

        vm.SelectedEncoding = vm.TextEncodings.First(e => e.DisplayName == "CP437 (DOS)");
        Assert.Contains('╔', vm.TextViewContent);

        vm.SelectedEncoding = vm.TextEncodings.First(e => e.DisplayName == "ISO-8859-1 (Latin-1)");
        Assert.Contains('É', vm.TextViewContent);
        Assert.DoesNotContain('╔', vm.TextViewContent);
    }

    [Fact]
    public void TextView_InactiveByDefault_DoesNotDecodeOnSelection()
    {
        byte[] payload = Encoding.ASCII.GetBytes("SHOULD_NOT_DECODE");
        string srr = SRREditingServiceImageTests.WriteMinimalSrr(TempDir, "lazy.srr", "lazy.nfo", payload);

        using InspectorViewModel vm = CreateVm(new FakeReadEditingService(), new RecordingImagePreviewService());
        vm.LoadFile(srr);
        vm.SelectedTreeNode = vm.TreeRoots.Flatten()
            .First(n => n.Tag is SRRStoredFileBlock b && b.FileName == "lazy.nfo");

        // Still in Hex mode → no decode happened.
        Assert.Equal(string.Empty, vm.TextViewContent);
    }
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test ReScene.NET.Tests/ReScene.NET.Tests.csproj -p:BaseOutputPath=bin2/ --filter "FullyQualifiedName~InspectorViewModelImageTests.TextView"`
Expected: FAIL to compile — `IsTextViewActive`, `IsHexViewActive`, `TextWordWrap`, `SelectedEncoding`, `TextEncodings`, `TextViewContent` do not exist.

- [ ] **Step 3: Add the using and the properties**

In `ReScene.NET/ViewModels/InspectorViewModel.cs`, ensure `using ReScene.NET.Helpers;` is present (it is — `FieldGuidance`/etc. live there; if missing, add it).

Add these members near the other hex `[ObservableProperty]` declarations (e.g. just after `HexBytesPerLine`/`ShowHexView`):
```csharp
    private const int TextViewMaxBytes = 1024 * 1024; // 1 MB

    /// <summary>The encodings offered by the Text view (UTF-8 first / default).</summary>
    public IReadOnlyList<TextEncodingOption> TextEncodings { get; } = TextEncodingOptions.All;

    [ObservableProperty]
    public partial TextEncodingOption SelectedEncoding { get; set; } = TextEncodingOptions.All[0];

    /// <summary>True when the bottom panel shows the Text view; false shows the Hex view.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsHexViewActive))]
    public partial bool IsTextViewActive { get; set; }

    /// <summary>Convenience inverse of <see cref="IsTextViewActive"/> for the Hex toggle and hex-only chrome.</summary>
    public bool IsHexViewActive => !IsTextViewActive;

    [ObservableProperty]
    public partial bool TextWordWrap { get; set; }

    [ObservableProperty]
    public partial string TextViewContent { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool TextViewTruncated { get; set; }
```

- [ ] **Step 4: Add the decode method and change hooks**

Still in `InspectorViewModel.cs`, add the update method (place it near `SetHexBlock`/`ShowFullHex`):
```csharp
    private void UpdateTextView()
    {
        if (!IsTextViewActive || HexDataSource is null || HexBlockLength <= 0)
        {
            TextViewContent = string.Empty;
            TextViewTruncated = false;
            return;
        }

        (TextViewContent, TextViewTruncated) = TextDecoder.Decode(
            HexDataSource, HexBlockLength, SelectedEncoding.Encoding, TextViewMaxBytes);
    }

    partial void OnIsTextViewActiveChanged(bool value) => UpdateTextView();

    partial void OnSelectedEncodingChanged(TextEncodingOption value)
    {
        if (IsTextViewActive)
        {
            UpdateTextView();
        }
    }
```

Now call `UpdateTextView()` wherever the selected hex block changes:

(a) At the **end** of `OnSelectedTreeNodeChanged` (after the existing `ExportSelectedPropertiesCommand.NotifyCanExecuteChanged();` final line), add:
```csharp
        UpdateTextView();
```

(b) In `LoadFile`, at the end of the `try` block (after the status/warning lines, just before the `catch`), add:
```csharp
            UpdateTextView();
```

(c) In `CloseFile` (after `HexDataSource = null;`), add:
```csharp
        UpdateTextView();
```

(Decoding stays lazy: when `IsTextViewActive` is false these calls just clear the two text properties.)

- [ ] **Step 5: Run tests to verify they pass**

Run: `dotnet test ReScene.NET.Tests/ReScene.NET.Tests.csproj -p:BaseOutputPath=bin2/ --filter "FullyQualifiedName~InspectorViewModelImageTests.TextView"`
Expected: PASS (4 new tests). Then run the whole suite to confirm nothing regressed:
`dotnet test ReScene.NET.Tests/ReScene.NET.Tests.csproj -p:BaseOutputPath=bin2/`
Expected: all PASS. Inspect for NEW `warning CS`/`warning CA` and fix.

- [ ] **Step 6: Commit**

```bash
cd E:/Projects/ReScene.NET
git add ReScene.NET/ViewModels/InspectorViewModel.cs ReScene.NET.Tests/InspectorViewModelImageTests.cs
git commit -m "feat(inspector): Text view state and lazy decoding in the view-model

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>"
```

---

## Task 4: View — Hex/Text toggle + Text content

Restructure the bottom **Hex View** panel so its header carries a Hex/Text toggle and mode-specific controls, and its content switches between the `HexViewControl` and a read-only `TextBox`. Build-verified (no new unit tests; view wiring).

**Files:**
- Modify: `ReScene.NET/Views/InspectorView.xaml`

- [ ] **Step 1: Replace the Hex View panel**

In `ReScene.NET/Views/InspectorView.xaml`, replace the entire `<!-- Hex view panel -->` `Border` (the `<Border Grid.Row="3" Style="{StaticResource PanelSection}">…</Border>` block — currently the panel header `DockPanel` with the "Hex View" title + Bytes/Row combo, then the inner `DockPanel` with the search `Border` and the `HexViewControl`) with this:

```xml
        <!-- Hex / Text view panel -->
        <Border Grid.Row="3"
                Style="{StaticResource PanelSection}">
          <DockPanel>
            <!-- Panel header: Hex/Text toggle + mode-specific controls -->
            <Border DockPanel.Dock="Top" Style="{StaticResource PanelHeaderBar}">
              <DockPanel>
                <StackPanel Orientation="Horizontal" VerticalAlignment="Center">
                  <RadioButton Content="Hex" GroupName="InspectorViewMode"
                               IsChecked="{Binding IsHexViewActive, Mode=OneWay}"
                               VerticalAlignment="Center" Margin="0,0,8,0" />
                  <RadioButton Content="Text" GroupName="InspectorViewMode"
                               IsChecked="{Binding IsTextViewActive, Mode=TwoWay}"
                               VerticalAlignment="Center" />
                </StackPanel>

                <!-- Hex-mode: Bytes/Row -->
                <StackPanel Orientation="Horizontal" HorizontalAlignment="Right"
                            Margin="0,0,4,0"
                            Visibility="{Binding IsHexViewActive, Converter={StaticResource BoolToVisibility}}">
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

                <!-- Text-mode: Encoding + word wrap -->
                <StackPanel Orientation="Horizontal" HorizontalAlignment="Right"
                            Margin="0,0,4,0"
                            Visibility="{Binding IsTextViewActive, Converter={StaticResource BoolToVisibility}}">
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
              </DockPanel>
            </Border>

            <!-- Content: Hex view OR Text view -->
            <Grid>
              <!-- Hex content -->
              <DockPanel Visibility="{Binding IsHexViewActive, Converter={StaticResource BoolToVisibility}}">
                <Border DockPanel.Dock="Top"
                        Visibility="{Binding IsHexSearchVisible, Converter={StaticResource BoolToVisibility}}"
                        Background="{DynamicResource SurfaceBackground}"
                        BorderBrush="{DynamicResource BorderSeparator}"
                        BorderThickness="0,0,0,1"
                        Padding="4"
                        IsVisibleChanged="OnHexSearchBarVisibleChanged">
                  <DockPanel>
                    <Button DockPanel.Dock="Right" Content="Close"
                            Command="{Binding HideHexSearchCommand}"
                            Style="{StaticResource GhostButton}"
                            MinWidth="55" Margin="4,0,0,0" />
                    <Button DockPanel.Dock="Right" Content="Prev"
                            Command="{Binding FindPreviousCommand}"
                            Style="{StaticResource GhostButton}"
                            MinWidth="55" Margin="4,0,0,0" />
                    <Button DockPanel.Dock="Right" Content="Next"
                            Command="{Binding FindNextCommand}"
                            Style="{StaticResource PrimaryButton}"
                            MinWidth="55" Margin="4,0,0,0" />
                    <TextBlock DockPanel.Dock="Right"
                               Text="{Binding HexSearchStatus}"
                               Foreground="{DynamicResource ForegroundSecondary}"
                               VerticalAlignment="Center"
                               Margin="8,0" />
                    <CheckBox DockPanel.Dock="Right" Content="Hex"
                              IsChecked="{Binding HexSearchAsHex}"
                              VerticalAlignment="Center"
                              Margin="4,0,0,0" />
                    <CheckBox DockPanel.Dock="Right" Content="Highlight all"
                              IsChecked="{Binding HighlightAllMatches}"
                              VerticalAlignment="Center"
                              Margin="4,0,0,0" />
                    <TextBox x:Name="HexSearchBox"
                             Text="{Binding HexSearchText, UpdateSourceTrigger=PropertyChanged}"
                             FontFamily="{DynamicResource MonoFontFamily}"
                             VerticalAlignment="Center">
                      <TextBox.InputBindings>
                        <KeyBinding Key="Enter" Command="{Binding FindNextCommand}" />
                        <KeyBinding Key="Enter" Modifiers="Shift" Command="{Binding FindPreviousCommand}" />
                      </TextBox.InputBindings>
                    </TextBox>
                  </DockPanel>
                </Border>

                <controls:HexViewControl DataSource="{Binding HexDataSource}"
                                         BlockOffset="{Binding HexBlockOffset}"
                                         BlockLength="{Binding HexBlockLength}"
                                         SelectionOffset="{Binding HexSelectionOffset}"
                                         SelectionLength="{Binding HexSelectionLength}"
                                         BytesPerLine="{Binding HexBytesPerLine}"
                                         HighlightRanges="{Binding HexMatchRanges}" />
              </DockPanel>

              <!-- Text content -->
              <DockPanel Visibility="{Binding IsTextViewActive, Converter={StaticResource BoolToVisibility}}">
                <Border DockPanel.Dock="Bottom"
                        Visibility="{Binding TextViewTruncated, Converter={StaticResource BoolToVisibility}}"
                        Background="{DynamicResource SurfaceBackground}"
                        BorderBrush="{DynamicResource BorderSeparator}"
                        BorderThickness="0,1,0,0"
                        Padding="6,3">
                  <TextBlock Text="Large block — showing the first 1 MB as text."
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
            </Grid>
          </DockPanel>
        </Border>
```

Notes:
- The Hex/Text toggle uses two `RadioButton`s in one `GroupName`. Only "Text" is two-way bound to `IsTextViewActive`; "Hex" is one-way bound to the computed `IsHexViewActive`. Clicking "Hex" unchecks "Text" (group behavior), which writes `false` back through Text's two-way binding — keeping the VM in sync without an extra converter.
- The hex search `KeyBinding`s on the `UserControl` (Ctrl+F etc.) are unchanged; they drive the hex search box, which now lives inside the hex-only content.
- The truncation note sits at the bottom of the Text content and shows only when `TextViewTruncated` is true.

- [ ] **Step 2: Build to verify it compiles**

Run: `dotnet build ReScene.NET/ReScene.NET.csproj -p:BaseOutputPath=bin2/`
Expected: `Build succeeded`, `0 Error(s)`, no new warnings.

- [ ] **Step 3: Run the full app suite (sanity)**

Run: `dotnet test ReScene.NET.Tests/ReScene.NET.Tests.csproj -p:BaseOutputPath=bin2/`
Expected: all PASS (the VM tests still hold; the XAML change doesn't affect them).

- [ ] **Step 4: Commit**

```bash
cd E:/Projects/ReScene.NET
git add ReScene.NET/Views/InspectorView.xaml
git commit -m "feat(inspector): Hex/Text toggle and Text view in the bottom panel

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>"
```

---

## Task 5: Full verification + cleanup

**Files:** none (verification only)

- [ ] **Step 1: Build the whole solution (warning sweep)**

Run: `dotnet build ReScene.NET.slnx -p:BaseOutputPath=bin2/`
Expected: `Build succeeded`, `0 Error(s)`. Grep the full output for `warning` (not just `warning CS` — analyzer warnings are `warning CA`) and fix any attributable to this work.

- [ ] **Step 2: Run all tests (lib + app)**

```bash
dotnet test ReScene.Lib/ReScene.Tests/ReScene.Tests.csproj -p:BaseOutputPath=bin2/
dotnet test ReScene.NET.Tests/ReScene.NET.Tests.csproj -p:BaseOutputPath=bin2/
```
Expected: both PASS, 0 failures. (Lib is unchanged — it should remain at its current count; app gains the new tests.)

- [ ] **Step 3: Delete all `bin2` folders**

Run: `find E:/Projects/ReScene.NET -type d -name bin2 -prune -exec rm -rf {} + 2>/dev/null`
Expected: no output; `git status` clean except intended (already-committed) changes.

- [ ] **Step 4: Manual smoke test (ask the user)**

Since the app is running, ask the user to confirm in-app after a rebuild/restart:
1. Inspector → load an SRR → select a stored `.nfo`/text block → switch the bottom panel to **Text** → text appears; switching **Encoding** to **CP437 (DOS)** renders box-art correctly; **Word wrap** checkbox wraps/unwraps.
2. Switching back to **Hex** restores the hex view, Bytes/Row, and Ctrl+F search.
3. A very large selection in Text mode shows the "first 1 MB" note.

---

## Self-review notes

- **Spec coverage:** encoding provider incl. CP437 + CodePages registration (Task 1); `TextDecoder` pure decode + truncation (Task 2); VM toggle/encoding/wrap/content + lazy decode at the three block-change sites (Task 3); Hex/Text toggle, encoding combo, word-wrap checkbox (default off via the `NoWrap` base + `TextWordWrap` trigger), read-only monospace TextBox, truncation note (Task 4). All spec test cases are present (encoding list + CP437 glyphs; decoder round-trips/truncation/empty; VM default UTF-8/activation/encoding-switch/laziness). MVP exclusions honored (no search, no persistence, no auto-detect, read-only, capped).
- **Type/name consistency:** `TextEncodingOption(DisplayName, Encoding)`; `TextEncodingOptions.All`; `TextDecoder.Decode(IHexDataSource?, long, Encoding, int) → (string Text, bool Truncated)`; VM members `TextEncodings`, `SelectedEncoding`, `IsTextViewActive`, `IsHexViewActive`, `TextWordWrap`, `TextViewContent`, `TextViewTruncated`, `UpdateTextView()`, `TextViewMaxBytes`. Display names used in tests and XAML match exactly ("UTF-8", "CP437 (DOS)", "ISO-8859-1 (Latin-1)").
- **No ctor change:** `InspectorViewModel`'s constructor is unchanged, so existing test factories (`InspectorViewModelImageTests.CreateVm`, `InspectorViewModelMkvTests.CreateViewModel`, `MainWindowViewModel`) need no edits.
- **No library/submodule change:** entirely in the parent repo; the public-API snapshot baseline is unaffected.
