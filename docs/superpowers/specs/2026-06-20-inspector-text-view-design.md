# Inspector Text View — Design

**Date:** 2026-06-20
**Status:** Approved (brainstorming)

## Goal

Add a "Text" view alongside the existing Hex view in the Inspector's bottom panel.
It decodes the currently-selected block as text using a user-selectable encoding,
so users can read embedded text content (NFO art, SFV, SRS app strings, etc.)
without exporting the block.

## Background

The Inspector's right-hand pane stacks **Properties** (top) over **Hex View**
(bottom). The Hex view is a custom-drawn `HexViewControl`
(`ReScene.NET/Controls/HexViewControl.cs`) bound to:

- `DataSource` — an `IHexDataSource` (`ReScene.Hex.IHexDataSource`, defined in the
  library): `long Length` + `int Read(long position, byte[] buffer, int offset, int count)`.
- `BlockOffset` / `BlockLength` — the region of the file currently shown.

`InspectorViewModel` drives these via `HexDataSource`, `HexBlockOffset`,
`HexBlockLength`. When a tree node is selected, `SetHexBlock(offset, size)` sets
`HexDataSource = new HexDataSourceSlice(_fileDataSource, offset, clampedSize)`
(a windowed view over the file's `MemoryMappedDataSource` that reads **relative**
positions `0..clampedSize`), and `HexBlockOffset`/`HexBlockLength` to the region.
`ShowFullHex()` shows the whole file capped at 100 MB.

The Text view reuses this exact same selected-block region and data source — it
just renders the bytes decoded as text instead of hex.

## Decisions (from brainstorming)

- **Placement:** a **Hex | Text toggle** in the bottom panel; one view visible at a
  time, both rendering the same selected block. (Not a separate stacked panel.)
- **Encodings (curated set):** UTF-8 (**default**), UTF-16 LE, UTF-16 BE, ASCII,
  Windows-1252, ISO-8859-1 (Latin-1), CP437 (DOS/NFO). Requires registering
  `CodePagesEncodingProvider` (CP437, Windows-1252 are not built into .NET core).
- **Rendering:** read-only, monospace `TextBox` with native select/copy; capped at
  ~1 MB with a truncation note for larger blocks.
- **Word-wrap:** a **checkbox**, default **off** (so NFO box-art / fixed-width
  columns are preserved; horizontal scroll when off).
- **No auto-detection** (no BOM sniffing, no `.nfo`→CP437); manual selection only.

## Components (app-only — no library or public-API change)

### 1. `ReScene.NET/Helpers/TextEncodingOption.cs`

```csharp
public sealed record TextEncodingOption(string DisplayName, Encoding Encoding);
```

Plus a static provider:

```csharp
public static class TextEncodingOptions
{
    // Registers CodePagesEncodingProvider once (for CP437 / Windows-1252), then
    // exposes the curated list in display order. UTF-8 first (the default).
    public static IReadOnlyList<TextEncodingOption> All { get; }
}
```

The static initializer calls `Encoding.RegisterProvider(CodePagesEncodingProvider.Instance)`
before resolving CP437 (437) and Windows-1252 (1252). UTF-8 is emitted **without** a
BOM/preamble concern (decoding side only). Display names are human-friendly
(e.g. "UTF-8", "UTF-16 LE", "UTF-16 BE", "ASCII", "Windows-1252",
"ISO-8859-1 (Latin-1)", "CP437 (DOS)").

**New dependency:** add the `System.Text.Encoding.CodePages` NuGet package to
`ReScene.NET/ReScene.NET.csproj`.

### 2. `ReScene.NET/Helpers/TextDecoder.cs` (pure, testable)

```csharp
public static class TextDecoder
{
    public static (string Text, bool Truncated) Decode(
        IHexDataSource source, long length, Encoding encoding, int maxBytes);
}
```

Reads `min(length, maxBytes)` bytes from `source` (relative positions `0..`),
decodes them with `encoding`, and returns the text plus `Truncated = length > maxBytes`.
Returns `("", false)` when `source` is null or `length <= 0`. Decoding uses the
encoding's default replacement behavior — it never throws on invalid sequences.

