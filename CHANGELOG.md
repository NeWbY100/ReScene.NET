# Changelog

All notable changes to ReScene Manager (formerly ReScene.NET) are documented here. Releases follow [SemVer](https://semver.org/) and this file follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/).

## [Unreleased]

### Added

- The CLI now has its own test suite (`ReScene.Cli.Tests`, 19 tests) covering all four verbs,
  their exit codes, and extraction safety; it runs in CI on every platform.

### Changed

- Every project now builds under the full .NET analyzer regime
  (`AnalysisLevel=latest-All` + style enforcement), warning-free — previously only the library
  and the CLI did, leaving the application core and the Avalonia head unanalyzed. CI installs
  the .NET 8 runtime so the library's test suite genuinely executes its net8.0 leg.
- `rescene extract` now delegates to the library's new bulk extraction API instead of its own
  copy loop. Extraction still preserves each stored file's relative path, but an SRR carrying a
  hostile stored name (rooted, `.`/`..` segments, or a name that pre-existing links would
  redirect outside the output directory) is now refused outright with exit code 2 and nothing
  written — previously such names were silently rewritten and extraction continued. Truncated
  stored data is likewise refused up front instead of producing silently short files.
- The SRR Creator's view-model has been split into focused collaborators — artifact naming,
  scan-session lifecycle, per-file generation, folder-mode staging, field guidance, the
  folder-scan controller and the file-mode creation pipeline — shrinking it from 2,295 lines to
  1,035. Behaviour is unchanged; each moved method body was diffed mechanically against its
  original rather than retyped. The decomposition is covered by new characterization tests for
  the behaviour it could have disturbed, several of which closed pre-existing gaps: the Create
  button's enabled-state *notification* (as opposed to its predicate, which is re-evaluated on
  demand and so could never detect a missing notification) was untested on the scan fault,
  root-error and success paths; nothing verified that the Advanced tab and the Beginner wizard
  keep separate progress streams; and nothing pinned that the stored-file list is appended to
  incrementally during a build, or that an option toggled off mid-run takes effect.

- The RAR Reconstructor's view-model has had the same treatment: the batched log buffer, the
  reserved-output-tree cleanup, the start-validation gauntlet, the version-tree coordinator, the
  reconstruction run loop and the SRR import decisions are now separate units, with the engine's
  progress handlers and much of the bound property surface filed into partials. It goes from 3,091
  lines in one file to 1,840 plus two partials, and behaviour is unchanged — every verbatim move was diffed
  mechanically against its original rather than retyped, and the decision logic deliberately reshaped into
  returned values is covered by characterization tests instead. As with the Creator, the
  characterization tests written to protect the move closed gaps that already existed: the run's
  completion no longer relied on untested writes to settle the progress readout; the busy-flag clear
  order and both queued-progress staleness gates are pinned, so a late event cannot re-open a closed
  progress window; the SRR import's RAR-version and volume-size decisions and the ORDER it applies
  them in are covered; and the single flag that guards a programmatic bulk change to the version tree is
  pinned separately at each of the two places it is raised, because they fail in different ways.

### Fixed

- Repeated in-process `create` invocations no longer accumulate `Console.CancelKeyPress`
  handlers holding disposed cancellation sources.

- Rapidly stopping and restarting an operation could leave its progress dialog missing — and in
  the worst interleaving, cancel the restarted operation outright. Both progress-window
  controllers now record which operation each window belongs to, reopen the dialog when busy
  returns before the old window has finished closing, and can no longer route a stale window's
  close handling to the new operation.

## [2.3.0] — 2026-08-08

### Fixed

- Solid archives are now packed in the release's own file order. The reconstructor reads the
  original order from the SRR's embedded headers and passes the files to rar explicitly with
  rar's own sorting disabled (`-ds`), so machine-side order lists can no longer scramble the
  produced bytes — most notably the `/etc/rarfiles.lst` that Ubuntu's `rar` package installs,
  whose Debian-modified ordering packs `*.cue` files before `*.bin` and made bin/cue releases
  unmatchable on such machines. As a side effect, releases whose original order differs from
  rar's plain name sort become reconstructable for the first time. The copied full command line
  includes the explicit file list, so a pasted command reproduces the run exactly.
