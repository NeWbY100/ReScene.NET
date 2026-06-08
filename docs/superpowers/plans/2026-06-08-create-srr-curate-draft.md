# Create-an-SRR "Build a Draft, Then Curate" — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: superpowers:subagent-driven-development. Steps use checkbox (`- [ ]`).

**Goal:** The Beginner Create-an-SRR wizard builds the SRR to a temp draft, then offers the Edit wizard's full Manage step (reorder/rename/remove/extract/add, multi-select) on that draft, then saves the curated result.

**Architecture:** A new `CreateSrrWizardViewModel` facade composes the existing `CreatorViewModel` (build) and a dedicated `SrrEditorViewModel` (manage + save), forwarding their `PropertyChanged` for step gating. The Edit wizard's manage UI + code-behind is extracted into a shared `StoredFilesManagePanel`. See `docs/superpowers/specs/2026-06-08-create-srr-curate-draft-design.md`.

**Tech Stack:** .NET 10, WPF, CommunityToolkit.Mvvm partial-property `[ObservableProperty]`, xUnit.

**Conventions:** conventional commits ending with `Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>`. Build `dotnet build ReScene.NET/ReScene.NET.csproj`; test `dotnet test ReScene.NET.Tests/ReScene.NET.Tests.csproj`.

---

## Group 1 — VM seams (TDD)

### Task 1: `CreatorViewModel.BuildSucceeded`
**Files:** `ReScene.NET/ViewModels/CreatorViewModel.cs`; test `ReScene.NET.Tests/CreatorViewModelTests.cs` (create if absent — otherwise add cases).
- [ ] Add `[ObservableProperty] public partial bool BuildSucceeded { get; set; }`.
- [ ] In `CreateSRRAsync`, set `BuildSucceeded = false` at the start (next to `IsCreating = true`), and `BuildSucceeded = true` only inside the `if (result.Success)` branch.
- [ ] In `Reset()`, set `BuildSucceeded = false`.
- [ ] Build + commit `feat(creator): expose BuildSucceeded for wizard gating`.

### Task 2: `SrrEditorViewModel.AdoptWorkingCopy`
**Files:** `ReScene.NET/ViewModels/SrrEditorViewModel.cs`; test `ReScene.NET.Tests/SrrEditorViewModelTests.cs`.
- [ ] Add:
```csharp
/// <summary>
/// Adopts an already-built SRR (e.g. a freshly created draft) as the working copy to edit,
/// instead of copying from a <see cref="SourcePath"/>. Loads its stored-file list and pre-fills
/// <see cref="OutputPath"/> with <paramref name="suggestedOutputPath"/>. Idempotent for the same
/// draft so Back/Next does not discard in-place edits.
/// </summary>
public void AdoptWorkingCopy(string draftPath, string suggestedOutputPath)
{
    if (_workingCopyPath == draftPath)
    {
        ReloadList();
        return;
    }

    DeleteWorkingCopy();
    _workingCopyPath = draftPath;
    _workingCopySource = null;   // adopted, not copied from a source
    ReloadList();

    OutputPath = suggestedOutputPath;
    OutputStatus = FieldStatus.Info("Auto-filled next to the release. Change it if you want the SRR elsewhere.");
}
```
- [ ] **TDD:** add a `TestSrrEditorViewModel` already overrides `CreateWorkingCopy`/`CopyWorkingCopyTo`; `AdoptWorkingCopy` sets `_workingCopyPath` directly (no seam needed). Test: after `AdoptWorkingCopy(@"X:\draft\out.srr", @"D:\rel\movie.srr")` with the fake service scripted to return two stored names, `StoredFiles` is populated, `OutputPath == @"D:\rel\movie.srr"`, `OutputStatus.State == Info`. Test idempotency: calling again with the same draft path keeps state and only reloads (assert via fake `Calls` containing `GetStoredFiles` twice and no extra working-copy churn). Note: `ReloadList` reads `_srrEditing.GetStoredFiles(_workingCopyPath)`; the fake serves `StoredFileNames`.
- [ ] Build + test + commit `feat(srr-editor): adopt an existing draft as the working copy`.

---

## Group 2 — Shared manage panel

