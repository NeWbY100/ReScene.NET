# Brute-Force Progress Timing Columns Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add Start / End / Duration columns to the Brute Force Progress modal's per-version table.

**Architecture:** Stamp timing on the row model (`ReconstructorViewModel.VersionEntry`): `StartedAt` at construction, `EndedAt` when `Status` leaves "Testing" (via an `OnStatusChanged` hook). The `ReconstructionProgressTracker` already constructs each row at test start and sets its terminal `Status`, so no tracker changes are needed. Three computed display strings back three new `DataGridTextColumn`s.

**Tech Stack:** .NET 10, WPF, CommunityToolkit.Mvvm (partial-property `[ObservableProperty]`), xUnit.

**Spec:** `docs/superpowers/specs/2026-06-26-brute-force-progress-timing-columns-design.md`

## Global Constraints

- **Target framework:** `net10.0-windows` (WPF). Do NOT touch the ReScene.Lib submodule.
- **The running app locks `ReScene.NET/bin/`.** ALWAYS build and test with `-p:BaseOutputPath=bin2/`. NEVER kill the app.
- **Verify with a non-incremental build** so analyzers + XAML re-run: `dotnet build ... --no-incremental`. A clean build must produce **0 warnings, 0 errors** (`AnalysisLevel=latest-All`, `EnforceCodeStyleInBuild`).
- After verification, delete the scratch output: `find E:/Projects/ReScene.NET -type d -name bin2 -prune -exec rm -rf {} + 2>/dev/null` (never the real `bin/`).
- **Work on the branch chosen at execution start** — do not switch/rebase/amend. Each task is one new commit.
- **Time formats (verbatim):** Start/End use `HH:mm:ss` (wall clock, matching the ETA field); Duration uses `ReconstructorFormatting.FormatTimeSpan` (the same `mm:ss` / `h:mm:ss` formatter as Elapsed/Remaining).
- **No live-ticking Duration** on the in-progress row — End/Duration stay blank until the row reaches a terminal status.
- **Do not modify `ReconstructionProgressTracker`** — it already drives the row lifecycle this feature relies on.
- **`InternalsVisibleTo ReScene.NET.Tests`** is set; `VersionEntry` is a public nested type (`ReconstructorViewModel.VersionEntry`).
- **End every commit message** with: `Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>`.

---

## Task 1: Row-level timing on `VersionEntry`

Add `StartedAt`/`EndedAt` and the `StartText`/`EndText`/`DurationText` display strings, stamping the end time when the row leaves "Testing". TDD.

**Files:**
- Test: `ReScene.NET.Tests/VersionEntryTests.cs` (create)
- Modify: `ReScene.NET/ViewModels/ReconstructorViewModel.cs` (the nested `VersionEntry` class)

**Interfaces:**
- Produces (on `ReconstructorViewModel.VersionEntry`):
  - `DateTime StartedAt { get; }` — stamped at construction.
  - `DateTime? EndedAt { get; set; }` — `[ObservableProperty]`; stamped once when `Status` first leaves "Testing".
  - `string StartText` / `string EndText` / `string DurationText` — computed display strings; Task 2 binds the new columns to these.

- [ ] **Step 1: Write the failing tests**

Create `ReScene.NET.Tests/VersionEntryTests.cs`:

```csharp
using ReScene.NET.ViewModels;

namespace ReScene.NET.Tests;

public class VersionEntryTests
{
    [Fact]
    public void NewRow_HasStartText_AndBlankEndAndDuration()
    {
        var row = new ReconstructorViewModel.VersionEntry();
        Assert.Equal(8, row.StartText.Length); // HH:mm:ss
        Assert.Equal(string.Empty, row.EndText);
        Assert.Equal(string.Empty, row.DurationText);
    }

    [Fact]
    public void Complete_StampsEnd_AndDuration()
    {
        var row = new ReconstructorViewModel.VersionEntry();
        row.Status = "Complete";
        Assert.NotNull(row.EndedAt);
        Assert.False(string.IsNullOrEmpty(row.EndText));
        Assert.False(string.IsNullOrEmpty(row.DurationText));
    }

    [Fact]
    public void TerminalStatus_IsIdempotent_DoesNotMoveEnd()
    {
        var row = new ReconstructorViewModel.VersionEntry();
        row.Status = "Complete";
        DateTime? first = row.EndedAt;
        row.Status = "Error";
        Assert.Equal(first, row.EndedAt);
    }

    [Fact]
    public void WhileTesting_EndAndDuration_AreBlank()
    {
        var row = new ReconstructorViewModel.VersionEntry();
        row.Status = "Testing"; // no-op vs the default; must not stamp an end
        Assert.Null(row.EndedAt);
        Assert.Equal(string.Empty, row.EndText);
        Assert.Equal(string.Empty, row.DurationText);
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run:
```bash
dotnet test E:/Projects/ReScene.NET/ReScene.NET.Tests/ReScene.NET.Tests.csproj \
  --filter "FullyQualifiedName~VersionEntryTests" \
  -p:BaseOutputPath=bin2/
