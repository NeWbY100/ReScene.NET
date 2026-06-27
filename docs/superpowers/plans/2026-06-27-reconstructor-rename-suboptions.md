# Reconstructor Rename Sub-Options Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Present the two "Rename matched output files…" options as indented sub-items of "Stop after the first match" in the advanced Output tab — greyed while the parent is off, and cleared (unchecked) when the parent is turned off — and keep that invariant on config import.

**Architecture:** A view-model change hook clears the rename flags when `StopOnFirstMatch` goes false; the existing `IsEnabled` greying is kept; the Output-tab XAML nests the two options and drops the now-redundant "Requires Stop on first match." copy; `ReconstructorConfigMapper.Apply` sets `StopOnFirstMatch` last so import stays consistent.

**Tech Stack:** .NET 10, WPF, CommunityToolkit.Mvvm, xUnit.

**Spec:** `docs/superpowers/specs/2026-06-27-reconstructor-rename-suboptions-design.md`

## Global Constraints

- **Target:** `net10.0-windows` (WPF). Do NOT touch the `ReScene.Lib` submodule.
- **The running app locks `ReScene.NET/bin/`.** ALWAYS build/test with `-p:BaseOutputPath=bin2/`. NEVER kill the app.
- **Verify non-incrementally:** `dotnet build ... --no-incremental` → **0 warnings, 0 errors**.
- After verification, delete scratch: `find E:/Projects/ReScene.NET -type d -name bin2 -prune -exec rm -rf {} + 2>/dev/null`.
- **Work on the branch chosen at execution start** — do not switch/rebase/amend. One commit per task.
- **Keep the existing greying** (`IsRenameToOriginalEnabled`/`IsRenameToSfvEnabled` and the `[NotifyPropertyChangedFor]` on `StopOnFirstMatch`) — do not remove it. The hook only *adds* clear-on-disable.
- **Do NOT add auto-checking of the parent** when a rename is checked (out of scope; unreachable while greyed).
- `InternalsVisibleTo ReScene.NET.Tests` is set.
- **End every commit message** with: `Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>`.

---

## Task 1: Clear rename flags when the parent is unchecked (VM + config import)

**Files:**
- Modify: `ReScene.NET/ViewModels/ReconstructorViewModel.cs` (add `OnStopOnFirstMatchChanged`)
- Modify: `ReScene.NET/ViewModels/Reconstruction/ReconstructorConfigMapper.cs` (apply `StopOnFirstMatch` last)
- Test: `ReScene.NET.Tests/ReconstructorViewModelDialogTests.cs` (VM coupling) + `ReScene.NET.Tests/ReconstructorConfigMapperTests.cs` (import consistency)

**Interfaces:**
- Produces: `ReconstructorViewModel` clears `RenameToOriginal` and `RenameToSfvNames` whenever `StopOnFirstMatch` becomes false (via the generated `OnStopOnFirstMatchChanged` partial hook).

- [ ] **Step 1: Write the failing tests**

Add to `ReScene.NET.Tests/ReconstructorViewModelDialogTests.cs` (it has `CreateVm(out _, out _)`):

```csharp
    [Fact]
    public void UncheckingStopOnFirstMatch_ClearsBothRenameFlags()
    {
        ReconstructorViewModel vm = CreateVm(out _, out _);
        vm.StopOnFirstMatch = true;
        vm.RenameToOriginal = true;
        vm.RenameToSfvNames = true;

        vm.StopOnFirstMatch = false;

        Assert.False(vm.RenameToOriginal);
        Assert.False(vm.RenameToSfvNames);
    }

    [Fact]
    public void StopOnFirstMatchOn_RenameSubItemsAreEnabled()
    {
        ReconstructorViewModel vm = CreateVm(out _, out _);
        vm.StopOnFirstMatch = true;
        Assert.True(vm.IsRenameToOriginalEnabled);
        Assert.True(vm.IsRenameToSfvEnabled);

        vm.StopOnFirstMatch = false;
        Assert.False(vm.IsRenameToOriginalEnabled);
        Assert.False(vm.IsRenameToSfvEnabled);
    }

    [Fact]
    public void UncheckingOneRename_DoesNotChangeStopOnFirstMatch()
    {
        ReconstructorViewModel vm = CreateVm(out _, out _);
        vm.StopOnFirstMatch = true;
        vm.RenameToSfvNames = true;

        vm.RenameToSfvNames = false;

        Assert.True(vm.StopOnFirstMatch); // unchanged
    }
```

Add to `ReScene.NET.Tests/ReconstructorConfigMapperTests.cs` (it has `CreateVm()` and `using ReScene.NET.Models;`):

