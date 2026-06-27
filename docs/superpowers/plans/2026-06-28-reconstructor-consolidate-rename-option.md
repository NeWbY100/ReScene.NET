# Consolidate the Two Rename Options Into One — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace the redundant `RenameToOriginal` + `RenameToSfvNames` pair with a single `RenameToReleaseNames` option (default on) across the view-model, config, config mapper, advanced Output tab, and Beginner wizard.

**Architecture:** The library already exposes a single rename concept (`RAROptions.RenameToOriginalNames` + a names list); the two VM toggles just fed it via `ResolveOutputRenameNames`, where `RenameToSfvNames` was already a superset. This is a VM/UI consolidation done as one atomic change set (removing a bound property while the XAML still binds it would silently break the checkbox at runtime). Clean break on config — old configs' rename setting is not migrated.

**Tech Stack:** .NET 10, WPF, CommunityToolkit.Mvvm, xUnit.

**Spec:** `docs/superpowers/specs/2026-06-28-reconstructor-consolidate-rename-option-design.md`

## Global Constraints

- **Target:** `net10.0-windows` (WPF). Do NOT touch the `ReScene.Lib` submodule.
- **The running app locks `ReScene.NET/bin/`.** ALWAYS build/test with `-p:BaseOutputPath=bin2/`. NEVER kill the app.
- **Verify non-incrementally:** `dotnet build ... --no-incremental` → **0 warnings, 0 errors**.
- After verification, delete scratch: `find E:/Projects/ReScene.NET -type d -name bin2 -prune -exec rm -rf {} + 2>/dev/null`.
- **Work on the branch chosen at execution start** (`fix/reconstructor-rename-suboptions`) — do not switch/rebase/amend.
- **New flag:** `RenameToReleaseNames` (bool, default `true`). **New enable property:** `IsRenameEnabled => StopOnFirstMatch`.
- **Clean break:** remove `RenameToOriginal`/`RenameToSfvNames` from the config; do NOT migrate old values.
- **Keep `StopOnFirstMatch` applied last** in `ConfigMapper.Apply` (its clear hook must have the final say).
- **Advanced-tab label (verbatim):** `Rename rebuilt archives to the release's original filenames (from the SRR, or the verification .sfv).`
- **Wizard label unchanged:** `Rename to the release's file names`.
- `InternalsVisibleTo ReScene.NET.Tests` is set.
- **End the commit message** with: `Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>`.

---

## Task 1: Consolidate to a single `RenameToReleaseNames`

**Files:**
- Modify: `ReScene.NET/ViewModels/ReconstructorViewModel.cs`
- Modify: `ReScene.NET/Models/ReconstructorConfig.cs`
- Modify: `ReScene.NET/ViewModels/Reconstruction/ReconstructorConfigMapper.cs`
- Modify: `ReScene.NET/Views/ReconstructorView.xaml`
- Modify: `ReScene.NET/Views/Wizards/ReconstructWizardBody.xaml`
- Test: `ReScene.NET.Tests/ReconstructorViewModelDialogTests.cs`, `ReScene.NET.Tests/ReconstructorConfigMapperTests.cs`

**Interfaces:**
- Produces: `ReconstructorViewModel.RenameToReleaseNames : bool`, `ReconstructorViewModel.IsRenameEnabled : bool`, `ReconstructorConfig.RenameToReleaseNames : bool`. Removes `RenameToOriginal`, `RenameToSfvNames`, `IsRenameToOriginalEnabled`, `IsRenameToSfvEnabled` from the VM and `RenameToOriginal`/`RenameToSfvNames` from the config.