- Reconstruction is now immune to rar switches injected by the user's environment: every rar
  invocation passes `-cfg-`, so a forgotten `switches=` line in `~/.rarrc`, a `rar.ini` beside
  the executable, or a `RAR` environment variable can no longer silently alter the produced
  archives. Found in the field: a Linux user's `-ds` (disable name sort) made rar pack a solid
  set in directory order instead of the release's order, so every combination compressed a
  different byte stream and could never match — while the archives themselves stayed perfectly
  valid and extractable. The switch is part of the copied full command line, and it exists in
  every rar version the app supports (verified 2.03 through 7.20).

### Added

- When assembly rejects a candidate and the produced archive packed files in a different order
  than the release, the log now says so directly — once per run, naming both first files — so
  an environment-level cause like the one above identifies itself instead of reading as an
  endless clean "no match".

### Changed

- Assembly's per-volume log lines now read plainly "written" — the old "(no hash to verify)"
  suffix described the inner layer's inputs and read as "unverified", when in fact the engine
  verifies every assembled volume against the release's checksums right afterwards.

## [2.2.0] — 2026-08-06

### Added

- The app now follows your operating system's high-contrast setting. Turn high contrast on in
  Windows, and ReScene Manager switches to a maximum-contrast theme — black surfaces, white text
  and borders, and a yellow focus outline — without a restart; turn it off and the normal theme
  returns exactly as it was. There is no in-app switch on purpose: if you have told your system you
  need high contrast, you should not have to tell every application again.

### Fixed

- Keyboard order now matches visual order everywhere: the SRR Creator's Input and Output rows
  no longer trap or reorder Tab movement, wizard pages put their own fields ahead of the
  Back/Next footer, and every path field in the app tabs before its own Browse button.
- Screen readers hear the whole app: every Browse button announces what it browses for, a
  field's first status message is announced (it used to reach assistive tech only from the
  second message on), and previously silent outcomes now speak — the Edit SRR wizard's save
  result, the Reconstruct wizard's custom-packer warning, and the Inspector's verify outcome.
- A brief busy flicker could open a progress dialog that nothing could close — it sat over the
  page swallowing clicks until the app was restarted. Both progress-window controllers now
  settle to the latest requested state, however fast that state changes.

## [2.1.0] — 2026-08-02

### Changed

- Content text is now 13px, up from the 12px inherited from the WPF app — more readable without
  costing the vertical room 14px would in small windows. Tab-strip headers keep their 12px.

### Added

- Task pages now adapt to small windows: panes shrink and scroll instead of clipping, header
  help collapses behind a disclosure, and every control stays reachable by keyboard at the
  minimum window size (700×450). Each page works out for itself how small it can get before
  switching to the compact layout, measuring its own content rather than relying on fixed
  numbers — so the switch happens in the right place on Linux and macOS, whose font metrics
  render the same content at different heights, and it keeps up as a page's content grows.

### Fixed

- RAR reconstruction now assembles output volumes from the SRR's original headers, fixing
  cross-platform reconstruction (e.g. Linux rar builds that omit the EXT_TIME header field);
  SRRs with recovery records fall back to the legacy path with a clear diagnostic.

## [2.0.0] — 2026-07-26

ReScene Manager 2.0 is a full cross-platform rewrite: the WPF app has been rebuilt on Avalonia
and now runs natively on **Windows, Linux, and macOS** (Intel and Apple Silicon), shipped as a
self-contained single file per platform — no .NET install needed. Everything survives the move:
all eight Advanced tabs, the Beginner wizards, and the secondary windows, in a Fluent dark theme.

### Added

- **Multi-set SRR creation.** Point the SRR Creator at a release directory whose subdirectories
  carry their own SFVs (`dvd1`/`dvd2`-style) and it writes one SRR covering every set — matching
  pyReScene's output byte-for-byte. The Reconstructor understands multi-set SRRs too: each
  archive set is reconstructed independently in a single run.
- **WinRAR on Linux.** The RAR Reconstructor runs Linux rar binaries, preferring each version
  folder's `run-rar` launcher (a bundled-runtime wrapper, so 2002-era rar builds work on any
  modern x86_64 distro with nothing installed). Download links for the Windows pack, the Linux
  pack, and the RAR FTP originals (Windows-only) are offered on the Reconstructor tab, the
  Reconstruct wizard, and Settings.