### Task 3: Extract `StoredFilesManagePanel`
**Files:** create `ReScene.NET/Views/Wizards/StoredFilesManagePanel.xaml` (+`.xaml.cs`); modify `ReScene.NET/Views/Wizards/EditSrrWizardBody.xaml` (+`.xaml.cs`).
- [ ] Create `StoredFilesManagePanel` as a `UserControl` (xmlns `c=clr-namespace:ReScene.NET.Controls`) whose **root content is the current Edit "Manage stored files" DockPanel**: the toolbar (`Add files… / Remove / Rename / Extract… / Move up / Move down`, GhostButton), the `c:FieldStatusLine` docked Bottom bound to `ManageStatus`, and the `DataGrid` (`x:Name="StoredFilesGrid"`, `ItemsSource={Binding StoredFiles}`, `SelectedItem={Binding SelectedStoredFile}`, `SelectionChanged`/`PreviewMouseDown` handlers, `SelectionMode=Extended`, `SelectionUnit=FullRow`, the Name + Size columns). Copy these verbatim from `EditSrrWizardBody.xaml` step 1.
- [ ] Move the code-behind handlers (`StoredFilesGrid_SelectionChanged`, `StoredFilesGrid_PreviewMouseDown`, `FindAncestor<T>`) from `EditSrrWizardBody.xaml.cs` into `StoredFilesManagePanel.xaml.cs` (same `using`s; class derives from `UserControl`). The handlers already resolve the VM via `DataContext is SrrEditorViewModel` — unchanged, works whether the DataContext is inherited (Edit) or set to `Editor` (Create).
- [ ] In `EditSrrWizardBody.xaml`, replace the step-1 DockPanel's inner content with `<wizards:StoredFilesManagePanel .../>` inside the existing `IndexToVisibility` DockPanel (add `xmlns:wizards="clr-namespace:ReScene.NET.Views.Wizards"`). Keep the step-1 visibility wrapper. Remove the now-unused handlers/`using`s from `EditSrrWizardBody.xaml.cs` (it returns to the trivial `InitializeComponent` shell).
- [ ] Build. Confirm the Edit wizard still compiles and `SrrEditorViewModelTests` still pass (the VM is unchanged). Commit `refactor(wizards): extract StoredFilesManagePanel shared by Edit/Create`.

---

## Group 3 — Facade + wizard

### Task 4: `CreateSrrWizardViewModel` facade
**Files:** create `ReScene.NET/ViewModels/CreateSrrWizardViewModel.cs`; test `ReScene.NET.Tests/CreateSrrWizardViewModelTests.cs`.
- [ ] Constructed with `(CreatorViewModel creator, SrrEditorViewModel editor, ITempDirectoryService tempDir)`. Expose `public CreatorViewModel Creator { get; }` and `public SrrEditorViewModel Editor { get; }`. In the constructor, subscribe `creator.PropertyChanged += (_, _) => OnPropertyChanged(nameof(Creator));` and the same for `editor` → `nameof(Editor)` (forwarding for wizard gating).
- [ ] `private string? _draftDir;`
- [ ] `public void PrepareDraft()`:
```csharp
// Build the SRR into a throwaway draft so the user can curate it before choosing a save location.
_draftDir = _tempDir.CreateTempDirectory();
string releaseName = Path.GetFileNameWithoutExtension(Creator.InputPath);
if (string.IsNullOrWhiteSpace(releaseName))
{
    releaseName = "release";
}
Creator.OutputPath = Path.Combine(_draftDir, releaseName + ".srr");
Creator.SuppressOverwriteConfirm = true;
if (Creator.CreateSRRCommand.CanExecute(null))
{
    Creator.CreateSRRCommand.Execute(null);
}
```
- [ ] `public void AdoptDraftIntoEditor()`:
```csharp
string suggested = FieldGuidance.SuggestSiblingPath(Creator.InputPath, ".srr");
Editor.AdoptWorkingCopy(Creator.OutputPath, suggested);
```
- [ ] `public void Reset()`: `Creator.Reset(); Editor.Reset(); _draftDir = null;` (Editor.Reset cleans the draft dir via its working-copy cleanup; Creator.Reset clears its own state). Add a class doc-comment noting the draft-dir ownership.
- [ ] **TDD** (`CreateSrrWizardViewModelTests`): use the real `CreatorViewModel` with fake `ISrrCreationService`/`ISrsCreationService`/`IFileDialogService`/`ITempDirectoryService`/`IAppSettingsService`, and a `TestSrrEditorViewModel`-style editor (override the working-copy seams). Cover: (a) `PrepareDraft` sets `Creator.OutputPath` under the temp draft dir, sets `SuppressOverwriteConfirm`, and invokes the creation service; (b) `AdoptDraftIntoEditor` calls `Editor.AdoptWorkingCopy` with the draft path + a sibling-of-input suggestion (assert `Editor.OutputPath` ends with `.srr` next to the input); (c) `Reset` clears both sub-VMs; (d) a `Creator`/`Editor` property change raises the facade's `PropertyChanged` for `nameof(Creator)`/`nameof(Editor)`. (If wiring a full `CreatorViewModel` in a unit test is heavy, introduce a minimal fake creation service that returns `SRRCreationResult` success.)
- [ ] Build + test + commit `feat(create-wizard): add CreateSrrWizardViewModel facade`.

