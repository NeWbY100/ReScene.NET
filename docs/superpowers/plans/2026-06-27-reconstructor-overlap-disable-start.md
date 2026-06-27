# Reconstructor Overlap — Disable Start + Inline Warning Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** When the Release and Output folders overlap, disable Start and show a red "must be different folders" error on both fields instead of their normal OK messages.

**Architecture:** Two overlap-aware 2-arg overloads in `ReconstructorFieldGuidance` carry the logic (unit-tested); `ReconstructorViewModel` recomputes both Release/Output statuses on either path change and adds `!PathsOverlap` to `CanStart`. Rides on the `fix/v1.6.1-reconstructor` branch.

**Tech Stack:** .NET 10, WPF, CommunityToolkit.Mvvm, xUnit.

**Spec:** `docs/superpowers/specs/2026-06-27-reconstructor-overlap-disable-start-design.md`

## Global Constraints

- **Target:** `net10.0-windows` (WPF). Do NOT touch the `ReScene.Lib` submodule.
- **The running app locks `ReScene.NET/bin/`.** ALWAYS build/test with `-p:BaseOutputPath=bin2/`. NEVER kill the app.
- **Verify non-incrementally:** `dotnet build ... --no-incremental` → **0 warnings, 0 errors**.
- After verification, delete scratch: `find E:/Projects/ReScene.NET -type d -name bin2 -prune -exec rm -rf {} + 2>/dev/null`.
- **Stay on `fix/v1.6.1-reconstructor`** — do not switch/rebase/amend. One commit per task.
- **Overlap error message (verbatim, both fields):** `Release and Output must be different folders.`
- **Severity is `Error` (red ✗)**, not Warning.
- **Keep the existing `StartAsync` overlap guard + dialog** (defense-in-depth) — do not remove it.
- `InternalsVisibleTo ReScene.NET.Tests` is set; `ReconstructorFieldGuidance` is internal.
- **End every commit message** with: `Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>`.

---

## Task 1: Overlap-aware guidance overloads

**Files:**
- Modify: `ReScene.NET/ViewModels/Reconstruction/ReconstructorFieldGuidance.cs`
- Test: `ReScene.NET.Tests/ReconstructorFieldGuidanceTests.cs`

**Interfaces:**
- Produces:
  - `EvaluateReleasePath(string releasePath, string outputPath) : FieldStatus` — overlap → `Error`, else single-path result.
  - `EvaluateOutputPath(string outputPath, string releasePath) : FieldStatus` — overlap → `Error`, else single-path result.
  - `PathsNeedAttention` now also returns true on overlap.

- [ ] **Step 1: Write the failing tests**

Add to `ReScene.NET.Tests/ReconstructorFieldGuidanceTests.cs`:

```csharp
    [Fact]
    public void EvaluateReleasePath_OverlapsOutput_IsError()
    {
        FieldStatus s = ReconstructorFieldGuidance.EvaluateReleasePath(TempDir, TempDir);
        Assert.Equal(FieldState.Error, s.State);
        Assert.Contains("different folders", s.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void EvaluateOutputPath_OverlapsRelease_IsError()
    {
        FieldStatus s = ReconstructorFieldGuidance.EvaluateOutputPath(TempDir, TempDir);
        Assert.Equal(FieldState.Error, s.State);
        Assert.Contains("different folders", s.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void EvaluateReleasePath_NoOverlap_FallsThroughToSinglePath()
    {
        string release = Path.Combine(TempDir, "release");
        string output = Path.Combine(TempDir, "output");
        Directory.CreateDirectory(release);
        FieldStatus s = ReconstructorFieldGuidance.EvaluateReleasePath(release, output);
        Assert.Equal(FieldState.Ok, s.State); // existing release dir -> "Source files selected."
    }

    [Fact]
    public void EvaluateOutputPath_NoOverlap_FallsThroughToSinglePath()
    {
        string release = Path.Combine(TempDir, "release");
        string output = Path.Combine(TempDir, "output");
        FieldStatus s = ReconstructorFieldGuidance.EvaluateOutputPath(output, release);
        Assert.Equal(FieldState.Ok, s.State); // non-empty output -> "Output folder set."
    }

    [Fact]
    public void EvaluateReleasePath_EmptyOutput_NoFalseOverlap()
    {
        // Output empty -> not an overlap; release falls through to its single-path result.
        FieldStatus s = ReconstructorFieldGuidance.EvaluateReleasePath(TempDir, "");
        Assert.Equal(FieldState.Ok, s.State);
    }

    [Fact]
    public void PathsNeedAttention_Overlap_IsTrue()
    {
        string verify = Path.Combine(TempDir, "verify.sfv");
        File.WriteAllText(verify, "");
        // WinRAR/Release/Verify/Output all otherwise valid, but Release == Output.
        Assert.True(ReconstructorFieldGuidance.PathsNeedAttention(TempDir, TempDir, verify, TempDir));
    }
```