- **One chronological run log** replaces the Reconstructor's three separate log panes, tagging
  the engine phases `[P1]`/`[P2]`, with **Save log…** buttons on the wizards as well.
- **Copy Full Command Line** on a version row copies the exact command the engine ran —
  including the switches it adds itself (`-ma4`, `-vn`, comment file) — as a paste-runnable
  `cd … && rar …` line in your platform's shell dialect.
- **Keep work files for diagnostics.** A new setting (off by default, meaning scratch is kept)
  controls whether each run's work files — input copies, attempted archives, per-attempt rar
  logs — are cleared when a run finishes.
- **A tabbed, wider Settings window** (Interface / General / Inspector & Compare /
  RAR Reconstruction).

### Fixed

- **Failed rar launches say so.** A version whose rar cannot start (missing loader, not
  executable, wrong arguments) now shows an **Error** row naming the exit code instead of a
  fake "Complete", cancelled runs are never misreported, and per-attempt rar output is logged
  in every mode.
- **Verified output lands in your output folder.** Fixed the case where a verified
  reconstruction reported success while its volumes stayed stranded in the hidden scratch
  directory — placement is now transactional with rollback, and a failed rollback preserves the
  scratch rather than deleting recoverable output.
- **Linux paper cuts**, found running the real thing: WinRAR version folders parse
  case-correctly, the archive input mask is platform-correct, scrollbars no longer draw over
  the Browse buttons or the newest log line (they reserve real space), and every file picker
  opens where its field points instead of `$HOME` — with sensible anchors when a field is empty
  (the movie picker starts beside your sample; the verification picker in the release folder).
- **Byte-exact reconstruction fixes** (via ReScene.Lib): a further round of RAR-header and
  rebuild correctness fixes, proven against real WinRAR output.
- **Accessibility.** Wizard path fields announce their names to screen readers and the four
  identical Browse buttons are distinguishable; the Settings window reaches its tab strip before
  the footer in Tab order; run completion is announced politely, including the could-not-run
  count.

### Changed

- **Renamed: ReScene.NET → ReScene Manager** (the GitHub repository redirects). Settings live in
  a fresh `ReScene.Manager` settings folder — 1.x settings are not migrated.
- On Windows, file pickers now start at the folder the field shows, taking precedence over the
  dialog's own last-folder memory.
- Release artifacts are self-contained single files named `ReSceneManager-<version>-<rid>` for
  win-x64, linux-x64, osx-x64, and osx-arm64.

## [1.9.0] — 2026-07-05

### Added

- **The WinRAR versions you tick are the ones actually tried.** The RAR Reconstructor now
  passes your selected version folders through to the brute-force engine, so unticking versions
  genuinely narrows (and speeds up) the search instead of the engine still trying every installed
  version. This completes the version picker added in 1.8.0.

### Fixed

- **Large files no longer freeze the window.** Opening a big archive in the Inspector, and scanning
  a media folder in the Sample Restorer, now parse off the UI thread — the app stays responsive
  instead of locking up on large inputs.
- **RAR Reconstructor reliability.** Fixed cross-set seeding that never ran, a reversed
  multithreading (`-mt`) switch, a stale version switch carried over between runs, a folder-scan
  race, a hang when cancelling during CRC verification, an ISO flag leaking between runs, and a
  wizard crash.
- **More accurate reconstruction and rebuilds** (via ReScene.Lib): byte-exact RAR fixes
  (EXT_TIME field, Unicode filename patching, 64-bit pack sizes), FLAC samples that carry a leading
  ID3v2 tag now rebuild, MP3 samples with stacked tags rebuild correctly (and no longer hang on a
  zero-size block), MKV lacing headers over 256 bytes are measured correctly, and fragmented
  (multi-`mdat`) MP4 is refused cleanly on both sides instead of producing a bad result.
- **Editing SRRs is safe again.** Fixed cases where the SRR editor/verifier could mis-parse
  embedded RAR headers as SRR blocks and lose data on commit.
- **Verification and comparison fixes.** Correct SHA-1/CRC-32 verification, a resilient brute-force
  loop, support for releases with more than 101 volumes, correct stored-file content comparison,
  and several Inspector/Compare fixes (hex-search coordinates, stale hex selection, a Compare
  side-cross, shared-service state bleeding between tabs).
