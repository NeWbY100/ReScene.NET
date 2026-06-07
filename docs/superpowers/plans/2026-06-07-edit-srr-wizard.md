# Edit-an-SRR Wizard Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: superpowers:subagent-driven-development. Steps use checkbox (`- [ ]`).

**Goal:** A 5th Beginner-hub wizard, "Edit an SRR", that adds/removes/renames/reorders an SRR's stored files non-destructively (working copy → save to a new file).

**Architecture:** A new shared `SrrEditorViewModel` copies the chosen `.srr` to a temp working file, applies edits to it via the existing `ISrrEditingService`, lists stored files via a new `ISrrEditingService.GetStoredFileNames`, and saves the working copy to a user-chosen output. Surfaced through `BeginnerCard.EditSrr` + `BuildEditSrr` (4 wizard steps) + `EditSrrWizardBody`, with a 5th hub card. See `docs/superpowers/specs/2026-06-07-edit-srr-wizard-design.md`.

**Tech Stack:** .NET 10, WPF, CommunityToolkit.Mvvm partial-property `[ObservableProperty]`, xUnit.

**Conventions:** conventional commits ending with `Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>`. Build `dotnet build ReScene.NET/ReScene.NET.csproj`; test `dotnet test ReScene.NET.Tests/ReScene.NET.Tests.csproj`.

---

## Group 1 — Service listing + SrrEditorViewModel (TDD where possible)

### Task 1: `ISrrEditingService.GetStoredFileNames`
**Files:** `ReScene.NET/Services/ISrrEditingService.cs`, `ReScene.NET/Services/SRREditingService.cs`
- [ ] Add to the interface: `IReadOnlyList<string> GetStoredFileNames(string srrFilePath);`
- [ ] Implement in `SRREditingService` by loading the SRR and projecting stored-file names:
```csharp
public IReadOnlyList<string> GetStoredFileNames(string srrFilePath)
    => SRRFile.Load(srrFilePath).StoredFiles.Select(s => s.FileName).ToList();
```
(Confirm `SRRFile.Load` + `StoredFiles` + `SRRStoredFileBlock.FileName` against `ReScene.SRR`; the Inspector uses these. Add `using System.Linq;` / `using ReScene.SRR;` as needed.)
- [ ] Build. Commit `feat(srr): add GetStoredFileNames to the editing service`.

### Task 2: `SrrEditorViewModel`
**Files:** create `ReScene.NET/ViewModels/SrrEditorViewModel.cs`; test `ReScene.NET.Tests/SrrEditorViewModelTests.cs`

Responsibilities (shared singleton; constructed with `ISrrEditingService`, `IFileDialogService`, `ITempDirectoryService`):
- `[ObservableProperty] string SourcePath`, `FieldStatus SourceStatus`, `string OutputPath`, `FieldStatus OutputStatus`.
- `ObservableCollection<string> StoredFiles`, `[ObservableProperty] string? SelectedStoredFile`.
- `ObservableCollection<string> LogEntries`; `[ObservableProperty] bool IsSaving`; `[ObservableProperty] string ResultMessage`; `[ObservableProperty] bool ShowResult`.
- private `string? _workingCopyPath`, `string? _workingCopySource` (the source the working copy was made from).
- `OnSourcePathChanged`: set `SourceStatus` — `Error` if not `.srr`/missing, else `Ok` (and auto-fill `OutputPath` to `<dir>/<name> (edited).srr` when blank, `OutputStatus = Info`).
- `EnsureWorkingCopy()` (called when leaving Step 1): if `_workingCopyPath` exists and `_workingCopySource == SourcePath`, keep it; else copy `SourcePath` → a temp file (via `ITempDirectoryService`), set `_workingCopyPath`/`_workingCopySource`, then `ReloadList()`.
- `ReloadList()`: `StoredFiles` ← `_srrEditing.GetStoredFileNames(_workingCopyPath)`.
- `[RelayCommand] BrowseSourceAsync` (open `.srr`); `[RelayCommand] BrowseOutputAsync` (save `.srr`).
- `[RelayCommand] AddStoredFilesAsync`: pick file(s) → `_srrEditing.AddStoredFiles(_workingCopyPath, [(name, path)…])` → `ReloadList()` → log.
- `[RelayCommand(CanExecute=HasSelection)] RemoveStoredFile`: `_srrEditing.RemoveStoredFiles(_workingCopyPath, [SelectedStoredFile])` → reload → log.
- `[RelayCommand(CanExecute=HasSelection)] RenameStoredFileAsync`: prompt for new name (`IFileDialogService.PromptForTextAsync`) → `RenameStoredFileAsync(working, old, new)` → reload → log.
- `[RelayCommand(CanExecute=HasSelection)] MoveStoredFileUpAsync` / `MoveStoredFileDownAsync`: `MoveStoredFileAsync(working, name, -1/+1)` → reload (preserve selection) → log.
- `Save()`: copy `_workingCopyPath` → `OutputPath` (overwrite), set `ResultMessage`/`ShowResult`, log; on error log + result=failed. (Sync `File.Copy`; wrap in try/catch.)
- `Reset()`: delete `_workingCopyPath` if present; null `_workingCopyPath`/`_workingCopySource`; clear `SourcePath`/`OutputPath`/statuses/`StoredFiles`/`LogEntries`/`ResultMessage`; `ShowResult=false`, `SelectedStoredFile=null`.

