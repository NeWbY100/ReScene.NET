# Edit-an-SRR Wizard — Design

- **Date:** 2026-06-07
- **Status:** Approved (brainstorm), pending implementation plan
- **App:** ReScene.NET (WPF, .NET 10, CommunityToolkit.Mvvm)

## Summary

Add a fifth Beginner-hub card, **Edit an SRR**, that opens a pop-up wizard for
managing an existing SRR's *stored files* (add, remove, rename, reorder). Editing
is **non-destructive**: the original `.srr` is never modified — changes are made on
a working copy and written to an output file the user chooses.

## Background

The library already supports stored-file editing via `SRREditor`, surfaced by
`ISrrEditingService` (`AddStoredFiles`, `RemoveStoredFiles`, `RenameStoredFileAsync`,
`MoveStoredFileAsync`). The **Advanced Inspector** uses all of it, but those
operations rewrite a file **in place**. Beginner mode has no Inspector, so there is
no guided way to edit an SRR there.

## Decisions (from brainstorm)

| Decision | Choice |
|---|---|
| Edit/save model | **Working copy → save to a new file** (original untouched) |
| Operations offered | **Add, remove, rename, reorder** (full parity with the Inspector) |
| Placement | A **5th Beginner hub card**, "Edit an SRR" |

## Approach — working copy

The edit operations rewrite a file in place, so rather than staging a diff of
operations and replaying it (fragile once rename + reorder interact), the wizard
works on a **temp copy**:

1. Leaving Step 1 copies the chosen `.srr` to a temp working file (via
   `ITempDirectoryService`). The original is never touched again.
2. All edits apply to the working copy immediately, reusing `ISrrEditingService`
   as-is, and the displayed list is reloaded after each.
3. The final step copies the working file to the user-chosen output path.

The working copy is created idempotently on leaving Step 1: if one already exists
for the current source it is kept (so navigating Back → Next does not discard
edits); it is recreated only when the source changes.

## The wizard — 4 steps

1. **Choose the SRR** — source `.srr` path + Browse + `SourceStatus` (✓ when a valid
   `.srr`). Next is disabled until valid. Leaving this step builds/refreshes the
   working copy and loads its stored-file list.
2. **Manage stored files** — a list of the working copy's stored files with
   **Add file(s)…, Remove, Rename, Move up, Move down**, each applied to the
   working copy via `ISrrEditingService` and the list reloaded. Always allows Next
   (saving with no changes is valid).
3. **Save as** — output path + Browse + `OutputStatus`, auto-filled to a sibling
   (`<name> (edited).srr`). The Next button reads **Save**; if the output already
   exists, the overwrite confirmation appears **before** advancing (the existing
   `WizardStep.ConfirmLeave` mechanism). Leaving this step copies the working file
   to the output and records the result.
4. **Done** — result message + log.

## Components

- **New `SrrEditorViewModel`** (`ViewModels`): a shared singleton, `Reset()` on each
  wizard open (deletes any prior working copy, clears paths/list). Holds:
  `SourcePath`/`SourceStatus`, `OutputPath`/`OutputStatus`, the working-copy path,
  `ObservableCollection<string> StoredFiles` + `SelectedStoredFile`, a `LogEntries`
  collection, a result/`IsSaving` state, and commands `BrowseSource`,
  `AddStoredFiles`, `RemoveStoredFile`, `RenameStoredFile`, `MoveStoredFileUp`,
  `MoveStoredFileDown`, `BrowseOutput`, and `Save`. Cleans up its temp copy on
  `Reset()`.
- **New `ISrrEditingService.GetStoredFileNames(srrPath)`** — returns the current
  stored-file names (wrapping `SRRFile.Load(...).StoredFiles`), so listing goes
  through the service and the view-model is unit-testable with a fake service.
- **New `BeginnerCard.EditSrr`**, a `BuildEditSrr` in `BeginnerWizardFactory` (the 4
  steps above, with the Save action + overwrite confirm on Step 3), and a new
  `EditSrrWizardBody` view.
- **Hub**: a 5th card "Edit an SRR" in `BeginnerShellView`.

## Edge cases

- **Output == source**: allowed but overwrites the original; the default output is a
  sibling "(edited)" name so this only happens if the user deliberately picks the
  source path. The overwrite confirm still fires.
- **Back to Step 1 then forward**: the working copy is preserved unless the source
  changed (see Approach), so edits aren't lost.
- **Working-copy cleanup**: removed on `Reset()` (next open) and on app shutdown via
  the existing temp-directory cleanup; a leftover temp between close and next open is
  harmless.
- **No stored files / no changes**: Manage step still allows Save (produces a copy).

## Testing

- `ISrrEditingService.GetStoredFileNames` + the `SrrEditorViewModel` orchestration
  (list reload after add/remove/rename/move; Save copies working → output) —
  unit-tested with a fake `ISrrEditingService` and an in-memory/stub file list.
- Working-copy create/save (real `File.Copy`) — a light integration test over a small
  temp SRR, plus manual smoke.
- Wizard navigation is already covered by `WizardViewModelTests`.

## Out of scope

- Editing RAR metadata, archived-file lists, or anything beyond stored files.
- Editing the original in place from Beginner (that remains the Advanced Inspector's job).