- **ISO sample sources are detected from typed or dropped paths**, not only via the file picker.
- Hardened against path-traversal (Zip-Slip) when reconstructing from an SRR.
- Smaller fixes: recent-files limit clamped, a temporary-directory leak closed, link opening no
  longer crashes on a bad handler, and drag-and-drop parity across tabs.

### Changed

- Settings and reconstructor-config files now load case-insensitively, so files written by older
  versions keep working after internal property renames.
- Large internal cleanup with no behavior change: one top-level type per file across the codebase,
  format-acronym casing normalized to all-caps (`SRR`/`RAR`/`MP3`/`MP4`/`SRS`/`MKV`/…), and magic
  numbers replaced with named constants.

## [1.8.0] — 2026-07-02

### Added

- **Pick the exact WinRAR versions to try.** The RAR Reconstructor's Versions tab now lists the
  WinRAR sub-versions actually installed in your WinRAR versions folder as a collapsible tree —
  one expander per major version (`RAR 2.x (2 of 38)`) with the individual builds in columns
  inside — instead of six coarse `2.x`–`7.x` checkboxes. Tick only the versions you think produced
  the release and the brute-force tests just those. Importing an SRR still auto-selects all
  installed versions in the matching majors (and expands those groups); **Rescan** picks up
  versions you drop into the folder while the app is running; **All**/**None** for bulk selection.
- **Same-version builds are distinguishable.** Folders that parse to the same version (betas,
  locale builds) show their variant and origin: `2.50 b2  (wrar25b2)`.
- **Exported configurations remember the exact selection.** A saved Reconstructor configuration
  round-trips the individual ticked versions; configurations from older versions keep loading and
  fall back to selecting all installed versions in their enabled majors.
- Starting with a scanned folder but nothing ticked (or a folder with no usable WinRAR versions)
  is now blocked with a clear message instead of silently testing nothing.

### Fixed

- A WinRAR version folder whose name has no parseable version (e.g. `winrar-beta/`) no longer
  crashes the brute-force — it is skipped with a log line.

## [1.7.2] — 2026-06-29

### Fixed

- **Solid releases now reconstruct as solid.** When the imported SRR's archive is solid, the RAR Reconstructor enables solid compression (`-s`) instead of defaulting to non-solid — previously a solid original was rebuilt non-solid, which changes the packed bytes for multi-file solid archives (so they couldn't be reconstructed at all). The advanced tab gains a "-s: Solid archiving." checkbox that's set automatically from the SRR and is mutually exclusive with "-s-: Disable solid archiving.".

## [1.7.1] — 2026-06-29

### Added

- **All archive flags are shown.** The Inspector and Compare tabs now list every header flag — set *and* unset (e.g. VOLUME / SOLID / FIRST_VOLUME), each marked with its meaning or "Not set". In Compare this means the differing flag is highlighted directly, instead of you having to notice a row that's simply missing on one side.
- **End-of-Archive reserved space is shown.** The reserved bytes some archives keep at the end of the terminator (the REV_SPACE region) appear as a "Reserved Space" field, so a 20-byte terminator is no longer indistinguishable from a 13-byte one.
- **Responsive Compare with a busy indicator.** Loading and comparing two files now runs in the background with a "Comparing files…" overlay, so the window no longer freezes; Browse/Swap/Close are disabled while a comparison is in progress.

## [1.7.0] — 2026-06-28

### Added

- **Multi-disc reconstruction.** The RAR Reconstructor now rebuilds releases that contain more than one archive set (e.g. a game's `DVD1` and `DVD2`, or a movie's `CD1`/`CD2`). Each set is brute-forced and reconstructed independently — with its own input file(s), its own settings, and its own expected CRCs — and the rebuilt volumes are written under the release's original subfolders (`output\DVD1\…`, `output\DVD2\…`). When a later set was packed with the same settings as the first, those settings are tried first so it resolves almost immediately. The import shows a notice when a release has multiple sets, and the Brute Force Progress window gains a **Set** column.
- **Every rebuilt volume is verified.** When recreating a whole release, the CRC of *every* produced volume is now checked against the release's `.sfv` (not just the first), so a match means the entire set is byte-exact.

### Fixed

- **Multi-disc releases reconstructed incorrectly.** Previously the whole release was packed as a single archive and only the first produced volume was verified, so additional discs — and the last volume of the first disc — came out with the wrong CRCs and were silently misnamed. The reconstructor now treats each disc as its own archive set and keeps searching settings until one reproduces the complete, fully-verified set.

## [1.6.2] — 2026-06-28

### Changed

- **Clearer RAR Reconstructor "Output" options.** "Stop after the first match — don't keep testing other settings." and "Recreate the whole release (write every volume)." are reworded so it's obvious which one rebuilds the full set of volumes (the "stop after first match" option only controls how long the search runs).
- **One rename option instead of two.** The two near-identical "Rename matched output files…" checkboxes are merged into a single "Rename rebuilt archives to the release's original filenames (from the SRR, or the verification .sfv)." It's shown as a sub-item of "Stop after the first match" and is disabled and cleared when that option is off. (Reconstructor configuration files from older versions keep loading; the rename setting resets to the new option's default.)

## [1.6.1] — 2026-06-27

### Fixed

- **Release == Output no longer deletes the release files.** The RAR Reconstructor refuses to start when the Output folder is the same as — or nested with — the Release folder (which previously let the pre-run cleanup delete the release's source files, and could wedge the Stop button). The overlap now disables Start and shows a red "Release and Output must be different folders." on both fields, in the advanced view and the Beginner wizard.
- **The SRR/SRS Creator embeds the running version.** The "created by" application name stored in settings now refreshes to the current ReScene.NET version after an upgrade, instead of keeping the version saved by an older build; a custom name you set is preserved.
- **The "Verify" field is labelled `.sfv`/`.sha1`** (description, the required-field hint, and the status messages) instead of `.srr` — it never accepted `.srr`.
- **The WinRAR versions folder set in Settings now applies to the RAR Reconstructor without a restart.**

## [1.6.0] — 2026-06-26

### Added

- **RAR Reconstructor — sub-tabbed advanced view.** The advanced reconstructor is reorganized into sub-tabs (Paths, Versions, Compression, Timestamps, Options, Output) under a persistent action bar (Import Config / Import from SRR / Export Config / Start), so it stays usable on small screens. The configuration area is sized to show the paths without scrolling, and the Paths tab shows a warning marker while a required path is missing.
- **"Required" markers for missing paths.** Empty required paths — WinRAR, Release, Verify, and Output — are now flagged inline with an amber "Required" hint, in both the advanced RAR Reconstructor and the Beginner reconstruct wizard.
- **Per-version timing in the Brute Force Progress window.** The progress table now shows Start, End, and Duration for each tested RAR version.

## [1.5.1] — 2026-06-21

### Added

- The Edit-an-SRR wizard's "Preview…" now works for any stored file (not just images) and opens a tabbed preview with **Hex**, **Text** (selectable encoding + word-wrap), and — for images — **Image** tabs.

### Fixed

- The rename / text-input dialog is now wider and resizable, so long stored-file names are fully visible.
- The Bytes/Row selector (in the Inspector's Hex view and the new preview) now shows its selected value — the editable combo box had no visible text area.

### Changed

- Bundled `ReScene.Lib` updated in lockstep — the Inspector now names many more MKV/Matroska elements (FlagLacing, the HDR Colour/MasteringMetadata set, BlockGroup, Cues, and other TrackEntry/Video/Audio fields) instead of "Unknown".

## [1.5.0] — 2026-06-20

### Added

- **View embedded images.** Proof and cover images stored inside an SRR (JPG, PNG, GIF, BMP) open in a resizable preview window — via a "View Image" button in the Inspector's Properties header (or double-clicking the stored file), and a "Preview…" button in the Edit-an-SRR wizard.
- **Inspector Text view.** The Inspector's bottom panel now has Hex and Text tabs; the Text tab decodes the selected block as text with a selectable encoding (UTF-8, UTF-16 LE, UTF-16 BE, ASCII, Windows-1252, ISO-8859-1, and CP437 for DOS/NFO art) and an optional word-wrap, shown up to a 1 MB cap.

### Fixed

- Exporting a stored file from the Inspector now writes just the file's contents, not the surrounding SRR block header — so an exported `.srs`, `.nfo`, or proof image opens correctly in its own application.
- Files that fail to open in the Inspector now show an error dialog explaining why, instead of only a status-bar note that is easy to miss.

### Changed

- Bundled `ReScene.Lib` updated in lockstep — it adds the in-memory stored-file read (`SRRFile.ReadStoredFile`) that backs the new image preview and text view.

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