```csharp
    [Fact]
    public void Apply_InconsistentConfig_NormalisesRenamesOffWhenStopIsOff()
    {
        ReconstructorViewModel vm = CreateVm();
        var config = new ReconstructorConfig
        {
            StopOnFirstMatch = false,
            RenameToOriginal = true,
            RenameToSfvNames = true,
        };

        ReconstructorConfigMapper.Apply(vm, config);

        Assert.False(vm.StopOnFirstMatch);
        Assert.False(vm.RenameToOriginal);
        Assert.False(vm.RenameToSfvNames);
    }
```

- [ ] **Step 2: Run the tests to verify they fail**

Run:
```bash
dotnet test E:/Projects/ReScene.NET/ReScene.NET.Tests/ReScene.NET.Tests.csproj \
  --filter "FullyQualifiedName~ReconstructorViewModelDialogTests|FullyQualifiedName~ReconstructorConfigMapperTests" \
  -p:BaseOutputPath=bin2/
```
Expected: `UncheckingStopOnFirstMatch_ClearsBothRenameFlags` and `Apply_InconsistentConfig_NormalisesRenamesOffWhenStopIsOff` FAIL (rename flags stay true — no clear hook yet, and Apply sets StopOnFirstMatch before the renames). `StopOnFirstMatchOn_RenameSubItemsAreEnabled` and `UncheckingOneRename_DoesNotChangeStopOnFirstMatch` already pass (existing behavior).

- [ ] **Step 3: Add the clear-on-disable hook**

In `ReScene.NET/ViewModels/ReconstructorViewModel.cs`, immediately after the output-options properties (after the `RenameToSfvNames` declaration `[ObservableProperty] public partial bool RenameToSfvNames { get; set; } = true;`), add:

```csharp

    /// <summary>
    /// The rename options require Stop-after-first-match, so when it is turned off they are cleared
    /// (not left checked-but-greyed). They cannot be turned on while it is off — the sub-items are
    /// disabled — so no reverse coupling is needed.
    /// </summary>
    partial void OnStopOnFirstMatchChanged(bool value)
    {
        if (!value)
        {
            RenameToOriginal = false;
            RenameToSfvNames = false;
        }
    }
```

- [ ] **Step 4: Apply `StopOnFirstMatch` last in `ReconstructorConfigMapper.Apply`**

In `ReScene.NET/ViewModels/Reconstruction/ReconstructorConfigMapper.cs`, change this block:

```csharp
        vm.DeleteRARFiles = c.DeleteRARFiles;
        vm.DeleteDuplicateCRCFiles = c.DeleteDuplicateCRCFiles;
        vm.StopOnFirstMatch = c.StopOnFirstMatch;
        vm.CompleteAllVolumes = c.CompleteAllVolumes;
        vm.RenameToOriginal = c.RenameToOriginal;
        vm.RenameToSfvNames = c.RenameToSfvNames;
```

to:

```csharp
        vm.DeleteRARFiles = c.DeleteRARFiles;
        vm.DeleteDuplicateCRCFiles = c.DeleteDuplicateCRCFiles;
        vm.CompleteAllVolumes = c.CompleteAllVolumes;
        vm.RenameToOriginal = c.RenameToOriginal;
        vm.RenameToSfvNames = c.RenameToSfvNames;
        // Apply StopOnFirstMatch last: its OnStopOnFirstMatchChanged hook clears the rename flags
        // when false, so an inconsistent (rename-on while stop-off) config normalises on import.
        vm.StopOnFirstMatch = c.StopOnFirstMatch;
```

- [ ] **Step 5: Run the tests + clean build + full suite**

Run:
```bash
dotnet test E:/Projects/ReScene.NET/ReScene.NET.Tests/ReScene.NET.Tests.csproj \
  --filter "FullyQualifiedName~ReconstructorViewModelDialogTests|FullyQualifiedName~ReconstructorConfigMapperTests" \
  -p:BaseOutputPath=bin2/
dotnet build E:/Projects/ReScene.NET/ReScene.NET/ReScene.NET.csproj -p:BaseOutputPath=bin2/ --no-incremental
dotnet test E:/Projects/ReScene.NET/ReScene.NET.Tests/ReScene.NET.Tests.csproj -p:BaseOutputPath=bin2/
```
Expected: the new tests pass; the existing ConfigMapper round-trip tests still pass (they use `StopOnFirstMatch = true`, which never clears); **0 Warning(s) 0 Error(s)**; full suite **0 failures**.

- [ ] **Step 6: Commit**