### 3. `InspectorViewModel` additions (`ReScene.NET/ViewModels/InspectorViewModel.cs`)

- `public IReadOnlyList<TextEncodingOption> TextEncodings { get; } = TextEncodingOptions.All;`
- `[ObservableProperty] TextEncodingOption SelectedEncoding` — initialized to the
  UTF-8 entry from the list.
- `[ObservableProperty] bool IsTextViewActive` — the Hex/Text toggle state (false = Hex).
- `[ObservableProperty] bool TextWordWrap` — default `false`.
- `[ObservableProperty] string TextViewContent` — the decoded text (default `""`).
- `[ObservableProperty] bool TextViewTruncated` and a derived note, e.g.
  `TextViewTruncatedNote` = `"Showing the first {cap} of {HexBlockLength} bytes."`
- A private `const int TextViewMaxBytes = 1024 * 1024;`
- A private `UpdateTextView()` that, when `IsTextViewActive` and `HexDataSource is not null`,
  sets `(TextViewContent, TextViewTruncated)` via
  `TextDecoder.Decode(HexDataSource, HexBlockLength, SelectedEncoding.Encoding, TextViewMaxBytes)`;
  otherwise clears them.

**Recompute triggers** (`UpdateTextView` is called from each):
- `OnIsTextViewActiveChanged` (switching to Text).
- `OnSelectedEncodingChanged` (only meaningful while Text active — guard inside).
- `OnTextWordWrapChanged` does **not** recompute (wrap is a view concern; binding
  handles it) — wrap only flips the TextBox's `TextWrapping`.
