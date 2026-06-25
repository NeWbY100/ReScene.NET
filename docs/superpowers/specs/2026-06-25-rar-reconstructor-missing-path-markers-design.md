# RAR Reconstructor — Surface Which Required Paths Are Missing (Design)

**Date:** 2026-06-25
**Status:** Approved (pending implementation plan)
**Branch:** `feat/rar-reconstructor-subtabs` (extends the sub-tabbed-layout work)
**Scope:** `ReconstructorFieldGuidance`, `ReconstructorViewModel`, the advanced view
(`ReconstructorView.xaml`), and the Beginner reconstruct wizard
(`ReconstructWizardBody.xaml`).

## Problem

The new Paths sub-tab shows a ⚠ glyph in its header when a required path is unset, but the
fields themselves give no per-field cue: an empty Release / Verify / Output field shows nothing,
so the user knows *something* is missing but not *which*. The cause is that the path validators
return `FieldState.None` for an empty value, and `FieldStatusLine` hides itself on `None`.
Additionally, the reconstructor's **Output** field has never had a status line at all, so a
missing Output is doubly invisible.

## Goal

Make each unset required path visibly flagged, right under the field, naming what to provide —
in both the advanced RAR Reconstructor and the Beginner reconstruct wizard (they share the same
status properties and validators).

## Non-Goals

- No change to reconstruction logic, Start validation, or the option model.
- No change to other tabs/views or to non-reconstructor view-models.
- No new control type — reuse the existing `FieldStatusLine` and its severity styling.

## Decisions (from brainstorming)

- **Marker style:** amber `Warning` (the `FieldStatusLine` Warning style, ⚠ + `AccentWarning`).
  It matches the Paths-tab glyph color and reads as "needs input", not "error".
- **Scope:** applied at the shared evaluation layer, so both the advanced view and the wizard
  flag missing required fields.

## Architecture

### Evaluation layer — `ReconstructorFieldGuidance`

- `EvaluateWinRarPath`, `EvaluateReleasePath`, `EvaluateVerificationPath`: change the
  empty/whitespace branch from `FieldStatus.None` to a `FieldStatus.Warning(...)` with a
  "Required — …" message. Set-but-invalid keeps its existing `Error`; valid keeps `Ok`/`Info`.
- Add `EvaluateOutputPath(string value)`:
  - empty/whitespace → `Warning("Required — choose the output folder.")`
  - otherwise → `Ok("Output folder set.")` (no existence check; Output is created at Start).
- Rework `PathsNeedAttention` to a severity test rather than the previous `None or Error` check,
  so it stays correct now that "empty" is `Warning`:

  ```csharp
  public static bool PathsNeedAttention(
      string winRarPath, string releasePath, string verificationPath, string outputPath)
  {
      return NeedsAttention(EvaluateWinRarPath(winRarPath))
          || NeedsAttention(EvaluateReleasePath(releasePath))
          || NeedsAttention(EvaluateVerificationPath(verificationPath))
          || NeedsAttention(EvaluateOutputPath(outputPath));
  }

  private static bool NeedsAttention(FieldStatus status) =>
      status.State is not (FieldState.Ok or FieldState.Info);
  ```

  Every existing `PathsNeedAttention` test keeps its result: all-empty → true (all `Warning`);
  Output empty/whitespace → true; non-existent WinRAR → true (`Error`); all-valid → false
  (`Ok`/`Info`).

**Exact messages:**
- WinRAR: `Required — choose the WinRAR installations folder.`
- Release: `Required — choose the release folder.`
- Verify: `Required — choose the .srr/.sfv/.sha1 to verify against.`
- Output: `Required — choose the output folder.`