```
Expected: **build error** — `'ReconstructorViewModel.VersionEntry' does not contain a definition for 'StartText'` (CS1061) and similar for `EndText`/`DurationText`/`EndedAt`. This is the expected red.

- [ ] **Step 3: Add the timing members to `VersionEntry`**

In `ReScene.NET/ViewModels/ReconstructorViewModel.cs`, replace the entire nested `VersionEntry` class:

```csharp
    public partial class VersionEntry : ObservableObject
    {
        [ObservableProperty] public partial string VersionName { get; set; } = "";
        [ObservableProperty] public partial string Status { get; set; } = "Testing";
        [ObservableProperty] public partial string Arguments { get; set; } = "";
        [ObservableProperty] public partial string Result { get; set; } = "";

        /// <summary>
        /// Directory of the WinRAR version this entry tested; the run executes rar.exe inside it.
        /// </summary>
        public string VersionDirectory { get; set; } = "";

        /// <summary>
        /// The complete command line as executed: the quoted rar.exe path followed by the arguments.
        /// </summary>
        public string FullCommandLine => string.IsNullOrEmpty(VersionDirectory)
            ? Arguments
            : $"\"{Path.Combine(VersionDirectory, "rar.exe")}\" {Arguments}";
    }
```

with:

```csharp
    public partial class VersionEntry : ObservableObject
    {
        [ObservableProperty] public partial string VersionName { get; set; } = "";
        [ObservableProperty] public partial string Status { get; set; } = "Testing";
        [ObservableProperty] public partial string Arguments { get; set; } = "";
        [ObservableProperty] public partial string Result { get; set; } = "";

        /// <summary>
        /// Directory of the WinRAR version this entry tested; the run executes rar.exe inside it.
        /// </summary>
        public string VersionDirectory { get; set; } = "";

        /// <summary>
        /// The complete command line as executed: the quoted rar.exe path followed by the arguments.
        /// </summary>
        public string FullCommandLine => string.IsNullOrEmpty(VersionDirectory)
            ? Arguments
            : $"\"{Path.Combine(VersionDirectory, "rar.exe")}\" {Arguments}";

        // ── Timing ──
        // StartedAt is stamped when the row is created (the tracker constructs a row exactly when
        // its test begins). EndedAt is stamped once, when Status first leaves "Testing".

        /// <summary>When this test started (row construction time).</summary>
        public DateTime StartedAt { get; } = DateTime.Now;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(EndText))]
        [NotifyPropertyChangedFor(nameof(DurationText))]
        public partial DateTime? EndedAt { get; set; }

        /// <summary>Wall-clock start time, e.g. "22:13:28".</summary>
        public string StartText => StartedAt.ToString("HH:mm:ss");

        /// <summary>Wall-clock end time, or empty while the test is still running.</summary>
        public string EndText => EndedAt?.ToString("HH:mm:ss") ?? string.Empty;

        /// <summary>Elapsed test time once finished, or empty while running.</summary>
        public string DurationText => EndedAt is { } end
            ? ReconstructorFormatting.FormatTimeSpan(end - StartedAt)
            : string.Empty;

        // Stamp the end time the moment the row leaves "Testing" (Complete / Cancelled / Error all
        // flow through this setter, set by the tracker). The null guard makes it idempotent.
        partial void OnStatusChanged(string value)
        {
            if (value != "Testing" && EndedAt is null)
            {
                EndedAt = DateTime.Now;
            }
        }
    }
```

(`DateTime` is in scope via implicit `using System;`; `ReconstructorFormatting` via the file's existing `using ReScene.NET.ViewModels.Reconstruction;`.)

- [ ] **Step 4: Run the tests to verify they pass**

Run:
```bash
dotnet test E:/Projects/ReScene.NET/ReScene.NET.Tests/ReScene.NET.Tests.csproj \
  --filter "FullyQualifiedName~VersionEntryTests" \
  -p:BaseOutputPath=bin2/
```
Expected: **Passed! - Failed: 0, Passed: 4**.

- [ ] **Step 5: Verify the whole solution builds clean (non-incremental)**

Run:
```bash
dotnet build E:/Projects/ReScene.NET/ReScene.NET/ReScene.NET.csproj -p:BaseOutputPath=bin2/ --no-incremental
```
Expected: **Build succeeded. 0 Warning(s) 0 Error(s)**.

- [ ] **Step 6: Commit**

```bash
git add docs/superpowers/specs/2026-06-26-brute-force-progress-timing-columns-design.md \
        docs/superpowers/plans/2026-06-26-brute-force-progress-timing-columns.md \
        ReScene.NET/ViewModels/ReconstructorViewModel.cs \
        ReScene.NET.Tests/VersionEntryTests.cs