- [ ] **Step 2: Run the tests to verify they fail**

Run:
```bash
dotnet test E:/Projects/ReScene.NET/ReScene.NET.Tests/ReScene.NET.Tests.csproj \
  --filter "FullyQualifiedName~ReconstructorFieldGuidanceTests" -p:BaseOutputPath=bin2/
```
Expected: **build error** — no 2-arg `EvaluateReleasePath`/`EvaluateOutputPath` overload (CS1501/CS7036); and once those compile, `PathsNeedAttention_Overlap_IsTrue` would FAIL until the overlap term is added.

- [ ] **Step 3: Add the two overloads**

In `ReScene.NET/ViewModels/Reconstruction/ReconstructorFieldGuidance.cs`, add the release overload immediately after the single-arg `EvaluateReleasePath` (after its closing `}` that follows `return FieldStatus.Ok("Source files selected.");`):

```csharp
    /// <summary>
    /// Overlap-aware release status: a red error when Release and Output overlap (same folder or one
    /// nested in the other), otherwise the single-path result.
    /// </summary>
    public static FieldStatus EvaluateReleasePath(string releasePath, string outputPath)
        => PathsOverlap(releasePath, outputPath)
            ? FieldStatus.Error("Release and Output must be different folders.")
            : EvaluateReleasePath(releasePath);
```

And add the output overload immediately after the single-arg `EvaluateOutputPath` (after its closing `}` that follows `return FieldStatus.Ok("Output folder set.");`):

```csharp
    /// <summary>
    /// Overlap-aware output status: a red error when Release and Output overlap (same folder or one
    /// nested in the other), otherwise the single-path result.
    /// </summary>
    public static FieldStatus EvaluateOutputPath(string outputPath, string releasePath)
        => PathsOverlap(releasePath, outputPath)
            ? FieldStatus.Error("Release and Output must be different folders.")
            : EvaluateOutputPath(outputPath);
```