(The existing valid/invalid messages are unchanged: WinRAR `Ok` "WinRAR installations
directory selected." / `Error` "This WinRAR directory does not exist."; Release `Ok` "Source
files selected." / `Error` "This path does not exist."; Verify `Info` "Reconstructed archives
will be verified against this SRR." / `Error` "This .srr file does not exist.")

### View-model — `ReconstructorViewModel`

- Add `[ObservableProperty] public partial FieldStatus OutputStatus { get; set; } = FieldStatus.None;`
  alongside the other path-status properties, and an `OnOutputPathChanged` hook that sets it via
  `EvaluateOutputPath`.
- Add `private void RefreshPathStatuses()` that recomputes all four statuses from the current
  path strings, and call it:
  - at the end of the constructor (after `ApplyPathDefaultsFromSettings()`), and
  - at the end of `Reset()` (after `ApplyPathDefaultsFromSettings()`), replacing the current
    `WinRarStatus/ReleaseStatus/VerifyStatus = FieldStatus.None` lines.

  This is required because the per-property change hooks only fire when a value actually changes;
  a field that starts empty (and is never assigned) would otherwise keep its default `None` and
  show no marker. `RefreshPathStatuses` guarantees the initial/blank state shows the Required
  markers.

### Views

- `ReconstructorView.xaml` (advanced Paths tab): add
  `<c:FieldStatusLine Status="{Binding OutputStatus}" Margin="0,1,0,2" />` after the Output
  browse `DockPanel`. (WinRAR/Release/Verify already have a status line.)
- `ReconstructWizardBody.xaml` (Files & folders step): add the same `FieldStatusLine` bound to
  `OutputStatus` after the Output `DockPanel` (after line 115). WinRAR/Release/Verify already
  have one.

## Data Flow

Path edits flow through the existing observable setters (browse commands, drag-drop,
import-from-SRR, config apply, Reset), each of which already triggers the per-field hook; the new
`OutputStatus` joins that pattern, and `RefreshPathStatuses` covers the construct/reset baseline.
The Paths-tab glyph continues to read `PathsNeedAttention`, unchanged in behavior.

## Error Handling

No new failure modes. The validators only read path strings and `Directory.Exists`/`File.Exists`
(already the case). Output is intentionally not existence-checked (created at Start).

## Behavior Note

A freshly opened or Reset advanced view / Beginner wizard now shows amber ⚠ "Required" on each
unset path immediately — this is the intended effect of "show which are missing", not an error
state.

## Testing & Verification

- **Unit tests** (`ReconstructorFieldGuidanceTests`):
  - `EvaluateWinRarPath`/`EvaluateReleasePath`/`EvaluateVerificationPath` empty → `FieldState.Warning`.
  - `EvaluateOutputPath`: empty → `Warning`; whitespace → `Warning`; a set path → `Ok`.
  - The existing four `PathsNeedAttention` cases still pass unchanged.
- **Build:** clean non-incremental build of `ReScene.NET` with `-p:BaseOutputPath=bin2/
  --no-incremental` → 0 warnings, 0 errors.
- **Existing tests** stay green (full `ReScene.NET.Tests` run, 0 failures).
- **Manual visual check:** in the advanced Paths tab and the Beginner wizard, empty WinRAR /
  Release / Verify / Output each show the amber Required line; filling a valid value flips it to
  ✓/ℹ and clears the Paths-tab glyph once all four are satisfied.

## File Structure

- **Modify** `ReScene.NET/ViewModels/Reconstruction/ReconstructorFieldGuidance.cs` — empty→Warning,
  add `EvaluateOutputPath`, rework `PathsNeedAttention` + `NeedsAttention` helper.
- **Modify** `ReScene.NET/ViewModels/ReconstructorViewModel.cs` — `OutputStatus` +
  `OnOutputPathChanged`, `RefreshPathStatuses()` (ctor + Reset).
- **Modify** `ReScene.NET/Views/ReconstructorView.xaml` — Output `FieldStatusLine`.
- **Modify** `ReScene.NET/Views/Wizards/ReconstructWizardBody.xaml` — Output `FieldStatusLine`.
- **Extend** `ReScene.NET.Tests/ReconstructorFieldGuidanceTests.cs` — new validator tests.