git commit -m "$(cat <<'EOF'
feat(ui): track per-version start/end/duration on the brute-force row

VersionEntry stamps StartedAt at construction and EndedAt when its Status leaves
"Testing" (idempotent), exposing StartText/EndText/DurationText for the progress
table. No tracker changes — it already drives the row lifecycle.

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>
EOF
)"
```

---

## Task 2: Start / End / Duration columns in the progress window

Add the three columns (bound to the Task 1 display strings) and widen the window's default so Arguments keeps room.

**Files:**
- Modify: `ReScene.NET/Views/BruteForceProgressWindow.xaml`

**Interfaces:**
- Consumes: `VersionEntry.StartText` / `EndText` / `DurationText` (from Task 1).

- [ ] **Step 1: Add the three columns**

In `ReScene.NET/Views/BruteForceProgressWindow.xaml`, replace the `DataGrid.Columns` block:

```xml
            <DataGrid.Columns>
                <DataGridTextColumn Header="Version" Binding="{Binding VersionName}" Width="140" />
                <DataGridTextColumn Header="Status" Binding="{Binding Status}" Width="90" />
                <DataGridTextColumn Header="Result" Binding="{Binding Result}" Width="80" />
                <DataGridTextColumn Header="Arguments" Binding="{Binding Arguments}" Width="*" />
            </DataGrid.Columns>
```

with:

```xml
            <DataGrid.Columns>
                <DataGridTextColumn Header="Version" Binding="{Binding VersionName}" Width="140" />
                <DataGridTextColumn Header="Status" Binding="{Binding Status}" Width="90" />
                <DataGridTextColumn Header="Result" Binding="{Binding Result}" Width="80" />
                <DataGridTextColumn Header="Start" Binding="{Binding StartText}" Width="70" />
                <DataGridTextColumn Header="End" Binding="{Binding EndText}" Width="70" />
                <DataGridTextColumn Header="Duration" Binding="{Binding DurationText}" Width="70" />
                <DataGridTextColumn Header="Arguments" Binding="{Binding Arguments}" Width="*" />
            </DataGrid.Columns>
```

- [ ] **Step 2: Widen the window default**

In the same file, in the `<Window ...>` opening tag, change:

```xml
        Width="750" Height="600"
```

to:

```xml
        Width="920" Height="600"
```

(Leave `MinWidth="600" MinHeight="450"` unchanged.)

- [ ] **Step 3: Verify the solution builds clean (non-incremental)**

Run:
```bash
dotnet build E:/Projects/ReScene.NET/ReScene.NET/ReScene.NET.csproj -p:BaseOutputPath=bin2/ --no-incremental
```
Expected: **Build succeeded. 0 Warning(s) 0 Error(s)** — confirms the XAML compiles and the new bindings (`StartText`/`EndText`/`DurationText`) resolve.

- [ ] **Step 4: Run the full app-test suite (no regression)**

Run:
```bash
dotnet test E:/Projects/ReScene.NET/ReScene.NET.Tests/ReScene.NET.Tests.csproj -p:BaseOutputPath=bin2/
```
Expected: **0 failures**.

- [ ] **Step 5: Commit**

```bash
git add ReScene.NET/Views/BruteForceProgressWindow.xaml
git commit -m "$(cat <<'EOF'
feat(ui): show Start/End/Duration columns in the brute-force progress window

Adds the three timing columns (bound to VersionEntry's StartText/EndText/
DurationText) and widens the window default so Arguments keeps room.

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>
EOF
)"
```

---

## Final verification (after all tasks)

- [ ] Clean non-incremental build of `ReScene.NET` with `-p:BaseOutputPath=bin2/`: **0 warnings, 0 errors**.
- [ ] Full `ReScene.NET.Tests` run with `-p:BaseOutputPath=bin2/`: **0 failures**.
- [ ] Delete scratch output: `find E:/Projects/ReScene.NET -type d -name bin2 -prune -exec rm -rf {} + 2>/dev/null`.
- [ ] Hand back to the user for a visual check: run a reconstruction and confirm each completed row shows Start, End, and a sensible Duration; the running row shows Start only (End/Duration blank); Arguments stays readable at the default width.

## Notes on cross-cutting concerns

- **YAGNI:** timing lives entirely on the row as a pure function of `StartedAt`/`EndedAt`; no tracker plumbing, no per-second updates, no new accessors. The existing `FormatTimeSpan` and the `HH:mm:ss` style are reused for consistency with the run-level timing.
- **WPF layout/visual state is not snapshot/Playwright-tested** in this repo, so the visual confirmation is manual (final step). The automated gate is the zero-warning build plus the `VersionEntry` unit tests.
