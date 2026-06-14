# Compare MKV files — Full EBML element tree

- **Date:** 2026-06-10
- **Status:** Approved (user chose the full element-tree option)
- **App:** ReScene.NET (WPF) + ReScene.Lib

## Summary

Add **MKV** as a fourth comparable file type in the Advanced **Compare** window
(alongside RAR / SRR / SRS). Two `.mkv` files are parsed into their **full EBML
element tree** and rendered in the existing structure tree + property table + hex
view, with per-element diff highlighting — mirroring how SRS comparison already works.

## Decisions

| Decision | Choice |
|---|---|
| Granularity | **Full EBML element tree** (every element, expandable) |
| Parser location | **ReScene.Lib** (`ReScene.Core.Comparison`), like SRRFileData/RARFileData |
| EBML primitives | Reuse `ReScene.SRS.EBMLReader` (internal, same assembly) |
| Cluster bodies | **Not** expanded to per-block level (a Cluster is a leaf node with offset/size + Timestamp); avoids millions of nodes |
| Bounding | Cap total parsed elements (truncation marker); binary leaf values show size + short hex preview |
| Diff model | Path-keyed through the existing `CompareResult` (`FileDifferences` keyed by element path), so the existing tree-highlight + property machinery is reused |
| Byte ranges | Each element links to its bytes (offset = element start, length = total size) for the hex view |

## Data model (lib, new `MKVFileData.cs`)

- `enum EBMLValueType { Master, UnsignedInt, SignedInt, Float, String, Utf8, Date, Binary, Unknown }`
- `class EBMLElement`: `ulong ElementId`, `string Name`, `long Position` (file offset of the
  ID byte), `int HeaderSize` (id+size bytes), `long DataSize`, `long TotalSize`
  (`HeaderSize + DataSize`), `EBMLValueType ValueType`, `string? Value` (formatted leaf
  value; null for Master), `List<EBMLElement> Children`.
- `class MKVFileData`: `string FilePath`, `List<EBMLElement> Elements` (top level: the
  EBML header `0x1A45DFA3` and the Segment `0x18538067`), `int TrackCount` (for the root
  label), `static MKVFileData Load(string path)`.
- `static EBMLElementRegistry`: `ulong → (string name, EBMLValueType type)` for the known
  Matroska IDs (EBML header fields, SeekHead/Seek, Info + children, Tracks/TrackEntry +
  all track properties, Cluster + Timestamp, Cues, Chapters, Tags, Attachments/AttachedFile,
  Void, CRC-32, …). Unknown IDs → `"Unknown (0x…)"`, `Binary`.

### Parser rules
- Walk the file's top-level elements; recurse into **Master** elements building `Children`.
- Leaf value formatting by type (number / text / date / `"N bytes: AA BB …"` for binary).
- **Do not** recurse into a `Cluster`'s block contents — show the Cluster node with its
  offset/size and its `Timestamp` value; this keeps the tree bounded and the diff meaningful.
- Cap total elements (default 1 000 — beyond that a movie is just more clusters/cue points;
  user-configurable via Settings → "MKV element limit", persisted as `MkvMaxElements`) and append
  a truncation marker if exceeded; cap binary
  preview to the first ~16 bytes; never read a whole cluster body into memory.

## Comparison (lib, `FileComparer.CompareMKVFiles`)

- Add the `MKVFileData` dispatch to `Compare()` and `MKVFileData => "MKV File"` to
  `GetFileTypeName()`.
- `CompareMKVFiles(left, right, result, leftSource?, rightSource?)` walks both element trees
  in parallel, pairing children by **(Name, occurrence index among same-named siblings under
  the parent)**:
  - both present with children → recurse.
  - both present without children (true leaves **and** non-recursed masters, i.e. Clusters) →
    compare in order: formatted `Value` → `Data Size` → raw bytes via the optional hex data
    sources (`BlockDataMatches`, chunked with early exit). The byte check catches changes the
    formatted value cannot show: **cluster A/V payloads**, binary fields longer than the
    16-byte preview, and strings that only differ in trailing NULs. Each hit → a `Modified`
    `FileDifference` keyed by the element **path**, with a `PropertyDifference` naming what
    differed (`"Value"`, `"Data Size"`, or `"Data"`).
  - one side only → `Added` / `Removed` `FileDifference` keyed by the path.
- A shared `ElementPath(parentPath, element, index)` helper builds the key; **the ViewModel
  uses the identical scheme** so tree nodes (`CompareNodeData.FileName = path`) line up with
  the diff keys.

## Rendering (app, `FileCompareViewModel`)

- `CompareNodeType`: add `MKVElement` (and a root marker if needed).
- `LoadFileData` (service) `.mkv → MKVFileData.Load`; `FileDialogFilters` add `*.mkv`.
- `PopulateTree` → `PopulateMKVTree(roots, mkv, isLeft)`: a root `"MKV File (N tracks)"`, then
  the element tree recursively; each node tagged `CompareNodeData { NodeType = MKVElement,
  Data = EBMLElement, FileName = path, IsLeft }`.
- `ShowProperties` → `ShowMKVElementProperties`: Name, Element ID (hex), Position, Header
  Size, Data Size, Total Size, Type, Value — the **Value** row `IsDifferent` via
  `GetTrackDiffs(path)`.
- `HighlightBlock`: `EBMLElement → offset = Position, length = TotalSize`.
- `ApplyNodeHighlighting`: an `MKVElement` case mirroring `SRSTrack` ([DIFF]/[NEW]/[REMOVED]
  by `FileName`/path), bubbling to parents.

## Testing
- Lib: parse a small synthetic MKV (EBML header + Segment + Info + Tracks/TrackEntry + a
  Cluster) → assert the element tree + values; `CompareMKVFiles` for identical / a changed
  leaf value / an added & removed element.
- Whole solution builds with 0 warnings; existing 925 lib + 93 app tests stay green.

## Out of scope
- Per-frame / block-level diffing inside clusters; editing MKVs; remuxing.