### Task 5: `CreateSrrWizardBody` + factory + shell wiring
**Files:** rewrite `ReScene.NET/Views/Wizards/CreateSrrWizardBody.xaml`; `ReScene.NET/Views/Wizards/BeginnerWizardFactory.cs`; `ReScene.NET/ViewModels/BeginnerShellViewModel.cs`; `ReScene.NET/ViewModels/MainWindowViewModel.cs`.
- [ ] `BeginnerShellViewModel`: add `public CreateSrrWizardViewModel CreateSrrWizard { get; set; } = null!;`
- [ ] `MainWindowViewModel`: construct `var createSrrWizard = new CreateSrrWizardViewModel(Creator, new SrrEditorViewModel(srrEditingService, fileDialog, tempDir), tempDir);` and set `CreateSrrWizard = createSrrWizard` in the `BeginnerShellViewModel` initializer.
- [ ] `CreateSrrWizardBody.xaml` — 5 step panels gated by `IndexToVisibility` (ConverterParameter 0..4), `xmlns:wizards` + `xmlns:c`:
  - **0 (input):** `Creator.InputPath` + Browse (`Creator.BrowseInputCommand`) + `c:FieldStatusLine Status="{Binding Creator.InputStatus}"`. (Copy the existing step-0 markup, re-rooting bindings under `Creator.`.)
  - **1 (building):** progress bar `{Binding Creator.ProgressPercent}` + `{Binding Creator.ProgressMessage}` (`ShowProgress`), Cancel button (`Creator.CancelCreationCommand`, visible while `Creator.IsCreating`), and a Details log `{Binding Creator.LogEntries}` (mirror the current step-2 progress panel, re-rooted under `Creator.`).
  - **2 (manage):** `<wizards:StoredFilesManagePanel DataContext="{Binding Editor}" />` inside the `IndexToVisibility` DockPanel.
  - **3 (save as):** `Editor.OutputPath` + Browse (`Editor.BrowseOutputCommand`) + `c:FieldStatusLine Status="{Binding Editor.OutputStatus}"` (mirror Edit's save step, re-rooted under `Editor.`).
  - **4 (done):** `Editor.ResultMessage` (+`Editor.ShowResult`) + Details log `{Binding Editor.LogEntries}` (mirror Edit's done step).
- [ ] `BeginnerWizardFactory`: change the `CreateSrr` case to `shell.CreateSrrWizard.Reset(); return BuildCreateSrr(shell.CreateSrrWizard);` and rewrite `BuildCreateSrr(CreateSrrWizardViewModel vm)`:
```csharp
private static (WizardViewModel, FrameworkElement) BuildCreateSrr(CreateSrrWizardViewModel vm)
{
    var steps = new List<WizardStep>
    {
        new() { Title = "Choose the release",
                CanAdvance = () => vm.Creator.InputStatus.State == FieldState.Ok,
                OnLeave = vm.PrepareDraft },
        new() { Title = "Building draft",
                CanAdvance = () => !vm.Creator.IsCreating && vm.Creator.BuildSucceeded,
                OnLeave = vm.AdoptDraftIntoEditor },
        new() { Title = "Manage stored files" },
        new()
        {
            Title = "Save as",
            CanAdvance = () => !string.IsNullOrWhiteSpace(vm.Editor.OutputPath),
            NextLabel = "Save",
            ConfirmLeave = () =>
            {
                if (!File.Exists(vm.Editor.OutputPath))
                {
                    return true;
                }
                return MessageBox.Show(
                    $"A file already exists at:\n\n{vm.Editor.OutputPath}\n\nDo you want to overwrite it?",
                    "Overwrite existing file?",
                    MessageBoxButton.OKCancel, MessageBoxImage.Warning) == MessageBoxResult.OK;
            },
            OnLeave = vm.Editor.Save,
        },
        new() { Title = "Done" },
    };
    return (new WizardViewModel("Create an SRR", vm, steps), new CreateSrrWizardBody());
}
```
- [ ] Build + test. Manual smoke: create an SRR from a real release, reorder/rename/extract in the manage step, save, confirm the output reflects the curation and the original release files are untouched. Commit `feat(create-wizard): build a draft then curate in the Create an SRR wizard`.

---

## Group 4 — Verify & finish
- [ ] `dotnet build ReScene.NET.slnx` + `dotnet test` — all green, 0 warnings.
- [ ] Adversarial multi-lens review of the whole diff (facade handoff & PropertyChanged forwarding; build-failure gating; draft temp lifecycle/cleanup; shared panel DataContext correctness in both wizards; non-destructiveness of the source release; Back/Next idempotency).
- [ ] Address findings; finish branch (merge per user).

## Notes
- The build step starts on **leaving step 0** (WizardViewModel increments the index *then* runs `OnLeave`), so the progress shows on step 1 and `BuildSucceeded` gates the Next out of step 1.
- The manage/save/done steps mirror the Edit wizard exactly, driven through `vm.Editor`; the shared `StoredFilesManagePanel` keeps a single copy of the grid + tree-walking code-behind.
- Non-destructiveness: the build reads the release; all edits target the draft working copy; Save copies the draft to the user's output. The source release is never written.
