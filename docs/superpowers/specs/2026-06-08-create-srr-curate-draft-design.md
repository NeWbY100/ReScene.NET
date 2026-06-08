# Create-an-SRR: Build a Draft, Then Curate — Design

- **Date:** 2026-06-08
- **Status:** Approved (brainstorm)
- **App:** ReScene.NET (WPF, .NET 10, CommunityToolkit.Mvvm)

## Summary

Bring stored-file curation (reorder / rename / remove / extract / add, with
multi-select) into the Beginner **Create an SRR** wizard. After the user points at
a release, the wizard **builds the SRR to a temporary draft**, then presents the
**same "Manage stored files" step as the Edit-an-SRR wizard** on that draft, then
lets the user choose where to save the curated result.

## Background

The Beginner Create wizard is currently three steps (input → output → progress);
the stored-file list is auto-scanned and used silently. The Edit-an-SRR wizard
already has a full Manage step (DataGrid + Add/Remove/Rename/Extract/Move-up/down,
multi-select, click-empty-to-deselect) that operates on a real SRR via
`SRREditor`/`ISrrEditingService`. The Create path passes stored files to the
creation service as an **unordered `IReadOnlyDictionary<string,string>`**, so file
order is not a first-class concept there.

Rather than add an ordered-create API and a parallel manage UI, we **reuse the Edit
machinery**: build a draft SRR, then edit it. This shows the *complete* stored-file
set (including auto-generated SRS / vobsub SRR / fix-RAR entries) and inherits
ordering, rename, extract, and multi-select for free.

## Decisions (from brainstorm)

| Decision | Choice |
|---|---|
| Where curation fits | **Build a draft, then curate it** (reuse Edit's manage step on a temp SRR) |
| Manage actions | **Full parity with Edit** — add, remove, rename, reorder, extract |
| Scope | Beginner **Create wizard** only; the Advanced Creator tab is unchanged |

## The wizard — 5 steps

1. **Choose the release** — `.sfv` / first `.rar` + `InputStatus` (unchanged gating:
   Next enabled when `InputStatus == Ok`). On leaving, prepare a fresh draft temp
   path and start the build.
2. **Building draft…** — progress bar + details log while the SRR is built to the
   temp path (all existing phases: auto-SRS, vobsub SRR, fix-RAR, main create).
   Next is enabled only once the build **succeeds**; on failure Next stays disabled
   and the error log is visible (Back to fix the input). On leaving, the draft is
   adopted by the editor.
3. **Manage stored files** — the shared manage panel (DataGrid + toolbar) bound to
   the editor, operating on the draft. Always allows Next (saving unchanged is
   valid).
4. **Save the SRR as…** — output path + Browse (with name pre-fill) + `OutputStatus`,
   auto-filled to a sibling of the release. NextLabel **Save**; overwrite confirm
   fires before advancing; on leaving, the curated draft is copied to the output.
5. **Done** — result message + log.

## Architecture

### `CreateSrrWizardViewModel` (new facade)

A shared singleton exposing two existing task VMs and coordinating the handoff:

- `Creator` — the existing `CreatorViewModel` (input, options, build, progress).
- `Editor` — a dedicated `SrrEditorViewModel` (manage + save + done).

It forwards both sub-VMs' `PropertyChanged` as its own (same trick as
`BeginnerRestoreViewModel`) so the hosting `WizardViewModel` — which only observes
the facade — re-evaluates step gating when sub-VM fields change. Members:

- `PrepareDraft()` — create a fresh draft temp directory, set
  `Creator.OutputPath` to `<draftDir>/<release>.srr`, set
  `Creator.SuppressOverwriteConfirm = true`, clear `Editor` state, and start the
  build (`Creator.CreateSRRCommand`). Called when leaving step 1.
- `AdoptDraftIntoEditor()` — `Editor.AdoptWorkingCopy(Creator.OutputPath, suggested)`
  where `suggested = FieldGuidance.SuggestSiblingPath(Creator.InputPath, ".srr")`.
  Called when leaving step 2 (build succeeded).
- `Reset()` — cascade `Creator.Reset()` + `Editor.Reset()` (the latter cleans the
  draft temp dir via its working-copy cleanup), and drop the draft temp dir
  reference.

