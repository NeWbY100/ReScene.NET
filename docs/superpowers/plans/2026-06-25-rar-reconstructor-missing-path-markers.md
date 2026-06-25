# Surface Which Required Paths Are Missing — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Flag each unset required path (WinRAR / Release / Verify / Output) with an amber "Required" status line under the field, in both the advanced RAR Reconstructor and the Beginner reconstruct wizard.

**Architecture:** Change the shared `ReconstructorFieldGuidance` validators so an empty required value returns an amber `Warning` (not `None`), add an `EvaluateOutputPath`, and rework `PathsNeedAttention` to a severity test. Surface a new `OutputStatus` on the view-model and ensure the blank/Reset baseline is computed (`RefreshPathStatuses`). Add the missing Output `FieldStatusLine` to both views; the other three fields already render their status.

**Tech Stack:** .NET 10, WPF, CommunityToolkit.Mvvm (partial-property `[ObservableProperty]`), xUnit.

**Spec:** `docs/superpowers/specs/2026-06-25-rar-reconstructor-missing-path-markers-design.md`

## Global Constraints

- **Target framework:** `net10.0-windows` (WPF). Do NOT touch the ReScene.Lib submodule.
- **The running app locks `ReScene.NET/bin/`.** ALWAYS build and test with `-p:BaseOutputPath=bin2/`. NEVER kill the app.
- **Verify with a non-incremental build** so analyzers + XAML re-run: `dotnet build ... --no-incremental`. A clean build must produce **0 warnings, 0 errors** (`AnalysisLevel=latest-All`, `EnforceCodeStyleInBuild`).
- After verification, delete the scratch output: `find E:/Projects/ReScene.NET -type d -name bin2 -prune -exec rm -rf {} + 2>/dev/null` (never the real `bin/`).
- **Work on the existing feature branch `feat/rar-reconstructor-subtabs`** — do not switch/rebase/amend. Each task is one new commit.
- **Marker style is amber `Warning`** (the existing `FieldStatusLine` Warning style — ⚠ + `AccentWarning`). Do NOT use Error/red for the "empty required" case.
- **Exact "Required" messages** (copy verbatim):
  - WinRAR: `Required — choose the WinRAR installations folder.`
  - Release: `Required — choose the release folder.`
  - Verify: `Required — choose the .srr/.sfv/.sha1 to verify against.`
  - Output: `Required — choose the output folder.`
  - Output (set): `Output folder set.`
- **`InternalsVisibleTo ReScene.NET.Tests`** is set, so tests reach the internal `ReconstructorFieldGuidance`.
- **End every commit message** with: `Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>`.
- **No behavioural changes** beyond status display: reconstruction logic, Start validation, and the option model are untouched.

---

## Task 1: Evaluation layer + view-model statuses

Make empty required validators amber `Warning`, add `EvaluateOutputPath`, rework `PathsNeedAttention`, add `OutputStatus` + hook, and compute the blank/Reset baseline via `RefreshPathStatuses`. TDD for the validators.

**Files:**
- Test: `ReScene.NET.Tests/ReconstructorFieldGuidanceTests.cs` (extend)
- Modify: `ReScene.NET/ViewModels/Reconstruction/ReconstructorFieldGuidance.cs`
- Modify: `ReScene.NET/ViewModels/ReconstructorViewModel.cs`

**Interfaces:**
- Produces:
  - `ReconstructorFieldGuidance.EvaluateOutputPath(string value) : FieldStatus` — `public static`. Empty/whitespace → `Warning("Required — choose the output folder.")`; otherwise → `Ok("Output folder set.")`.
  - `ReconstructorFieldGuidance.EvaluateWinRarPath/EvaluateReleasePath/EvaluateVerificationPath` — unchanged signatures; empty branch now returns `Warning(...)` instead of `None`.
  - `ReconstructorFieldGuidance.PathsNeedAttention(string,string,string,string) : bool` — same signature, reworked body (severity test).
  - `ReconstructorViewModel.OutputStatus : FieldStatus` — `[ObservableProperty]`; Task 2 binds the advanced + wizard Output `FieldStatusLine` to it.

- [ ] **Step 1: Write the failing tests**

Extend `ReScene.NET.Tests/ReconstructorFieldGuidanceTests.cs` — add these methods inside the class (after the existing `PathsNeedAttention_AllValid_IsFalse`):