```bash
git add docs/superpowers/specs/2026-06-27-reconstructor-rename-suboptions-design.md \
        docs/superpowers/plans/2026-06-27-reconstructor-rename-suboptions.md \
        ReScene.NET/ViewModels/ReconstructorViewModel.cs \
        ReScene.NET/ViewModels/Reconstruction/ReconstructorConfigMapper.cs \
        ReScene.NET.Tests/ReconstructorViewModelDialogTests.cs \
        ReScene.NET.Tests/ReconstructorConfigMapperTests.cs
git commit -m "$(cat <<'EOF'
fix(reconstructor): clear rename options when stop-on-first-match is turned off

The rename options depend on StopOnFirstMatch but were only greyed (left
checked) when it was unchecked. Clear them via OnStopOnFirstMatchChanged, and
apply StopOnFirstMatch last on config import so the invariant holds there too.

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>
EOF
)"
```

---

## Task 2: Nest the rename options under the parent (advanced Output tab)

**Files:**
- Modify: `ReScene.NET/Views/ReconstructorView.xaml`

**Interfaces:**
- Consumes: `StopOnFirstMatch`, `RenameToOriginal`, `RenameToSfvNames`, `IsRenameToOriginalEnabled`, `IsRenameToSfvEnabled` (all existing).

- [ ] **Step 1: Reorder + indent the rename options and drop the stale copy**

In `ReScene.NET/Views/ReconstructorView.xaml`, in the Output `TabItem`, replace this block:

```xml
                        <CheckBox Content="Stop after the first match — don't keep testing other settings."
                                  IsChecked="{Binding StopOnFirstMatch}" Margin="0,1" />
                        <CheckBox Content="Recreate the whole release (write every volume)."
                                  IsChecked="{Binding CompleteAllVolumes}" Margin="0,1" />
                        <CheckBox Content="Rename matched output files to the original RAR filenames from the SRR. Requires Stop on first match."
                                  IsChecked="{Binding RenameToOriginal}"
                                  IsEnabled="{Binding IsRenameToOriginalEnabled}" Margin="0,1" />
                        <CheckBox Content="Rename matched output files to the names listed in the verification .sfv. Uses the SRR's original names when one is loaded. Requires Stop on first match."
                                  IsChecked="{Binding RenameToSfvNames}"
                                  IsEnabled="{Binding IsRenameToSfvEnabled}" Margin="0,1" />
```

with (parent, then indented sub-items, then `CompleteAllVolumes`):

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
```

(The `Delete…` checkboxes above and the `Patch…` checkbox below are unchanged.)

- [ ] **Step 2: Clean build + full suite**

Run:
```bash
dotnet build E:/Projects/ReScene.NET/ReScene.NET/ReScene.NET.csproj -p:BaseOutputPath=bin2/ --no-incremental
dotnet test E:/Projects/ReScene.NET/ReScene.NET.Tests/ReScene.NET.Tests.csproj -p:BaseOutputPath=bin2/
```
Expected: **0 Warning(s) 0 Error(s)**; full suite **0 failures** (no test asserts the old label text or ordering).

- [ ] **Step 3: Commit**

```bash
git add ReScene.NET/Views/ReconstructorView.xaml
git commit -m "$(cat <<'EOF'
fix(ui): nest the rename options under "Stop after the first match"

Indent RenameToOriginal/RenameToSfvNames as sub-items of StopOnFirstMatch in the
advanced Output tab and drop the now-redundant "Requires Stop on first match."
copy — the indentation and greying convey the dependency.

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>
EOF
)"
```

---

## Final verification (after all tasks)

- [ ] Clean non-incremental build of `ReScene.NET` with `-p:BaseOutputPath=bin2/`: **0 warnings, 0 errors**.
- [ ] Full `ReScene.NET.Tests` run with `-p:BaseOutputPath=bin2/`: **0 failures**.
- [ ] Delete scratch: `find E:/Projects/ReScene.NET -type d -name bin2 -prune -exec rm -rf {} + 2>/dev/null`.
- [ ] Hand back for a manual check: the two rename options render indented under "Stop after the first match"; unchecking the parent greys *and* unchecks them; re-checking re-enables them.

## Notes on cross-cutting concerns

- **Shared file caution:** Task 1 touches `ReconstructorViewModel.cs` + `ReconstructorConfigMapper.cs`; Task 2 touches only `ReconstructorView.xaml`. The test files (`ReconstructorViewModelDialogTests`, `ReconstructorConfigMapperTests`) are Task 1 only. Run sequentially.
- **No reverse coupling / no re-entrancy:** there are no change hooks on `RenameToOriginal`/`RenameToSfvNames`, so `OnStopOnFirstMatchChanged` setting them to false does not recurse.
- **YAGNI:** keeps the existing greying and computed enable properties; the only new logic is the clear-on-disable hook and the import ordering.
