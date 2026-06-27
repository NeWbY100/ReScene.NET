# Reconstructor — Consolidate the Two Rename Options Into One (Design)

**Date:** 2026-06-28
**Status:** Approved (pending implementation plan)
**Branch:** `fix/reconstructor-rename-suboptions` (extends the not-yet-merged rename sub-options work)
**Scope:** `ReconstructorViewModel`, `ReconstructorConfig`, `ReconstructorConfigMapper`, the advanced
Output tab, and the Beginner wizard.

## Background

The advanced RAR Reconstructor has two rename options:
- `RenameToOriginal` — "rename to the original RAR filenames from the SRR".
- `RenameToSfvNames` — "rename to the names listed in the verification .sfv. Uses the SRR's original
  names when one is loaded."

`ResolveOutputRenameNames()` shows `RenameToSfvNames` is a strict **superset** of `RenameToOriginal`:
when an SRR is loaded *both* use the SRR's names (identical result); only when no SRR is loaded do
they differ — and there `RenameToOriginal` produces no names at all (it needs an SRR), while
`RenameToSfvNames` falls back to the `.sfv`. So `RenameToOriginal` adds no unique capability and the
pair is redundant/confusing. The library already models this as a single flag
(`RAROptions.RenameToOriginalNames` + a names list), so consolidation is a VM/UI change only.

## Decision

Collapse the two options into one — **`RenameToReleaseNames`** (default `true`):

> *Rename rebuilt archives to the release's original filenames — from the SRR when one is loaded,
> otherwise from the verification `.sfv`.*

This is exactly the existing `RenameToSfvNames` behavior. **Clean break on config:** old exported
configs' rename settings are not migrated (the removed JSON fields are ignored on load; the new flag
defaults to on).

## Architecture

### `ReconstructorViewModel`

- Remove `RenameToOriginal` and `RenameToSfvNames`; add
  `[ObservableProperty] public partial bool RenameToReleaseNames { get; set; } = true;`.
- Remove `IsRenameToOriginalEnabled` and `IsRenameToSfvEnabled`; add
  `public bool IsRenameEnabled => StopOnFirstMatch;`.
- On `StopOnFirstMatch`, replace the two `[NotifyPropertyChangedFor]` attributes with one
  (`nameof(IsRenameEnabled)`).
- `OnStopOnFirstMatchChanged(bool value)`: when `!value`, set `RenameToReleaseNames = false`
  (replacing the two clears).
- `ResolveOutputRenameNames()` (logic unchanged, single gate):
  ```csharp
  private (bool Rename, List<string> Names) ResolveOutputRenameNames()
  {
      if (RenameToReleaseNames && _import.OriginalRarFileNames.Count > 0)
      {
          return (true, _import.OriginalRarFileNames);
      }

      if (RenameToReleaseNames
          && !string.IsNullOrWhiteSpace(VerificationPath)
          && Path.GetExtension(VerificationPath).Equals(".sfv", StringComparison.OrdinalIgnoreCase)
          && File.Exists(VerificationPath))
      {
          try
          {
              List<string> sfvNames = SFVFile.ReadFile(VerificationPath).Entries
                  .Select(e => e.FileName)
                  .Where(RARVolumeIdentifier.IsRarVolume)
                  .ToList();
              if (sfvNames.Count > 0)
              {
                  return (true, sfvNames);
              }
          }
          catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
          {
              Log(LogTarget.System, $"Could not read SFV for output renaming: {ex.Message}");
          }
      }

      return (RenameToReleaseNames, _import.OriginalRarFileNames);
  }
  ```
  (Only the flag names change vs. today; the `BuildRAROptions` call site still feeds the lib's single
  `RenameToOriginalNames` + `OriginalRarFileNames`.)

### `ReconstructorConfig` (clean break)

- Remove `RenameToOriginal` and `RenameToSfvNames`.
- Add `public bool RenameToReleaseNames { get; set; } = true;`.

Old config files that still contain `RenameToOriginal`/`RenameToSfvNames` deserialize fine
(System.Text.Json ignores unknown members) and get the new flag's default (`true`).

### `ReconstructorConfigMapper`

- `Capture`: `RenameToReleaseNames = vm.RenameToReleaseNames` (drop the two old lines).
- `Apply`: `vm.RenameToReleaseNames = c.RenameToReleaseNames` (drop the two old lines). Keep
  `StopOnFirstMatch` applied **last** (so its clear hook still has the final say on import).

### UI

- **Advanced Output tab** (`ReconstructorView.xaml`): the indented sub-item under "Stop after the
  first match" becomes one checkbox:
  `"Rename rebuilt archives to the release's original filenames (from the SRR, or the verification .sfv)."`
  bound to `RenameToReleaseNames`, `IsEnabled="{Binding IsRenameEnabled}"`.
- **Beginner wizard** (`ReconstructWizardBody.xaml`): its single checkbox keeps its label
  ("Rename to the release's file names") and rebinds `IsChecked` to `RenameToReleaseNames` and
  `IsEnabled` to `IsRenameEnabled`.

## Data Flow

Unchanged at the engine: `BuildRAROptions` still calls `ResolveOutputRenameNames()` and sets the
lib's `RAROptions.RenameToOriginalNames` + `OriginalRarFileNames`. The consolidation only reduces
two VM/UI toggles to one.

## Error Handling

No new failure modes (the SFV read keeps its existing `IOException`/`UnauthorizedAccessException`
guard).

## Testing & Verification

- Update the existing rename tests to the single flag:
  - VM: unchecking `StopOnFirstMatch` clears `RenameToReleaseNames`; `IsRenameEnabled` tracks
    `StopOnFirstMatch`.
  - ConfigMapper: round-trip of `RenameToReleaseNames`; applying a config with
    `StopOnFirstMatch=false` + `RenameToReleaseNames=true` normalises the flag to false (the
    StopOnFirstMatch-last ordering + hook).
- Build: clean non-incremental, 0 warnings.
- Manual check: one rename checkbox under "Stop after the first match" in the advanced tab and in
  the wizard; it greys + clears when the parent is unchecked; renaming still works (SRR-loaded and
  .sfv-only cases).

## Non-Goals

- No config migration of the old two flags (clean break, per decision).
- No change to the library (`RAROptions.RenameToOriginalNames` is already the single concept).
- No change to other Output options.

## Implementation Note

This is a single atomic change set: removing the two VM properties while the XAML still binds them
would leave the rename checkbox bound to a missing property (a silent runtime break, not a build
error). So the view-model/config/mapper edits and both XAML rebinds land together.

## File Structure

- `ReScene.NET/ViewModels/ReconstructorViewModel.cs` — single flag, `IsRenameEnabled`, hook,
  `ResolveOutputRenameNames`.
- `ReScene.NET/Models/ReconstructorConfig.cs` — single flag (remove the two old ones).
- `ReScene.NET/ViewModels/Reconstruction/ReconstructorConfigMapper.cs` — Capture/Apply the single flag.
- `ReScene.NET/Views/ReconstructorView.xaml` — single rename checkbox.
- `ReScene.NET/Views/Wizards/ReconstructWizardBody.xaml` — rebind the wizard checkbox.
- `ReScene.NET.Tests/ReconstructorViewModelDialogTests.cs`, `ReScene.NET.Tests/ReconstructorConfigMapperTests.cs` — updated to the single flag.
