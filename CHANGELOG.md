# Changelog

All notable changes to ReScene.NET are documented here. Releases follow [SemVer](https://semver.org/) and this file follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/).

## [1.4.0] — 2026-06-18

### Added

- Global exception handling: unhandled UI-thread exceptions, faulted unobserved tasks, and fatal background-thread exceptions are now surfaced (an error dialog plus a trace entry) instead of crashing silently or vanishing.
- Operation logs now auto-scroll to follow the newest line as work progresses — in the Create-an-SRR / Create-an-SRS / Edit / Restore wizards and the matching Advanced tabs — unless you have scrolled up to read earlier entries.

### Fixed

- **Stopping a reconstruction now actually cancels it.** The Stop signal is threaded into the library so the running WinRAR processes are terminated, and a mid-run stop is reported as "Cancelled" instead of the misleading "No match found."
- SRR creation no longer silently drops old-style RAR volumes (`.r00`, `.r01`, …) when there is a gap in the numbering.
- Settings load/save failures are now recorded (trace) instead of being silently swallowed.
- **WMV/ASF samples now reconstruct byte-exactly** — previously the rebuilt file could never match the original.
- **Reconstructing archives larger than 2 GB** now applies the correct per-file timestamps.
- Inspecting or comparing a file no longer crashes on corrupt RAR5 metadata, and loading a truncated or incomplete SRR no longer throws.
- Reading the contents of compressed RAR entries larger than 32 KB is now correct.
- SHA-1 verification files that contain blank lines are accepted.
- SRS reconstruction from an ISO: the Rebuild button now enables and disables correctly when the ISO source is toggled.

### Changed

- Faster SHA-1 hashing, and OpenSubtitles (OSO) hashing now reports any file it has to skip — from the bundled ReScene.Lib, which is released in lockstep (its library fixes are listed above).
- Internal refactoring with no behavioural change: view-model dialog and UI-dispatch calls now go through injectable abstractions (making the validation logic unit-testable), and the large Reconstructor view-model was decomposed into focused collaborators.
- Large additions to the automated test suite and assorted best-practice cleanups.

## [1.3.0] — 2026-06-14

### Added

- **Beginner mode** — a guided home hub of task cards (Create an SRR, Create an SRS, Reconstruct RAR archives, Restore a sample, Edit an SRR), each opening a focused, step-by-step pop-up wizard. Switch between Beginner and Advanced from the new **Mode** menu or in Settings; the choice is remembered. The hub groups its cards by file type.
- **Compare MKV/WebM files** — the Compare tab now parses MKV/WebM and shows their EBML structure side by side, highlighting differing elements in red down to byte-level cluster payloads. The Inspector also opens MKV/WebM files. A configurable element-parse limit keeps very large files responsive.
- **Create-an-SRR wizard**: a "Samples & subtitles" step lists the sample `.srs` and subtitle nested-`.srr` that will be embedded — detected automatically, or point at files for an unextracted release — as reorderable rows generated when you press Create. Stored files can be added, removed, renamed and reordered, and OSO hashes (for OpenSubtitles matching) are computed by default.
- **Create-an-SRS wizard**: an optional "full movie" step records each track's match offset (pyrescene parity), with a clear warning before creating a signature-only SRS.
- **Reconstruct wizard**: shows the imported SRR's details (RAR volumes, archived files, compression, stored files with sizes, one per line); can recreate the whole release (all volumes); renames rebuilt archives to the release's original names (from the SRR, or the verification `.sfv`); and offers **Open folder** and **Copy full command line** once a match is found.
- **Restore-a-sample wizard**: a single input routes automatically to bulk restore (`.srr`) or single rebuild (`.srs`), each with its own "Save to".
- **Edit-an-SRR wizard**: curate an existing SRR's stored files — add/remove/rename/extract with multi-select — non-destructively.
- **Settings**: a redesigned, grouped dialog adds default WinRAR-versions and reconstruction-output folders and an MKV element-parse limit, and hosts the Mode selector; Settings moved from the Help menu to File.

### Changed

- Reconstructed RAR archives are written to an `output/` subfolder of the chosen output directory (alongside the copied `input/`) rather than its root.

### Fixed

- Many wizard and dialog refinements: resizable/larger wizard windows, scrollable detail logs, clearer disabled buttons, a wider rename prompt, full-width menus without an empty icon gutter, and assorted layout/clipping fixes.

## [1.2.7] — 2026-05-24

### Added

- Every input on the five task forms (SRR Creator, SRS Creator, RAR Reconstructor, SRS Reconstructor, SRS Restorer) now explains itself and assists as you go. Each file/folder field carries an inline description, and a ✓/ℹ/⚠/✗ status line gives live feedback: the SRR Creator counts RAR volumes in the chosen release folder, the SRS Creator identifies the sample's container and size, the SRS Reconstructor reads the expected sample name/size from the SRS and sanity-checks the media file against it, the SRS Restorer reports how many embedded samples matched media files, and the RAR Reconstructor validates its WinRAR / Release / Verify paths.
- Output locations auto-fill from the input where possible — the SRR beside the input, the .srs from the sample name, the rebuilt sample from the SRS, the restore output from the media folder — only when empty, never overwriting a path you typed.
- The SRR Creator shows a hint next to a disabled "Create SRR" button explaining what's still needed.

### Changed

- Unified the input layout across all five task forms: a bold label with its description inline, the input row beneath, and the status line below — matching the SRS Creator's "Main file" style. The RAR Reconstructor's four paths move from left-aligned labels to this same layout.

## [1.2.6] — 2026-05-12

### Added

- SRS Creator gains an optional **Main file** input. When set, the writer locates each track's signature inside the main file after profiling and records the offset as `TrackInfo.MatchOffset`, mirroring pyrescene's `-c` flag. Produces SRS files byte-equivalent to scene tooling output (matching `MatchOffset` values rather than 0). MKV uses the EBML walker (handles subtitle-style tracks); other containers fall back to a raw byte scan. Tracks the verifier cannot locate keep `MatchOffset = 0` and emit a warning instead of failing.
- SRS Reconstructor and SRS Creator both show live scan progress (percent, MB scanned, throughput, ETA) during their long file-walking steps. The Reconstructor modal stays open through the "Rebuilding" and "Verifying CRC" phases — heading transitions through "Rebuilding Sample" → "Verifying CRC" → close — instead of disappearing while the EBML walker traverses the media file silently.
- RAR Reconstructor warns via `MessageBox` when an imported SRR carries no RAR reconstruction information (no RAR file entries, no archived files, and no detected compression method). The user is told to configure options manually instead of being left wondering why nothing auto-populated.
- RAR Reconstructor surfaces timestamp-preservation failures. When the brute-force input copy cannot apply the source file's mtime/ctime/atime onto the working copy (denied by ACLs, read-only volume, …), a single summary `MessageBox` lists the affected paths after the run completes, explaining that the produced RAR's File Time (DOS) may not match the original. Per-file warnings continue to flow through the system log in real time.

### Fixed

- MKV sample reconstruction no longer fails with "Unable to locate track signature for track N in the media file" when the SRS was generated from a sample containing subtitle tracks (or any track whose individual Block payloads are smaller than the 256-byte signature). `MKVContainerRebuilder.FindSampleStreams` now walks the media file's EBML structure and matches each track's signature progressively across non-contiguous Block payloads — mirroring pyrescene's `_mkv_block_find`, including partial-match reset/re-try as a fresh match start. The previous raw byte scan in `SRSRebuilder.FindSignature` is preserved as a fallback for non-MKV containers.
- `MKVContainerRebuilder.ExtractMediaAttachments` skips past `Cluster` bodies during its sweep. Attachments never live inside Clusters, and walking every `SimpleBlock` in a multi-GB MKV turned the attachment pass into a multi-second silent stall.
- Reconstructor input copies now propagate the source file's `LastWriteTime` / `CreationTime` / `LastAccessTime` onto the destination after the stream copy. Previously the stream copy stamped destinations with "now", so when the SRR carried no archived timestamps WinRAR packed `FILE_HEAD.FTIME` with the copy time instead of the source's mtime. With SRR-driven timestamps the existing `ApplyFileTimestamps` step still overrides — same end result. With an empty SRR, the file's correct mtime now flows through to the produced RAR.
- Compare tab's `SRSContainerChunks` matcher used to return the first matching node (usually the "Container Structure" parent, whose `Data` is a `List<SRSContainerChunk>` rather than a single chunk), leaving the opposite-side Properties panel empty when clicking through to a Cluster / EBML / Segment / etc. node. `FindMatchingNode` now special-cases the type and matches parent-to-parent and chunk-to-chunk by `Label`.

[1.2.6]: https://github.com/NeWbY100/ReScene.NET/releases/tag/v1.2.6

## [1.2.5] — 2026-05-10

### Added

- RAR Reconstructor patcher gains per-file modification-time rewriting. `PatchOptions.FileModifiedTimes` maps file names to target `DateTime`s; the patcher overwrites the matching file header's 4-byte DOS `FTIME` field and, when `LHD_EXTTIME` is set and the EXT_TIME mtime nibble carries the present bit, rewrites the sub-second remainder in-place at its existing precision (0–3 bytes), updating the +1s rounding flag for odd-second targets. `RAROptions` exposes `NeedsMtimePatching` (true when host-OS patching is enabled and `FileTimestamps` has entries) and `Manager` wires the existing `RAROptions.FileTimestamps` into the patch options. Sidesteps file system / WinRAR precision quirks that prevent the source file's mtime from being faithfully captured into the produced archive.

### Fixed

- `RARProcess` now registers `CodePagesEncodingProvider.Instance` before resolving the OEM code page. Without this, `Encoding.GetEncoding` for non-Unicode code pages (437, 850, 1252, …) throws `ArgumentException` on .NET Core / .NET 5+ and the OEM-encoding path silently fell back into its catch arm.

[1.2.5]: https://github.com/NeWbY100/ReScene.NET/releases/tag/v1.2.5

## [1.2.4] — 2026-05-10

### Fixed

- Compare tab no longer reports two RAR files as identical when only their block payload bytes differ. `FileComparer.CompareDetailedBlocks` now byte-compares each block's data region (`StartOffset + HeaderSize` through `+ DataSize`) in 64 KB chunks when both sides supply an `IHexDataSource`, surfacing a `Block Data` property difference and marking the affected file/service block as `[DIFF]` in the structure tree. Previously the comparator only inspected parsed RAR header fields, so two archives with identical metadata (filename, packed size, file CRC32, timestamp) but different compressed payloads — the exact case produced when reconstructing — slipped through as "identical."
- The status bar can no longer disagree with the hex-view byte diff: when the structural compare finds zero differences but the byte-level hex diff reports differing ranges, the status now reads "Byte-level differences detected in current hex view but no structural differences found." instead of "Files are identical."

[1.2.4]: https://github.com/NeWbY100/ReScene.NET/releases/tag/v1.2.4

## [1.2.3] — 2026-05-08

### Added

- Compare tab now overlays a translucent red highlight on bytes that differ between the left and right files inside the currently selected block. The diff is computed asynchronously in 64 KB chunks with progressive UI updates and is cancelled when the selection changes; trailing bytes on the longer side are marked when block lengths differ. Status text shows a `Computing byte diff… NN%` progress indicator while the scan runs.
- RAR Reconstructor tab gains Import / Export Config commands that persist all user-editable fields and switches as JSON via the new `ReconstructorConfig` snapshot type.
- Brute-Force Progress window gets an Auto-scroll toggle that keeps the version grid pinned to the latest entry as runs complete.

## [1.2.2] — 2026-05-07

### Fixed

- SRR Creator now prompts before overwriting an existing output file instead of silently truncating it. Cancelling leaves the previous log and progress untouched.
- Compare tab populates correctly when opening an SRR file. The v1.2.1 acronym rename created two `SRRFileData` types in different namespaces; the dispatch in `FileCompareViewModel` was matching the wrong sibling, leaving the tree empty.
- `languages.diz` extraction now decompresses RAR-compressed VobSub `.idx` files via `RARDecompressor` instead of writing garbage from the packed bitstream. Solid archives, split files, and decompression failures surface a precise per-file skip warning.
- SRR Creator no longer silently re-adds the SFV (and any sibling `.nfo` files) after the user removes them from the Stored Files list. `SRRWriter.CreateFromSFVAsync` now treats `additionalFiles` as the sole source of stored-file blocks; the WPF `ReleaseFileScanner` still pre-populates the UI list when an input is selected.

### Added

- Granular per-file log lines during SRR creation: `Adding stored file …`, `Computing OSO hashes…`, `Added OSO hash …`, `Scanning RAR archive for VobSub .idx files…`, `Adding languages.diz …`.
- New `RARArchive` / `RAREntry` types in `ReScene.RAR` — a file-level view over a RAR volume set with `Open`, `Files`, `OpenPackedStream`, and `TryReadAllBytes` (transparent decompression). Replaces hand-rolled header-walk code that had been duplicated across consumers.
- `RARArchiveOpenTests` (16 cases) and `RARVolumeNamingTests` (27 cases) covering the new abstraction and the volume-naming helper.
- `SRRCreationResult.LanguagesDizIdxFiles` exposes the discovered `.idx` files; the SRR Creator log surfaces these on the success line.

### Changed

- Acronyms in identifiers and source-file names normalized to ALL CAPS to match the dominant convention: `RAR`, `SRR`, `SRS`, `SFV`, `EBML`, `MP3`, `MP4`, `MKV`, `AVI`, `WMV`, `ASF`, `ISO`, `OSO`, `CRC`, `MHD`, `LHD`. Mid-identifier and standalone occurrences are covered (e.g. `CreateSrrCommand` → `CreateSRRCommand`, `BlockCrcMismatch` → `BlockCRCMismatch`). Third-party namespaces and types (`Force.Crc32`, `Crc32Algorithm` from Crc32.NET, `DiscUtils.Iso9660`, BCL `System.IO.Hashing.Crc32`) are intentionally preserved.
- `LanguagesDizGenerator` and `OSOHashCalculator` refactored onto `RARArchive`, dropping their duplicated header-walk loops.
- `RarStream`'s previously-private volume-naming helper extracted to `RARVolumeNaming` and shared with `RARArchive`.

[1.2.3]: https://github.com/NeWbY100/ReScene.NET/releases/tag/v1.2.3 [1.2.2]: https://github.com/NeWbY100/ReScene.NET/releases/tag/v1.2.2
