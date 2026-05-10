# Changelog

All notable changes to ReScene.NET are documented here.
Releases follow [SemVer](https://semver.org/) and this file follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/).

## [1.2.5] — 2026-05-10

### Added

- RAR Reconstructor patcher gains per-file modification-time
  rewriting. `PatchOptions.FileModifiedTimes` maps file names to target
  `DateTime`s; the patcher overwrites the matching file header's 4-byte
  DOS `FTIME` field and, when `LHD_EXTTIME` is set and the EXT_TIME
  mtime nibble carries the present bit, rewrites the sub-second
  remainder in-place at its existing precision (0–3 bytes), updating
  the +1s rounding flag for odd-second targets. `RAROptions` exposes
  `NeedsMtimePatching` (true when host-OS patching is enabled and
  `FileTimestamps` has entries) and `Manager` wires the existing
  `RAROptions.FileTimestamps` into the patch options. Sidesteps file
  system / WinRAR precision quirks that prevent the source file's
  mtime from being faithfully captured into the produced archive.

### Fixed

- `RARProcess` now registers `CodePagesEncodingProvider.Instance`
  before resolving the OEM code page. Without this, `Encoding.GetEncoding`
  for non-Unicode code pages (437, 850, 1252, …) throws
  `ArgumentException` on .NET Core / .NET 5+ and the OEM-encoding path
  silently fell back into its catch arm.

[1.2.5]: https://github.com/NeWbY100/ReScene.NET/releases/tag/v1.2.5

## [1.2.4] — 2026-05-10

### Fixed

- Compare tab no longer reports two RAR files as identical when only
  their block payload bytes differ. `FileComparer.CompareDetailedBlocks`
  now byte-compares each block's data region (`StartOffset + HeaderSize`
  through `+ DataSize`) in 64 KB chunks when both sides supply an
  `IHexDataSource`, surfacing a `Block Data` property difference and
  marking the affected file/service block as `[DIFF]` in the structure
  tree. Previously the comparator only inspected parsed RAR header
  fields, so two archives with identical metadata (filename, packed
  size, file CRC32, timestamp) but different compressed payloads — the
  exact case produced when reconstructing — slipped through as
  "identical."
- The status bar can no longer disagree with the hex-view byte diff:
  when the structural compare finds zero differences but the byte-level
  hex diff reports differing ranges, the status now reads "Byte-level
  differences detected in current hex view but no structural
  differences found." instead of "Files are identical."

[1.2.4]: https://github.com/NeWbY100/ReScene.NET/releases/tag/v1.2.4

## [1.2.3] — 2026-05-08

### Added

- Compare tab now overlays a translucent red highlight on bytes that
  differ between the left and right files inside the currently
  selected block. The diff is computed asynchronously in 64 KB
  chunks with progressive UI updates and is cancelled when the
  selection changes; trailing bytes on the longer side are marked
  when block lengths differ. Status text shows a `Computing byte
  diff… NN%` progress indicator while the scan runs.
- RAR Reconstructor tab gains Import / Export Config commands that
  persist all user-editable fields and switches as JSON via the new
  `ReconstructorConfig` snapshot type.
- Brute-Force Progress window gets an Auto-scroll toggle that keeps
  the version grid pinned to the latest entry as runs complete.

## [1.2.2] — 2026-05-07

### Fixed

- SRR Creator now prompts before overwriting an existing output file
  instead of silently truncating it. Cancelling leaves the previous log
  and progress untouched.
- Compare tab populates correctly when opening an SRR file. The
  v1.2.1 acronym rename created two `SRRFileData` types in different
  namespaces; the dispatch in `FileCompareViewModel` was matching the
  wrong sibling, leaving the tree empty.
- `languages.diz` extraction now decompresses RAR-compressed VobSub
  `.idx` files via `RARDecompressor` instead of writing garbage from
  the packed bitstream. Solid archives, split files, and decompression
  failures surface a precise per-file skip warning.
- SRR Creator no longer silently re-adds the SFV (and any sibling
  `.nfo` files) after the user removes them from the Stored Files
  list. `SRRWriter.CreateFromSFVAsync` now treats `additionalFiles`
  as the sole source of stored-file blocks; the WPF
  `ReleaseFileScanner` still pre-populates the UI list when an input
  is selected.

### Added

- Granular per-file log lines during SRR creation: `Adding stored
  file …`, `Computing OSO hashes…`, `Added OSO hash …`, `Scanning RAR
  archive for VobSub .idx files…`, `Adding languages.diz …`.
- New `RARArchive` / `RAREntry` types in `ReScene.RAR` — a file-level
  view over a RAR volume set with `Open`, `Files`, `OpenPackedStream`,
  and `TryReadAllBytes` (transparent decompression). Replaces hand-rolled
  header-walk code that had been duplicated across consumers.
- `RARArchiveOpenTests` (16 cases) and `RARVolumeNamingTests`
  (27 cases) covering the new abstraction and the volume-naming helper.
- `SRRCreationResult.LanguagesDizIdxFiles` exposes the discovered
  `.idx` files; the SRR Creator log surfaces these on the success
  line.

### Changed

- Acronyms in identifiers and source-file names normalized to ALL
  CAPS to match the dominant convention: `RAR`, `SRR`, `SRS`, `SFV`,
  `EBML`, `MP3`, `MP4`, `MKV`, `AVI`, `WMV`, `ASF`, `ISO`, `OSO`,
  `CRC`, `MHD`, `LHD`. Mid-identifier and standalone occurrences are
  covered (e.g. `CreateSrrCommand` → `CreateSRRCommand`,
  `BlockCrcMismatch` → `BlockCRCMismatch`). Third-party namespaces and
  types (`Force.Crc32`, `Crc32Algorithm` from Crc32.NET,
  `DiscUtils.Iso9660`, BCL `System.IO.Hashing.Crc32`) are
  intentionally preserved.
- `LanguagesDizGenerator` and `OSOHashCalculator` refactored onto
  `RARArchive`, dropping their duplicated header-walk loops.
- `RarStream`'s previously-private volume-naming helper extracted to
  `RARVolumeNaming` and shared with `RARArchive`.

[1.2.3]: https://github.com/NeWbY100/ReScene.NET/releases/tag/v1.2.3
[1.2.2]: https://github.com/NeWbY100/ReScene.NET/releases/tag/v1.2.2
