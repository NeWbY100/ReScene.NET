# Reconstructor — Nest Rename Options Under "Stop after the first match" (Design)

**Date:** 2026-06-27
**Status:** Approved (pending implementation plan)
**Scope:** `ReconstructorView.xaml` (advanced Output tab), `ReconstructorViewModel`, and
`ReconstructorConfigMapper` — a UX + dependency-consistency refinement following the Output-tab
relabel.

## Background

The advanced RAR Reconstructor's Output tab has two "Rename matched output files…" options that
depend on "Stop after the first match" (`StopOnFirstMatch`). Today that dependency is shown only by
a trailing "Requires Stop on first match." in each label, and enforced only by `IsEnabled` greying —
which leaves the rename boxes **checked but greyed** when the parent is unchecked (a stale state).
We just renamed the parent option, so the "Requires Stop on first match." copy is also out of date.

## Goal

Make the dependency obvious and self-consistent: present the two rename options as **indented
sub-items** of "Stop after the first match", greyed while the parent is off, and **cleared** (not
left checked) when the parent is turned off.

## Behaviour

- The two rename options (`RenameToOriginal`, `RenameToSfvNames`) appear indented directly beneath
  the "Stop after the first match" checkbox in the advanced Output tab.
- While "Stop after the first match" is **on**, the sub-items are enabled and checkable.
- When "Stop after the first match" is turned **off**, the sub-items are greyed (as today) **and**
  unchecked (new), so there is no checked-but-greyed stale state.
- The "Requires Stop on first match." text is removed from both rename labels — the indentation and
  greying convey the dependency.

(No auto-checking of the parent is needed: a sub-item cannot be clicked while the parent is off.)

## Architecture

### `ReconstructorView.xaml` — advanced Output tab

Reorder so the rename options sit immediately under their parent, wrapped in an indented panel, and
drop the "Requires Stop on first match." suffix:

```xml
<CheckBox Content="Stop after the first match — don't keep testing other settings."
          IsChecked="{Binding StopOnFirstMatch}" Margin="0,1" />
<StackPanel Margin="20,0,0,0">
    <CheckBox Content="Rename matched output files to the original RAR filenames from the SRR."
              IsChecked="{Binding RenameToOriginal}"
              IsEnabled="{Binding IsRenameToOriginalEnabled}" Margin="0,1" />
    <CheckBox Content="Rename matched output files to the names listed in the verification .sfv. Uses the SRR's original names when one is loaded."
              IsChecked="{Binding RenameToSfvNames}"
              IsEnabled="{Binding IsRenameToSfvEnabled}" Margin="0,1" />
</StackPanel>
<CheckBox Content="Recreate the whole release (write every volume)."
          IsChecked="{Binding CompleteAllVolumes}" Margin="0,1" />
<CheckBox Content="Patch brute-forced RAR headers to match the original archive (Host OS, attributes, LARGE flag, mtime)."
          IsChecked="{Binding EnableHostOSPatching}" Margin="0,1" />
```

`IsRenameToOriginalEnabled` / `IsRenameToSfvEnabled` (`=> StopOnFirstMatch`) and the
`[NotifyPropertyChangedFor]` attributes on `StopOnFirstMatch` are retained — they still drive the
greying.

### `ReconstructorViewModel` — clear sub-items when the parent is unchecked

Add a change hook on `StopOnFirstMatch`:

```csharp
partial void OnStopOnFirstMatchChanged(bool value)
{
    if (!value)
    {
        RenameToOriginal = false;
        RenameToSfvNames = false;
    }
}
```

There are no change hooks on `RenameToOriginal`/`RenameToSfvNames`, so this does not recurse. The
greying (`IsEnabled`) is unchanged; the hook only adds the clear-on-disable.

### `ReconstructorConfigMapper.Apply` — keep the invariant on import

`Apply` currently sets `StopOnFirstMatch` (line 171) **before** the rename flags (173–174). With the
new hook, importing a config where `StopOnFirstMatch` is `false` but a rename flag is `true` (a
possible legacy/hand-edited file) would re-create the inconsistent state. Move the
`vm.StopOnFirstMatch = c.StopOnFirstMatch;` assignment to **after** the two rename assignments, so
applying `StopOnFirstMatch = false` last fires the hook and clears the renames — the imported state
is always consistent. (Consistent configs are unaffected; capture order is irrelevant.)

## Data Flow

`StopOnFirstMatch` setter → `OnStopOnFirstMatchChanged` clears the rename flags when turned off; the
existing `IsEnabled` bindings grey the sub-items. Config import goes through the same setter, now
ordered so the hook has the final say.

## Error Handling

No new failure modes; all changes are UI state and a property-write ordering.

## Testing & Verification

- **VM test:** unchecking `StopOnFirstMatch` sets both `RenameToOriginal` and `RenameToSfvNames` to
  false; with `StopOnFirstMatch` on, `IsRenameToOriginalEnabled` / `IsRenameToSfvEnabled` are true
  (sub-items enabled). Unchecking a single rename does not change `StopOnFirstMatch`.
- **ConfigMapper test:** applying a config with `StopOnFirstMatch = false` and
  `RenameToSfvNames = true` yields a consistent VM state (`StopOnFirstMatch` false, both rename
  flags false). The existing round-trip tests (which use `StopOnFirstMatch = true`) still pass.
- **Build:** clean non-incremental build, 0 warnings.
- **Manual check:** the two rename options render indented under "Stop after the first match";
  unchecking the parent greys *and* unchecks them; re-checking the parent re-enables them.

## Non-Goals

- No auto-checking of `StopOnFirstMatch` when a rename is checked (unreachable — sub-items are
  disabled while the parent is off).
- No change to the Beginner wizard's layout. (The clear-on-disable hook is view-model-level and so
  also covers the wizard's shared `RenameToSfvNames`, but the wizard keeps `StopOnFirstMatch` on, so
  there is no visible change there.)
- No change to `CompleteAllVolumes` / the other Output options beyond their position in the list.

## File Structure

- `ReScene.NET/Views/ReconstructorView.xaml` — reorder + indent the rename options; drop the
  "Requires Stop on first match." copy.
- `ReScene.NET/ViewModels/ReconstructorViewModel.cs` — `OnStopOnFirstMatchChanged` clear hook.
- `ReScene.NET/ViewModels/Reconstruction/ReconstructorConfigMapper.cs` — apply `StopOnFirstMatch`
  after the rename flags.
- `ReScene.NET.Tests/` — the VM coupling test and the ConfigMapper consistency test.
