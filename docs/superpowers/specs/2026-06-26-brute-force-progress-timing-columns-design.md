# Brute-Force Progress — Per-Version Start / End / Duration Columns (Design)

**Date:** 2026-06-26
**Status:** Approved (pending implementation plan)
**Scope:** `ReconstructorViewModel.VersionEntry` (the per-test row model) and
`BruteForceProgressWindow.xaml` (the progress modal's table).

## Problem

The "Brute Force Progress" modal lists each tested RAR version+args combination with Version /
Status / Result / Arguments columns, but no timing. Users want to see when each test started,
when it finished, and how long it took.

## Goal

Add **Start**, **End**, and **Duration** columns to the per-version table, populated as each row
runs and completes.

## Non-Goals

- No change to the run-level timing (Elapsed / Remaining / Speed / ETA) above the table.
- No live-ticking Duration on the in-progress row (the still-running row shows Start, with End and
  Duration blank until it completes; the run-level Elapsed/ETA already tick).
- No change to the `ReconstructionProgressTracker` — it already drives every row's status
  transitions, which is all the timing needs.

## Architecture

The tracker creates a `VersionEntry` exactly when a test begins (a new version+args key appears)
and flips the previous row's `Status` to "Complete" at that same moment; the final row is set to
Complete / Cancelled / Error at run end. So the row's lifecycle boundaries already pass through
its `Status` setter. We stamp timing **on the row**, leaving the tracker untouched.

### `ReconstructorViewModel.VersionEntry`

Add:

```csharp
// Timing — StartedAt is stamped when the row is created (i.e. when the test begins).
public DateTime StartedAt { get; } = DateTime.Now;

[ObservableProperty]
[NotifyPropertyChangedFor(nameof(EndText))]
[NotifyPropertyChangedFor(nameof(DurationText))]
public partial DateTime? EndedAt { get; set; }

public string StartText => StartedAt.ToString("HH:mm:ss");
public string EndText => EndedAt?.ToString("HH:mm:ss") ?? string.Empty;
public string DurationText => EndedAt is { } end
    ? ReconstructorFormatting.FormatTimeSpan(end - StartedAt)
    : string.Empty;

// Stamp the end time the moment the row leaves the "Testing" state (Complete / Cancelled /
// Error all flow through this setter, set by the tracker). Idempotent: never re-stamps.
partial void OnStatusChanged(string value)
{
    if (value != "Testing" && EndedAt is null)
    {
        EndedAt = DateTime.Now;
    }
}
```

- `StartedAt` is a get-only auto-property stamped at construction — the row is constructed by the
  tracker's `createRow` the instant the test starts.
- `EndedAt` is nullable and stamped once, when `Status` first changes away from the initial
  "Testing". The `EndedAt is null` guard makes it idempotent (a second terminal status — e.g. a
  result update — does not move it).
- `ReconstructorFormatting.FormatTimeSpan` is the same formatter used for Elapsed/Remaining
  (already in scope via `using ReScene.NET.ViewModels.Reconstruction;` in `ReconstructorViewModel.cs`),
  so Duration matches the `mm:ss` / `h:mm:ss` style.
- Wall-clock `HH:mm:ss` for Start/End matches the ETA field already shown above the table.

### `BruteForceProgressWindow.xaml`

- Add three `DataGridTextColumn`s between **Result** and **Arguments**, so column order becomes:
  `Version | Status | Result | Start | End | Duration | Arguments(*)`. Arguments stays the
  star-sized (stretch) column, last.

  ```xml
  <DataGridTextColumn Header="Start" Binding="{Binding StartText}" Width="70" />
  <DataGridTextColumn Header="End" Binding="{Binding EndText}" Width="70" />
  <DataGridTextColumn Header="Duration" Binding="{Binding DurationText}" Width="70" />
  ```

- Bump the window's default `Width` from `750` to `920` so Arguments keeps usable room with the
  three added columns. `MinWidth` stays `600` (the grid scrolls horizontally if narrowed).

## Data Flow

`createRow` (in `ReconstructorViewModel`) constructs a `VersionEntry` → `StartedAt` stamped. The
tracker later sets `Status` (to Complete/Cancelled/Error) → `OnStatusChanged` stamps `EndedAt` →
`EndText`/`DurationText` raise `PropertyChanged` → the bound columns refresh. No new tracker
plumbing, no new accessors.

## Error Handling

None needed. `EndedAt` stays null (End/Duration blank) for any row that never reaches a terminal
status (it remains the active row); the final active row is always given a terminal status by
`StartAsync` (Complete on finish, Cancelled/Error in catch).

## Testing & Verification

- **Unit tests** on `ReconstructorViewModel.VersionEntry`:
  - A new row: `StartText` is a non-empty `HH:mm:ss` string; `EndText` and `DurationText` are empty.
  - Setting `Status = "Complete"` stamps `EndedAt` (non-null), and `EndText` / `DurationText`
    become non-empty.
  - Idempotency: after a terminal status, setting another terminal status (e.g. "Error") leaves
    `EndedAt` unchanged.
  - While `Status == "Testing"`, `EndText` / `DurationText` remain empty.
- **Build:** clean non-incremental build of `ReScene.NET` with `-p:BaseOutputPath=bin2/
  --no-incremental` → 0 warnings, 0 errors.
- **Existing tests** stay green.
- **Manual visual check:** run a reconstruction; confirm each completed row shows Start, End, and
  a sensible Duration, the running row shows Start only, and Arguments stays readable at the
  default window width.

## File Structure

- **Modify** `ReScene.NET/ViewModels/ReconstructorViewModel.cs` — add the timing members to the
  nested `VersionEntry` class.
- **Modify** `ReScene.NET/Views/BruteForceProgressWindow.xaml` — three columns + default width.
- **Add/extend** a test for `VersionEntry` timing in `ReScene.NET.Tests`.