```csharp
    [Fact]
    public void EvaluateWinRarPath_Empty_IsWarning()
    {
        Assert.Equal(FieldState.Warning, ReconstructorFieldGuidance.EvaluateWinRarPath("").State);
    }

    [Fact]
    public void EvaluateReleasePath_Empty_IsWarning()
    {
        Assert.Equal(FieldState.Warning, ReconstructorFieldGuidance.EvaluateReleasePath("").State);
    }

    [Fact]
    public void EvaluateVerificationPath_Empty_IsWarning()
    {
        Assert.Equal(FieldState.Warning, ReconstructorFieldGuidance.EvaluateVerificationPath("").State);
    }

    [Fact]
    public void EvaluateOutputPath_Empty_IsWarning()
    {
        Assert.Equal(FieldState.Warning, ReconstructorFieldGuidance.EvaluateOutputPath("").State);
    }

    [Fact]
    public void EvaluateOutputPath_Whitespace_IsWarning()
    {
        Assert.Equal(FieldState.Warning, ReconstructorFieldGuidance.EvaluateOutputPath("   ").State);
    }

    [Fact]
    public void EvaluateOutputPath_Set_IsOk()
    {
        Assert.Equal(FieldState.Ok, ReconstructorFieldGuidance.EvaluateOutputPath(TempDir).State);
    }
```

The test file already has `using ReScene.NET.ViewModels.Reconstruction;`. Add `using ReScene.NET.Models;` at the top if not present (for `FieldState`).

- [ ] **Step 2: Run the tests to verify they fail**