- After the selected block is set: at the end of `OnSelectedTreeNodeChanged`, in
  `LoadFile` (after `BuildTree`/hex setup), and in `CloseFile` (clears it). Because
  these set the hex block, call `UpdateTextView()` there so the Text view tracks
  the current selection. (When Text isn't active, `UpdateTextView` is a cheap no-op.)

Decoding is **lazy**: nothing decodes while in Hex mode.

### 4. View (`ReScene.NET/Views/InspectorView.xaml` + `.xaml.cs`)

In the bottom **Hex View** panel:
- The panel header gets a **Hex / Text toggle** (two `RadioButton`s styled as a
  segmented toggle, or `ToggleButton`s) bound to `IsTextViewActive`
  (Hex = false, Text = true).
- The existing **Bytes/Row** combo + the **search bar** + the `HexViewControl` are
  shown only when `IsTextViewActive` is false (`Visibility` via the existing
  `BoolToVisibility` / its inverse converter).
- New **Text** content (shown when `IsTextViewActive` is true):
  - An **Encoding** `ComboBox` bound to `TextEncodings` / `SelectedEncoding`
    (`DisplayMemberPath="DisplayName"`).
  - A **Word-wrap** `CheckBox` bound to `TextWordWrap`.
  - A read-only `TextBox`: `IsReadOnly=True`, `FontFamily="{DynamicResource MonoFontFamily}"`,
    `VerticalScrollBarVisibility=Auto`, `HorizontalScrollBarVisibility=Auto`,
    `Text="{Binding TextViewContent, Mode=OneWay}"`, and
    `TextWrapping` bound to `TextWordWrap` (true→`Wrap`, false→`NoWrap`, via a
    converter or a small style trigger).
  - A truncation note `TextBlock` shown when `TextViewTruncated` is true.

No code-behind logic is required beyond what already exists (toggle/combos/checkbox
bind directly). The hex search `KeyBinding`s (Ctrl+F etc.) remain on the view; they
operate on the hex view as before.

## Data flow

```
select tree node ─▶ SetHexBlock(offset,size)  (existing: sets HexDataSource/Offset/Length)
                                   │
                                   └─▶ UpdateTextView()  (no-op unless IsTextViewActive)

toggle to Text ─▶ IsTextViewActive=true ─▶ UpdateTextView()
change encoding ─▶ SelectedEncoding ─────▶ UpdateTextView() (if active)
toggle word-wrap ─▶ TextWordWrap ─────────▶ TextBox.TextWrapping only (no re-decode)

UpdateTextView():
  if !IsTextViewActive || HexDataSource is null: TextViewContent=""; TextViewTruncated=false
  else: (TextViewContent, TextViewTruncated) =
          TextDecoder.Decode(HexDataSource, HexBlockLength, SelectedEncoding.Encoding, 1 MB)
```

## Error handling / edge cases

- **No file / no selection:** `HexDataSource` null or `HexBlockLength <= 0` → empty
  Text view, no note.
- **Block larger than 1 MB:** decode first 1 MB; `TextViewTruncated = true` with the note.
- **Invalid byte sequences for the chosen encoding:** .NET replacement chars; never throws.
- **Encoding changed while in Hex mode:** no decode happens until Text is shown
  (the `OnSelectedEncodingChanged` guard checks `IsTextViewActive`).
- **CP437/Windows-1252 unavailable:** prevented by registering
  `CodePagesEncodingProvider` in the `TextEncodingOptions` static initializer before
  resolving those code pages.

## Testing

**`TextEncodingOptionsTests` (`ReScene.NET.Tests`):**
- `All` contains the seven curated encodings in order, UTF-8 first.
- The CP437 entry decodes a DOS box-drawing byte correctly (e.g. `0xB0` → `'░'`,
  `0xC9` → `'╔'`).
- Resolving the entries does not throw (provider registered).

**`TextDecoderTests` (`ReScene.NET.Tests`)** — using a tiny in-memory
`IHexDataSource` test double (byte-array backed):
- UTF-8 round-trip of a known string (incl. a multibyte char like "é").
- UTF-16 LE round-trip.
- CP437 decodes art bytes to the expected Unicode glyphs.
- `length > maxBytes` → `Truncated == true` and `Text.Length` reflects only the
  capped bytes; `length <= maxBytes` → `Truncated == false`.
- null source / `length <= 0` → `("", false)`.

**`InspectorViewModel` tests (`ReScene.NET.Tests`):**
- Default `SelectedEncoding` is the UTF-8 entry; `TextWordWrap` is false;
  `IsTextViewActive` is false.
- Load a minimal SRR with a stored text file, select it, set `IsTextViewActive = true`
  → `TextViewContent` equals the decoded stored text.
- Changing `SelectedEncoding` re-decodes `TextViewContent`.
- A stored payload larger than the cap → `TextViewTruncated == true` (use a small
  test-only cap is not exposed; instead assert with a payload > 1 MB, or assert the
  flag logic through `TextDecoder` directly and keep the VM test to the small case).
  (VM test focuses on correctness of decode + encoding switch; truncation is covered
  by `TextDecoderTests`.)

## Files

**Create**
- `ReScene.NET/Helpers/TextEncodingOption.cs` (record + `TextEncodingOptions` provider)
- `ReScene.NET/Helpers/TextDecoder.cs`
- `ReScene.NET.Tests/TextEncodingOptionsTests.cs`
- `ReScene.NET.Tests/TextDecoderTests.cs`

**Modify**
- `ReScene.NET/ReScene.NET.csproj` (add `System.Text.Encoding.CodePages`)
- `ReScene.NET/ViewModels/InspectorViewModel.cs`
- `ReScene.NET/Views/InspectorView.xaml` (+ `.xaml.cs` only if a `TextWrapping`
  converter or trigger needs wiring)
- `ReScene.NET.Tests/InspectorViewModelImageTests.cs` (or a new Inspector text-view
  test file reusing the same stubs)

## Out of scope (YAGNI)

- Text search within the Text view (Hex view keeps Ctrl+F).
- Persisting the chosen encoding across sessions (session-only; resets to UTF-8).
- Auto-detecting encoding (BOM sniffing, `.nfo`→CP437).
- Editing the text (read-only).
- A custom virtualized text renderer (the ~1 MB TextBox cap covers stored text).