### `StoredFilesManagePanel` (new shared UserControl)

Extract the Edit wizard's manage UI — the `DataGrid` (Add/Remove/Rename/Extract/
Move-up/down toolbar, `ManageStatus` line) **and its code-behind**
(`SelectionChanged` → `SetSelection`, `PreviewMouseDown` empty-space deselect,
`FindAncestor`) — into a reusable control whose `DataContext` is a
`SrrEditorViewModel`. `EditSrrWizardBody` embeds it (inheriting its DataContext);
`CreateSrrWizardBody` embeds it with `DataContext="{Binding Editor}"`. One copy of
the manage UI and its tree-walking logic, used by both wizards.

### Small VM additions

- `CreatorViewModel.BuildSucceeded` (`[ObservableProperty]`, reset on each run and
  in `Reset()`, set `true` only on `result.Success`). Lets the build step gate Next.
- `SrrEditorViewModel.AdoptWorkingCopy(string draftPath, string suggestedOutputPath)`
  — adopt an existing SRR as the working copy (sets `_workingCopyPath = draftPath`,
  marks it adopted), `ReloadList()`, and set `OutputPath = suggestedOutputPath` with
  an `Info` status. Idempotent for the same draft (Back→Next preserves edits).

### `CreateSrrWizardBody` (rewritten) + factory + wiring

- The body has five step panels: input + building bind to `Creator`; manage embeds
  `StoredFilesManagePanel` with `DataContext="{Binding Editor}"`; save + done bind to
  `Editor`.
- `BeginnerWizardFactory.BuildCreateSrr` becomes the 5-step flow above using the
  facade (steps 3/4/5 mirror Edit's Manage/Save/Done, driven via `Editor`).
- `MainWindowViewModel` constructs `CreateSrrWizardViewModel(Creator, new
  SrrEditorViewModel(...))`; `BeginnerShellViewModel` exposes it as `CreateSrrWizard`;
  the factory's `CreateSrr` case resets and builds it.

## Lifecycle & edge cases

- **Build failure:** `BuildSucceeded` stays false → Next disabled; the details log
  shows the error; Back returns to input.
- **Back from manage → building → forward:** the build is started on leaving step 1
  only; re-entering step 2 re-adopts the same draft (reloads on-disk state, which
  already contains any in-place edits) — edits preserved.
- **Changing the input after building:** going Back to step 1 and forward re-runs
  `PrepareDraft()` → a fresh draft dir + rebuild; the prior draft dir is cleaned.
- **Draft temp cleanup:** owned by the editor's working-copy cleanup
  (`ITempDirectoryService.Cleanup`), triggered on `Editor.Reset()` (next wizard open)
  and app shutdown. A leftover draft between close and next open is harmless.
- **Save:** reuses the editor's `Save()` (copy working copy → output), overwrite
  confirm, and the output name pre-fill already in place.
- **Dedicated `Creator`:** the facade owns its own `CreatorViewModel` (not the Advanced
  tab's shared one), so the wizard's draft build never collides with the Advanced SRR
  Creator tab nor leaves a temp draft path behind in it.

## Testing

- `CreateSrrWizardViewModel` (fakes for Creator/Editor collaborators, or the real
  VMs with fake services): `PrepareDraft` sets a temp draft output + starts the
  build; `AdoptDraftIntoEditor` points the editor at the draft with a suggested
  output; `Reset` cascades; sub-VM `PropertyChanged` is forwarded.
- `CreatorViewModel.BuildSucceeded` transitions (true on success, false on failure /
  at start / after `Reset`).
- `SrrEditorViewModel.AdoptWorkingCopy` adopts a draft, loads its list, suggests the
  output, and is idempotent for the same draft (via the existing test seams).
- The manage operations themselves are already covered by `SrrEditorViewModelTests`
  (reused unchanged through `StoredFilesManagePanel`).

## Out of scope

- An ordered stored-file API on the creation service (the draft-then-edit approach
  makes it unnecessary).
- Adding reorder/rename to the Advanced Creator tab.
- Any change to nested vobsub-SRR curation (those remain auto-built).