(`PathsOverlap` is defined later in the same class — a forward reference is fine in C#.)

- [ ] **Step 4: Add the overlap term to `PathsNeedAttention`**

In the same file, change `PathsNeedAttention` to:

```csharp
    public static bool PathsNeedAttention(
        string winRarPath, string releasePath, string verificationPath, string outputPath)
    {
        return NeedsAttention(EvaluateWinRarPath(winRarPath))
            || NeedsAttention(EvaluateReleasePath(releasePath))
            || NeedsAttention(EvaluateVerificationPath(verificationPath))
            || NeedsAttention(EvaluateOutputPath(outputPath))
            || PathsOverlap(releasePath, outputPath);
    }
```

(The single-arg `EvaluateReleasePath`/`EvaluateOutputPath` calls here stay single-arg; the explicit `PathsOverlap` term covers the overlap case without ambiguity.)

- [ ] **Step 5: Run the tests to verify they pass**

Run:
```bash
dotnet test E:/Projects/ReScene.NET/ReScene.NET.Tests/ReScene.NET.Tests.csproj \
  --filter "FullyQualifiedName~ReconstructorFieldGuidanceTests" -p:BaseOutputPath=bin2/
```
Expected: **Passed!** (the six new tests plus all existing guidance tests).

- [ ] **Step 6: Clean build**

Run:
```bash
dotnet build E:/Projects/ReScene.NET/ReScene.NET/ReScene.NET.csproj -p:BaseOutputPath=bin2/ --no-incremental
```
Expected: **Build succeeded. 0 Warning(s) 0 Error(s)**. (The single-arg `EvaluateReleasePath`/`EvaluateOutputPath` are still called by the VM, so adding overloads does not break existing call sites.)

- [ ] **Step 7: Commit**

```bash
git add docs/superpowers/specs/2026-06-27-reconstructor-overlap-disable-start-design.md \
        docs/superpowers/plans/2026-06-27-reconstructor-overlap-disable-start.md \
        ReScene.NET/ViewModels/Reconstruction/ReconstructorFieldGuidance.cs \
        ReScene.NET.Tests/ReconstructorFieldGuidanceTests.cs
git commit -m "$(cat <<'EOF'
feat(reconstructor): overlap-aware Release/Output status overloads

Add 2-arg EvaluateReleasePath/EvaluateOutputPath overloads that return a red
"Release and Output must be different folders." error when the two paths
overlap, and include the overlap in PathsNeedAttention so the Paths-tab glyph
reflects it.

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>
EOF
)"
```

---

## Task 2: View-model wiring — both fields red + Start disabled

**Files:**
- Modify: `ReScene.NET/ViewModels/ReconstructorViewModel.cs`
- Test: `ReScene.NET.Tests/ReconstructorViewModelDialogTests.cs`

**Interfaces:**
- Consumes: the Task 1 overloads and `PathsOverlap`.

- [ ] **Step 1: Write the failing VM tests**

Add to `ReScene.NET.Tests/ReconstructorViewModelDialogTests.cs` (the file already has `using ReScene.NET.Models;`, `CreateVm`, and `NewTempDir`):

```csharp
    [Fact]
    public void ReleaseEqualsOutput_TurnsBothStatusesRed_AndClears()
    {
        ReconstructorViewModel vm = CreateVm(out _, out _);
        string shared = NewTempDir();

        vm.ReleasePath = shared;
        vm.OutputPath = shared; // overlap

        Assert.Equal(FieldState.Error, vm.ReleaseStatus.State);
        Assert.Equal(FieldState.Error, vm.OutputStatus.State);

        vm.OutputPath = NewTempDir(); // separate folder clears the overlap

        Assert.NotEqual(FieldState.Error, vm.ReleaseStatus.State);
        Assert.NotEqual(FieldState.Error, vm.OutputStatus.State);
    }

    [Fact]
    public void CanStart_FalseWhenReleaseEqualsOutput_TrueWhenSeparate()
    {
        ReconstructorViewModel vm = CreateVm(out _, out _);
        string release = NewTempDir();

        vm.WinRarPath = NewTempDir();
        vm.ReleasePath = release;
        vm.OutputPath = release; // overlap

        Assert.False(vm.StartCommand.CanExecute(null));

        vm.OutputPath = NewTempDir(); // separate folder

        Assert.True(vm.StartCommand.CanExecute(null));
    }
```

- [ ] **Step 2: Run them to verify they fail**

Run:
```bash
dotnet test E:/Projects/ReScene.NET/ReScene.NET.Tests/ReScene.NET.Tests.csproj \
  --filter "FullyQualifiedName~ReconstructorViewModelDialogTests" -p:BaseOutputPath=bin2/
```
Expected: both new tests FAIL — `ReleaseStatus`/`OutputStatus` are not yet overlap-aware (they stay Ok), and `CanStart` does not yet gate on overlap (so it returns true).

- [ ] **Step 3: Cross-couple the Release/Output status hooks**

In `ReScene.NET/ViewModels/ReconstructorViewModel.cs`, replace the `OnReleasePathChanged` and `OnOutputPathChanged` hooks:

```csharp
    partial void OnReleasePathChanged(string value) =>
        ReleaseStatus = ReconstructorFieldGuidance.EvaluateReleasePath(value);
```
and
```csharp
    partial void OnOutputPathChanged(string value) =>
        OutputStatus = ReconstructorFieldGuidance.EvaluateOutputPath(value);
```
with:

```csharp
    partial void OnReleasePathChanged(string value) => RefreshReleaseOutputStatuses();

    partial void OnOutputPathChanged(string value) => RefreshReleaseOutputStatuses();

    /// <summary>
    /// Recomputes the Release and Output statuses together: an overlap between the two folders is a
    /// relationship, so a change to either must re-evaluate both (turning both red on overlap, or
    /// clearing both when resolved).
    /// </summary>
    private void RefreshReleaseOutputStatuses()
    {
        ReleaseStatus = ReconstructorFieldGuidance.EvaluateReleasePath(ReleasePath, OutputPath);
        OutputStatus = ReconstructorFieldGuidance.EvaluateOutputPath(OutputPath, ReleasePath);
    }
```

(Leave `OnWinRarPathChanged` and `OnVerificationPathChanged` unchanged.)

- [ ] **Step 4: Use the overlap-aware pair in `RefreshPathStatuses`**

In the same file, change `RefreshPathStatuses` to delegate the Release/Output pair:

```csharp
    private void RefreshPathStatuses()
    {
        WinRarStatus = ReconstructorFieldGuidance.EvaluateWinRarPath(WinRarPath);
        VerifyStatus = ReconstructorFieldGuidance.EvaluateVerificationPath(VerificationPath);
        RefreshReleaseOutputStatuses();
    }
```

- [ ] **Step 5: Gate `CanStart` on overlap**

In the same file, change `CanStart`:

```csharp
    private bool CanStart() => !IsRunning
        && !string.IsNullOrWhiteSpace(WinRarPath)
        && !string.IsNullOrWhiteSpace(ReleasePath)
        && !string.IsNullOrWhiteSpace(OutputPath)
        && !ReconstructorFieldGuidance.PathsOverlap(ReleasePath, OutputPath);
```

(`ReleasePath` and `OutputPath` already carry `[NotifyCanExecuteChangedFor(nameof(StartCommand))]`, so the button re-evaluates when either changes.)

- [ ] **Step 6: Run the VM tests + clean build + full suite**

Run:
```bash
dotnet test E:/Projects/ReScene.NET/ReScene.NET.Tests/ReScene.NET.Tests.csproj \
  --filter "FullyQualifiedName~ReconstructorViewModelDialogTests" -p:BaseOutputPath=bin2/
dotnet build E:/Projects/ReScene.NET/ReScene.NET/ReScene.NET.csproj -p:BaseOutputPath=bin2/ --no-incremental
dotnet test E:/Projects/ReScene.NET/ReScene.NET.Tests/ReScene.NET.Tests.csproj -p:BaseOutputPath=bin2/
```
Expected: the two new tests pass; **0 Warning(s) 0 Error(s)**; full suite **0 failures**.

- [ ] **Step 7: Commit**

```bash
git add ReScene.NET/ViewModels/ReconstructorViewModel.cs \
        ReScene.NET.Tests/ReconstructorViewModelDialogTests.cs
git commit -m "$(cat <<'EOF'
feat(reconstructor): disable Start and flag both fields on Release/Output overlap

Recompute both Release and Output statuses on either path change (so an overlap
turns both red and clears together) and gate CanStart on PathsOverlap so Start
greys out while the two folders match. The StartAsync guard remains as a backstop.

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>
EOF
)"
```

---

## Final verification (after all tasks)

- [ ] Clean non-incremental build of `ReScene.NET` with `-p:BaseOutputPath=bin2/`: **0 warnings, 0 errors**.
- [ ] Full `ReScene.NET.Tests` run with `-p:BaseOutputPath=bin2/`: **0 failures**.
- [ ] Delete scratch: `find E:/Projects/ReScene.NET -type d -name bin2 -prune -exec rm -rf {} + 2>/dev/null`.
- [ ] Hand back for a manual check: pick the same folder for Release and Output → both lines turn red with "Release and Output must be different folders." and Start greys out; change either to a separate folder → both clear and Start re-enables.

## Notes on cross-cutting concerns

- **Shared files across tasks:** `ReconstructorFieldGuidance.cs` (Task 1) then `ReconstructorViewModel.cs` (Task 2); the test files differ. Run sequentially.
- **YAGNI:** reuses `FieldStatusLine`'s existing `Error` styling and the existing `PathsOverlap`; no new control, converter, or message infrastructure. The `StartAsync` deletion guard is retained unchanged as defense-in-depth.