- [ ] **Step 1: Update the tests to the single flag (RED — they won't compile against the old members)**

In `ReScene.NET.Tests/ReconstructorViewModelDialogTests.cs`, replace the three rename tests:

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

with:

```csharp
    [Fact]
    public void UncheckingStopOnFirstMatch_ClearsRename()
    {
        ReconstructorViewModel vm = CreateVm(out _, out _);
        vm.StopOnFirstMatch = true;
        vm.RenameToReleaseNames = true;

        vm.StopOnFirstMatch = false;

        Assert.False(vm.RenameToReleaseNames);
    }

    [Fact]
    public void StopOnFirstMatch_DrivesRenameEnabled()
    {
        ReconstructorViewModel vm = CreateVm(out _, out _);
        vm.StopOnFirstMatch = true;
        Assert.True(vm.IsRenameEnabled);

        vm.StopOnFirstMatch = false;
        Assert.False(vm.IsRenameEnabled);
    }

    [Fact]
    public void UncheckingRename_DoesNotChangeStopOnFirstMatch()
    {
        ReconstructorViewModel vm = CreateVm(out _, out _);
        vm.StopOnFirstMatch = true;
        vm.RenameToReleaseNames = true;

        vm.RenameToReleaseNames = false;

        Assert.True(vm.StopOnFirstMatch); // unchanged
    }
```

In `ReScene.NET.Tests/ReconstructorConfigMapperTests.cs`, replace the `Apply_InconsistentConfig…` test:

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

with:

```csharp
    [Fact]
    public void Apply_InconsistentConfig_NormalisesRenameOffWhenStopIsOff()
    {
        ReconstructorViewModel vm = CreateVm();
        var config = new ReconstructorConfig
        {
            StopOnFirstMatch = false,
            RenameToReleaseNames = true,
        };

        ReconstructorConfigMapper.Apply(vm, config);

        Assert.False(vm.StopOnFirstMatch);
        Assert.False(vm.RenameToReleaseNames);
    }
```

Also in `ReconstructorConfigMapperTests.cs`, replace the remaining old-flag references (the round-trip stamp/asserts):
- `vm.RenameToOriginal = true; vm.RenameToSfvNames = true;` → `vm.RenameToReleaseNames = true;`
- `vm.RenameToOriginal = false; vm.RenameToSfvNames = false;` → `vm.RenameToReleaseNames = false;`
- `Assert.True(vm.RenameToOriginal); Assert.True(vm.RenameToSfvNames);` → `Assert.True(vm.RenameToReleaseNames);`

- [ ] **Step 2: Run the tests to confirm RED (compile failure)**

Run:
```bash
dotnet test E:/Projects/ReScene.NET/ReScene.NET.Tests/ReScene.NET.Tests.csproj \
  --filter "FullyQualifiedName~ReconstructorViewModelDialogTests|FullyQualifiedName~ReconstructorConfigMapperTests" \
  -p:BaseOutputPath=bin2/
```
Expected: **build error** — `RenameToReleaseNames`/`IsRenameEnabled` don't exist yet (CS1061).

- [ ] **Step 3: View-model — single flag, enable property, hook, resolver**

In `ReScene.NET/ViewModels/ReconstructorViewModel.cs`:

(a) On `StopOnFirstMatch`, collapse the two notify attributes to one:
```csharp
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsRenameEnabled))]
    public partial bool StopOnFirstMatch { get; set; } = true;
```

(b) Replace the two rename properties:
```csharp
    [ObservableProperty] public partial bool RenameToOriginal { get; set; }
    [ObservableProperty] public partial bool RenameToSfvNames { get; set; } = true;
```
with one:
```csharp
    [ObservableProperty] public partial bool RenameToReleaseNames { get; set; } = true;
```

(c) Update the clear hook body:
```csharp
    partial void OnStopOnFirstMatchChanged(bool value)
    {
        if (!value)
        {
            RenameToReleaseNames = false;
        }
    }
```

(d) Replace the two enable properties:
```csharp
    public bool IsRenameToOriginalEnabled => StopOnFirstMatch;
    public bool IsRenameToSfvEnabled => StopOnFirstMatch;
```
with one:
```csharp
    public bool IsRenameEnabled => StopOnFirstMatch;
```

(e) Update `ResolveOutputRenameNames` to the single flag (logic unchanged):
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

- [ ] **Step 4: Config + mapper**

In `ReScene.NET/Models/ReconstructorConfig.cs`, replace:
```csharp
    public bool RenameToOriginal { get; set; }
    public bool RenameToSfvNames { get; set; } = true;
```
with:
```csharp
    public bool RenameToReleaseNames { get; set; } = true;
```

In `ReScene.NET/ViewModels/Reconstruction/ReconstructorConfigMapper.cs`:
- In `Capture`, replace:
  ```csharp
        RenameToOriginal = vm.RenameToOriginal,
        RenameToSfvNames = vm.RenameToSfvNames,
  ```
  with:
  ```csharp
        RenameToReleaseNames = vm.RenameToReleaseNames,
  ```
- In `Apply`, replace the two rename lines:
  ```csharp
        vm.RenameToOriginal = c.RenameToOriginal;
        vm.RenameToSfvNames = c.RenameToSfvNames;
  ```
  with:
  ```csharp
        vm.RenameToReleaseNames = c.RenameToReleaseNames;
  ```
  (Leave the `// Apply StopOnFirstMatch last …` comment and the `vm.StopOnFirstMatch = c.StopOnFirstMatch;` line that follows it in place.)

- [ ] **Step 5: XAML — advanced Output tab (one checkbox)**

In `ReScene.NET/Views/ReconstructorView.xaml`, replace the indented two-checkbox panel:
```xml
                        <StackPanel Margin="20,0,0,0">
                            <CheckBox Content="Rename matched output files to the original RAR filenames from the SRR."
                                      IsChecked="{Binding RenameToOriginal}"
                                      IsEnabled="{Binding IsRenameToOriginalEnabled}" Margin="0,1" />
                            <CheckBox Content="Rename matched output files to the names listed in the verification .sfv. Uses the SRR's original names when one is loaded."
                                      IsChecked="{Binding RenameToSfvNames}"
                                      IsEnabled="{Binding IsRenameToSfvEnabled}" Margin="0,1" />
                        </StackPanel>
```
with:
```xml
                        <StackPanel Margin="20,0,0,0">
                            <CheckBox Content="Rename rebuilt archives to the release's original filenames (from the SRR, or the verification .sfv)."
                                      IsChecked="{Binding RenameToReleaseNames}"
                                      IsEnabled="{Binding IsRenameEnabled}" Margin="0,1" />
                        </StackPanel>
```

- [ ] **Step 6: XAML — Beginner wizard (rebind)**

In `ReScene.NET/Views/Wizards/ReconstructWizardBody.xaml`, on the "Rename to the release's file names" checkbox, change:
```xml
                IsChecked="{Binding RenameToSfvNames}"
                IsEnabled="{Binding IsRenameToSfvEnabled}" Margin="0,10,0,2" />
```
to:
```xml
                IsChecked="{Binding RenameToReleaseNames}"
                IsEnabled="{Binding IsRenameEnabled}" Margin="0,10,0,2" />
```
(The `Content="Rename to the release's file names"` and the following descriptive `TextBlock` are unchanged.)

- [ ] **Step 7: Run the tests (GREEN) + clean build + full suite**

Run:
```bash
dotnet test E:/Projects/ReScene.NET/ReScene.NET.Tests/ReScene.NET.Tests.csproj \
  --filter "FullyQualifiedName~ReconstructorViewModelDialogTests|FullyQualifiedName~ReconstructorConfigMapperTests" \
  -p:BaseOutputPath=bin2/
dotnet build E:/Projects/ReScene.NET/ReScene.NET/ReScene.NET.csproj -p:BaseOutputPath=bin2/ --no-incremental
dotnet test E:/Projects/ReScene.NET/ReScene.NET.Tests/ReScene.NET.Tests.csproj -p:BaseOutputPath=bin2/
```
Expected: focused tests pass; **0 Warning(s) 0 Error(s)**; full suite **0 failures** (no remaining references to the removed members anywhere — grep `RenameToOriginal\b|RenameToSfvNames|IsRenameTo.*Enabled` under `ReScene.NET/` and `ReScene.NET.Tests/` should return nothing).

- [ ] **Step 8: Commit**

```bash
git add docs/superpowers/specs/2026-06-28-reconstructor-consolidate-rename-option-design.md \
        docs/superpowers/plans/2026-06-28-reconstructor-consolidate-rename-option.md \
        ReScene.NET/ViewModels/ReconstructorViewModel.cs \
        ReScene.NET/Models/ReconstructorConfig.cs \
        ReScene.NET/ViewModels/Reconstruction/ReconstructorConfigMapper.cs \
        ReScene.NET/Views/ReconstructorView.xaml \
        ReScene.NET/Views/Wizards/ReconstructWizardBody.xaml \
        ReScene.NET.Tests/ReconstructorViewModelDialogTests.cs \
        ReScene.NET.Tests/ReconstructorConfigMapperTests.cs
git commit -m "$(cat <<'EOF'
refactor(reconstructor): consolidate the two rename options into one

RenameToSfvNames was a strict superset of RenameToOriginal (both use the SRR's
names when one is loaded; only the no-SRR fallback differed), so the pair was
redundant and confusing. Replace both with a single RenameToReleaseNames flag
(SRR names if loaded, else the verification .sfv), updating the view-model,
config (clean break — old rename settings not migrated), config mapper, advanced
Output tab, and the Beginner wizard. Engine behaviour is unchanged.

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>
EOF
)"
```

---

## Final verification (after the task)

- [ ] Clean non-incremental build of `ReScene.NET` with `-p:BaseOutputPath=bin2/`: **0 warnings, 0 errors**.
- [ ] Full `ReScene.NET.Tests` run with `-p:BaseOutputPath=bin2/`: **0 failures**.
- [ ] Grep confirms no leftover references to the removed members in `ReScene.NET/` or `ReScene.NET.Tests/`.
- [ ] Delete scratch: `find E:/Projects/ReScene.NET -type d -name bin2 -prune -exec rm -rf {} + 2>/dev/null`.
- [ ] Hand back for a manual check: one rename checkbox under "Stop after the first match" (advanced) and in the wizard; it greys + clears when the parent is unchecked; renaming still works (SRR-loaded and .sfv-only).

## Notes on cross-cutting concerns

- **Atomic by necessity:** the property removal and the XAML rebinds must land in the same commit, or the checkbox would bind to a removed property (a silent runtime break, not a build error). Hence one task.
- **Engine unchanged:** `BuildRAROptions` still feeds the lib's single `RAROptions.RenameToOriginalNames` + `OriginalRarFileNames`; only the VM/UI toggles changed.
- **YAGNI:** no config migration (clean break, per decision); no new control — reuses the existing checkbox + greying.