Run:
```bash
dotnet test E:/Projects/ReScene.NET/ReScene.NET.Tests/ReScene.NET.Tests.csproj \
  --filter "FullyQualifiedName~ReconstructorFieldGuidanceTests" \
  -p:BaseOutputPath=bin2/
```
Expected: **build error** — `'ReconstructorFieldGuidance' does not contain a definition for 'EvaluateOutputPath'` (CS0117), and (once that's added) the three `*_Empty_IsWarning` tests would FAIL because the validators still return `None`. This is the expected red.

- [ ] **Step 3: Update the three validators' empty branch**

In `ReScene.NET/ViewModels/Reconstruction/ReconstructorFieldGuidance.cs`, change each empty/whitespace branch from `FieldStatus.None` to the amber Required warning.

`EvaluateWinRarPath` — replace:
```csharp
        if (string.IsNullOrWhiteSpace(value))
        {
            return FieldStatus.None;
        }

        if (!Directory.Exists(value))
        {
            return FieldStatus.Error("This WinRAR directory does not exist.");
        }

        return FieldStatus.Ok("WinRAR installations directory selected.");
```
with:
```csharp
        if (string.IsNullOrWhiteSpace(value))
        {
            return FieldStatus.Warning("Required — choose the WinRAR installations folder.");
        }

        if (!Directory.Exists(value))
        {
            return FieldStatus.Error("This WinRAR directory does not exist.");
        }

        return FieldStatus.Ok("WinRAR installations directory selected.");
```

`EvaluateReleasePath` — replace its `if (string.IsNullOrWhiteSpace(value)) { return FieldStatus.None; }` block with:
```csharp
        if (string.IsNullOrWhiteSpace(value))
        {
            return FieldStatus.Warning("Required — choose the release folder.");
        }
```

`EvaluateVerificationPath` — replace its `if (string.IsNullOrWhiteSpace(value)) { return FieldStatus.None; }` block with:
```csharp
        if (string.IsNullOrWhiteSpace(value))
        {
            return FieldStatus.Warning("Required — choose the .srr/.sfv/.sha1 to verify against.");
        }
```

- [ ] **Step 4: Add `EvaluateOutputPath` and rework `PathsNeedAttention`**

In the same file, insert `EvaluateOutputPath` directly after `EvaluateVerificationPath` (after its closing `}` at line 61) and before `PathsNeedAttention`:

```csharp
    /// <summary>
    /// Status for the output directory (where rebuilt archives are written). It is created at
    /// Start if missing, so only emptiness is flagged here.
    /// </summary>
    public static FieldStatus EvaluateOutputPath(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return FieldStatus.Warning("Required — choose the output folder.");
        }

        return FieldStatus.Ok("Output folder set.");
    }
```

Then replace the entire `PathsNeedAttention` method (the doc-comment + body, current lines 63–79) with:

```csharp
    /// <summary>
    /// Whether the Paths tab still needs attention: any of the four required paths (WinRAR,
    /// Release, Verify, Output) is empty or invalid, so the run could not start. Drives the
    /// warning glyph on the Paths sub-tab header.
    /// </summary>
    public static bool PathsNeedAttention(
        string winRarPath, string releasePath, string verificationPath, string outputPath)
    {
        return NeedsAttention(EvaluateWinRarPath(winRarPath))
            || NeedsAttention(EvaluateReleasePath(releasePath))
            || NeedsAttention(EvaluateVerificationPath(verificationPath))
            || NeedsAttention(EvaluateOutputPath(outputPath));
    }

    /// <summary>A field needs attention unless its value is accepted (Ok) or merely informational (Info).</summary>
    private static bool NeedsAttention(FieldStatus status) =>
        status.State is not (FieldState.Ok or FieldState.Info);
```

- [ ] **Step 5: Run the validator tests to verify they pass**

Run:
```bash
dotnet test E:/Projects/ReScene.NET/ReScene.NET.Tests/ReScene.NET.Tests.csproj \
  --filter "FullyQualifiedName~ReconstructorFieldGuidanceTests" \
  -p:BaseOutputPath=bin2/
```
Expected: **Passed!** All `ReconstructorFieldGuidanceTests` pass — the six new ones plus the four pre-existing `PathsNeedAttention_*` (which keep their results: all-empty → true via Warning; Output empty/whitespace → true; non-existent WinRAR → true via Error; all-valid → false via Ok/Info).

- [ ] **Step 6: Add `OutputStatus`, its hook, and `RefreshPathStatuses` to the view-model**

In `ReScene.NET/ViewModels/ReconstructorViewModel.cs`:

(a) In the "Path status" region, after the `VerifyStatus` property (current line 158), add:
```csharp
    [ObservableProperty]
    public partial FieldStatus OutputStatus { get; set; } = FieldStatus.None;
```

(b) After `OnVerificationPathChanged` (current lines 166–167) and before the `PathsNeedAttention` doc-comment (current line 169), add the Output hook and the refresh helper:
```csharp
    partial void OnOutputPathChanged(string value) =>
        OutputStatus = ReconstructorFieldGuidance.EvaluateOutputPath(value);

    /// <summary>
    /// Recomputes all four path statuses from the current path values. Called at construction and
    /// after <see cref="Reset"/> so a blank field shows its "Required" marker immediately — the
    /// per-property change hooks only fire when a value actually changes.
    /// </summary>
    private void RefreshPathStatuses()
    {
        WinRarStatus = ReconstructorFieldGuidance.EvaluateWinRarPath(WinRarPath);
        ReleaseStatus = ReconstructorFieldGuidance.EvaluateReleasePath(ReleasePath);
        VerifyStatus = ReconstructorFieldGuidance.EvaluateVerificationPath(VerificationPath);
        OutputStatus = ReconstructorFieldGuidance.EvaluateOutputPath(OutputPath);
    }
```

(c) In the constructor, call `RefreshPathStatuses()` right after `ApplyPathDefaultsFromSettings();` (current line 70). The end of the constructor becomes:
```csharp
        ApplyPathDefaultsFromSettings();
        RefreshPathStatuses();
    }
```

(d) In `Reset()`, remove the now-incorrect explicit `None` block (current lines 462–465):
```csharp
        // Path status
        WinRarStatus = FieldStatus.None;
        ReleaseStatus = FieldStatus.None;
        VerifyStatus = FieldStatus.None;

```
and call `RefreshPathStatuses()` right after the final `ApplyPathDefaultsFromSettings();` (current line 491). The end of `Reset()` becomes:
```csharp
        // The paths were just cleared; pre-fill the configured defaults again.
        ApplyPathDefaultsFromSettings();
        RefreshPathStatuses();
    }
```

- [ ] **Step 7: Verify the whole solution builds clean (non-incremental) and tests pass**

Run:
```bash
dotnet build E:/Projects/ReScene.NET/ReScene.NET/ReScene.NET.csproj -p:BaseOutputPath=bin2/ --no-incremental
dotnet test E:/Projects/ReScene.NET/ReScene.NET.Tests/ReScene.NET.Tests.csproj -p:BaseOutputPath=bin2/
```
Expected: **Build succeeded. 0 Warning(s) 0 Error(s)** and the full app-test suite **0 failures**.

- [ ] **Step 8: Commit**

```bash
git add docs/superpowers/specs/2026-06-25-rar-reconstructor-missing-path-markers-design.md \
        docs/superpowers/plans/2026-06-25-rar-reconstructor-missing-path-markers.md \
        ReScene.NET/ViewModels/Reconstruction/ReconstructorFieldGuidance.cs \
        ReScene.NET/ViewModels/ReconstructorViewModel.cs \
        ReScene.NET.Tests/ReconstructorFieldGuidanceTests.cs
git commit -m "$(cat <<'EOF'
feat(ui): flag empty required reconstructor paths as "Required"

Empty WinRAR/Release/Verify now report an amber Warning instead of None, a new
EvaluateOutputPath flags an empty Output, and PathsNeedAttention switches to a
severity test. The view-model exposes OutputStatus and recomputes all path
statuses at construction and after Reset so blank fields show their marker.

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>
EOF
)"
```

---

## Task 2: Output status line in both views

Add the missing Output `FieldStatusLine` to the advanced Paths tab and the wizard's Files & folders step, bound to `OutputStatus`. The other three fields already have one, so they render the new amber Required automatically.

**Files:**
- Modify: `ReScene.NET/Views/ReconstructorView.xaml`
- Modify: `ReScene.NET/Views/Wizards/ReconstructWizardBody.xaml`

**Interfaces:**
- Consumes: `ReconstructorViewModel.OutputStatus` (from Task 1).

- [ ] **Step 1: Add the Output status line to the advanced Paths tab**

In `ReScene.NET/Views/ReconstructorView.xaml`, find the Output field's `DockPanel` inside the Paths `TabItem` (it ends the Paths `StackPanel`):
```xml
                        <DockPanel Margin="0,0,0,2">
                            <Button DockPanel.Dock="Right" Content="Browse"
                                    Command="{Binding BrowseOutputCommand}"
                                    Style="{StaticResource GhostButton}"
                                    Margin="4,0,0,0" MinWidth="75" />
                            <TextBox x:Name="OutputTextBox" Text="{Binding OutputPath}" />
                        </DockPanel>
```
Add a `FieldStatusLine` immediately after that closing `</DockPanel>` (and before the `</StackPanel>` that closes the Paths content):
```xml
                        <c:FieldStatusLine Status="{Binding OutputStatus}" Margin="0,1,0,2" />
```

- [ ] **Step 2: Add the Output status line to the Beginner wizard**

In `ReScene.NET/Views/Wizards/ReconstructWizardBody.xaml`, find the Output field's `DockPanel` in the Files & folders step (around line 111–115):
```xml
      <DockPanel Margin="0,0,0,2">
        <Button DockPanel.Dock="Right" Content="Browse" Command="{Binding BrowseOutputCommand}"
                Style="{StaticResource GhostButton}" Margin="4,0,0,0" MinWidth="75" />
        <TextBox Text="{Binding OutputPath}" />
      </DockPanel>
```
Add a `FieldStatusLine` immediately after that closing `</DockPanel>` (it sits between the Output `DockPanel` and the "Verification file" `TextBlock`):
```xml
      <c:FieldStatusLine Status="{Binding OutputStatus}" />
```
(The `c:` namespace is already declared at the top of this file.)

- [ ] **Step 3: Verify the solution builds clean (non-incremental)**

Run:
```bash
dotnet build E:/Projects/ReScene.NET/ReScene.NET/ReScene.NET.csproj -p:BaseOutputPath=bin2/ --no-incremental
```
Expected: **Build succeeded. 0 Warning(s) 0 Error(s)** — confirms the XAML compiles and `OutputStatus` / `FieldStatusLine` resolve in both views.

- [ ] **Step 4: Run the full app-test suite (no regression)**

Run:
```bash
dotnet test E:/Projects/ReScene.NET/ReScene.NET.Tests/ReScene.NET.Tests.csproj -p:BaseOutputPath=bin2/
```
Expected: **0 failures**.

- [ ] **Step 5: Commit**

```bash
git add ReScene.NET/Views/ReconstructorView.xaml ReScene.NET/Views/Wizards/ReconstructWizardBody.xaml
git commit -m "$(cat <<'EOF'
feat(ui): show Output path status line in reconstructor and wizard

Binds a FieldStatusLine to OutputStatus under the Output field in both the
advanced RAR Reconstructor and the Beginner reconstruct wizard, so a missing
Output is flagged like the other required paths.

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>
EOF
)"
```

---

## Final verification (after all tasks)

- [ ] Clean non-incremental build of `ReScene.NET` with `-p:BaseOutputPath=bin2/`: **0 warnings, 0 errors**.
- [ ] Full `ReScene.NET.Tests` run with `-p:BaseOutputPath=bin2/`: **0 failures**.
- [ ] Delete scratch output: `find E:/Projects/ReScene.NET -type d -name bin2 -prune -exec rm -rf {} + 2>/dev/null`.
- [ ] Hand back to the user for a visual check: in the advanced Paths tab and the Beginner wizard, each empty WinRAR / Release / Verify / Output shows an amber ⚠ "Required …" line; filling a valid value flips it to ✓/ℹ; the Paths-tab glyph clears once all four are satisfied.

## Notes on cross-cutting concerns

- **DRY/YAGNI:** the change lives in the one shared validator class; both views and the tab glyph read from it. No duplicated logic, no new control type — the existing `FieldStatusLine` and its severity styling are reused.
- **WPF layout/visual state is not snapshot/Playwright-tested** in this repo, so the visual confirmation is manual (final step). The automated gate is a zero-warning build plus the validator unit tests.