- [ ] **TDD:** write `SrrEditorViewModelTests` using a **fake `ISrrEditingService`** (records calls; `GetStoredFileNames` returns a scripted list) and a fake `IFileDialogService`. Cover: `OnSourcePathChanged` sets status + auto-fills output; Remove/Rename/Move call the service with the right args and reload the list; `HasSelection` gates Remove/Rename/Move; `Reset()` clears state. (Working-copy `File.Copy`/`Save` is exercised by the integration smoke, not these unit tests — keep file I/O behind `EnsureWorkingCopy`/`Save` and have tests drive `ReloadList` via a settable working path or an internal seam.)
- [ ] Build + test. Commit `feat(srr): add SrrEditorViewModel for stored-file editing`.

---

## Group 2 — Hub card + wizard

### Task 3: `BeginnerCard.EditSrr` + factory + body + hub wiring
**Files:** `ReScene.NET/ViewModels/BeginnerCard.cs`, `ReScene.NET/ViewModels/MainWindowViewModel.cs`, `ReScene.NET/ViewModels/BeginnerShellViewModel.cs`, `ReScene.NET/Views/Wizards/BeginnerWizardFactory.cs`, create `ReScene.NET/Views/Wizards/EditSrrWizardBody.xaml(.cs)`, `ReScene.NET/Views/BeginnerShellView.xaml`.

- [ ] Add `EditSrr` to `BeginnerCard`.
- [ ] `MainWindowViewModel`: construct a shared `SrrEditor = new SrrEditorViewModel(srrEditingService, fileDialog, tempDir)` and expose it on `BeginnerShellViewModel` (`public SrrEditorViewModel SrrEditor { get; set; }`), like the other task VMs.
- [ ] `BeginnerWizardFactory`: add a `case BeginnerCard.EditSrr: shell.SrrEditor.Reset(); return BuildEditSrr(shell.SrrEditor);` and:
```csharp
private static (WizardViewModel, FrameworkElement) BuildEditSrr(SrrEditorViewModel vm)
{
    var steps = new List<WizardStep>
    {
        new() { Title = "Choose the SRR", CanAdvance = () => vm.SourceStatus.State == FieldState.Ok,
                OnLeave = vm.EnsureWorkingCopy },
        new() { Title = "Manage stored files" },
        new()
        {
            Title = "Save as",
            CanAdvance = () => !string.IsNullOrWhiteSpace(vm.OutputPath),
            NextLabel = "Save",
            ConfirmLeave = () => vm.ConfirmOverwriteOutput(),   // MessageBox OKCancel if File.Exists(OutputPath); true otherwise
            OnLeave = vm.Save,
        },
        new() { Title = "Done" },
    };
    return (new WizardViewModel("Edit an SRR", vm, steps), new EditSrrWizardBody());
}
```
(`EnsureWorkingCopy`, `Save`, `ConfirmOverwriteOutput` are public on the VM; `ConfirmOverwriteOutput` shows the same OKCancel/Warning prompt as the Create SRR overwrite — keep it on the VM or inline in the factory like `BuildCreateSrr`. Match `BuildCreateSrr`'s inline `MessageBox` approach for consistency.)
- [ ] `EditSrrWizardBody.xaml` — 4 step panels gated by `IndexToVisibility` (same pattern as the other bodies):
  - **0**: SourcePath + Browse (`BrowseSourceCommand`) + `c:FieldStatusLine Status="{Binding SourceStatus}"`.
  - **1**: a `DataGrid`/`ListBox` bound to `StoredFiles` (SelectedItem → `SelectedStoredFile`) filling the panel, with a button row: Add files… (`AddStoredFilesCommand`), Remove (`RemoveStoredFileCommand`), Rename (`RenameStoredFileCommand`), Move up (`MoveStoredFileUpCommand`), Move down (`MoveStoredFileDownCommand`).
  - **2**: OutputPath + Browse (`BrowseOutputCommand`) + `OutputStatus`.
  - **3**: `ResultMessage` (+ `ShowResult`) + a Details log (`LogEntries`) filling the panel (mirror CreateSrrWizardBody's run step).
- [ ] `BeginnerShellView.xaml`: add a 5th `HubCardButton` "Edit an SRR" (icon e.g. ✎) with `Tag="{x:Static vm:BeginnerCard.EditSrr}"`.
- [ ] Build + test. Commit `feat(beginner): add Edit an SRR wizard`.

---

## Group 3 — Verify & finish
- [ ] `dotnet build ReScene.NET.slnx` + `dotnet test` — all green.
- [ ] Adversarial review of the whole diff (working-copy lifecycle, non-destructiveness, Reset cleanup, overwrite confirm, hub wiring).
- [ ] Manual smoke: edit an SRR (add/remove/rename/reorder), save to a new file, confirm the original is unchanged; reopen → fresh.
- [ ] Finish branch (merge per user).

## Notes
- Non-destructive guarantee rests on never editing `SourcePath` directly — all edits target `_workingCopyPath`; Save copies to `OutputPath`. Reviewers must confirm no operation is ever passed `SourcePath`.
- `EnsureWorkingCopy` must be idempotent per source so Back→Next doesn't discard edits.
