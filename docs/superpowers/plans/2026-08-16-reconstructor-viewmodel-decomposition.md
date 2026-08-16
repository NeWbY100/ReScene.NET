# ReconstructorViewModel Decomposition Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Split `ReconstructorViewModel.cs` (3,091 lines, 67 private methods) into focused collaborators without changing behaviour, and file its pinned binding surface into a `partial`.

**Architecture:** New units land in the existing `ReScene.App.Core/ViewModels/Reconstruction/` folder alongside the 32 collaborators already there. Bound-state writes travel back through narrow callbacks rather than moving the properties themselves — the 127 `[ObservableProperty]` declarations are the view's contract and cannot move. Two `partial` files (progress handlers, bindings) are filing, not decomposition, and are labelled as such.

**Tech Stack:** .NET 10 / C# 14, Avalonia 11.3.18, CommunityToolkit.Mvvm 8.4.2 source generators, xUnit 2.9.3 with hand-rolled doubles. `AnalysisLevel=latest-All` + `EnforceCodeStyleInBuild`; the build must stay at zero warnings.

**Spec:** [`docs/superpowers/specs/2026-08-15-hotspot-decomposition-design.md`](../specs/2026-08-15-hotspot-decomposition-design.md) § 3.

## Global Constraints

- **Behaviour-preserving.** Every moved body is transcribed, never retyped, and diffed back against the original modulo mechanical renames before the task is committed. See *Method*.
- **Zero warnings.** `dotnet build ReScene.Manager.slnx --no-incremental` must report 0 errors and 0 warnings after every task.
- **Both suites green**: `ReScene.App.Core.Tests` (780 at plan start) and `ReScene.Manager.Tests` (531). Counts change only where a task adds tests.
- **`CA2007` is suppressed in App.Core** (UI-thread sync context) — do not add `ConfigureAwait`. The opposite convention holds in ReScene.Lib; this plan does not touch it.
- **One top-level type per file** (`docs/coding-guidelines.md`). Nested types may stay nested.
- **Teeth checks are mandatory and must be honest**: confirm the suite is green *first*, then apply one mutation, confirm the new test fails, then revert by inverse substitution — never `git checkout` on a file holding uncommitted work.
- **No behaviour fixes.** Where this plan notes that current behaviour looks wrong, it stays wrong. Fixing it is a separate, separately-approved commit.
- **Codex reviews every task before it is committed.**

## Method: mechanical extraction, then diff

Retyping a 380-line method is how transcription errors get in. For every extraction task:

1. Extract each moving body from the original programmatically (brace-balanced, by signature).
2. Apply renames to **code lines only** — never comment lines. A rename inside prose silently corrupts it.
3. Assemble the new file, then **diff each moved body back against `git show <pre-task-commit>:...ReconstructorViewModel.cs`** with the same renames applied to the original. Anything that differs must be justified in the commit message.

Two failure modes this catches, both of which actually occurred during the Creator decomposition:

- A rename collapsing two distinct identifiers into one (a collection and a local list of the same name), which compiled and silently used the wrong one.
- A comment rewritten into nonsense by an over-greedy substitution.

**One it does NOT catch, and which also actually occurred: turning a live property read into a snapshot.** `Foo` → `inputs.Foo` is structurally a rename and semantically a different read time. Every task that introduces a parameter object must classify each member explicitly:

- **Stable** — a collaborator or service that never changes for the object's lifetime → constructor dependency.
- **Run-scoped snapshot** — read once at a defined point in the original → a value, and the plan/commit names the capture point.
- **Live** — the original genuinely re-reads it, so a later change is observable; or the holder can be replaced (a test seam such as `SetImportStateForTest`). → a `Func<>` accessor, invoked at every original read site. **Await placement does not decide this**: a value read once after an await is still a snapshot.

**Do not default either way. When unsure, stop and trace the original capture point.** Defaulting
to live is not behaviour-preserving: it re-reads a value the original captured once, and this
view-model has a concrete example in `_cleanupWorkFilesThisRun`, which is captured after the
version-scan await and then used for the rest of the run. "Read after an await" does not by itself
make something live.

A `Func<>` is fine as conservative *transport* for either kind, provided it is invoked the way the
original read it: **once, at the original capture point** for a snapshot, and **at every original
read site** for genuinely live state.

## Boundary enumeration is a step, not an assumption

The first version of this plan invented a seven-method `IRunSink` that did not match what the code
writes. Every extraction task therefore begins by **mechanically enumerating** the boundary and
deriving its interface from that. A method nothing calls does not go in the interface.

**Enumerate BY MEMBER, never by line range.** The second version of this plan enumerated the runner
as lines 1829-2245 and thereby missed `ReportSetSummary` at 2277 — which is where the runner's bound
writes actually are. Line ranges silently exclude members that sit outside them.

The enumeration has two halves, and the second is the one that is easy to skip:

1. **Per moved member**: locate it by signature, take its brace-balanced body, and list the private
   fields it touches, the statement-level assignments to PascalCase names (candidate bound writes),
   and the collaborator calls.
2. **Per moved declaration, across the WHOLE SOLUTION** — production *and* tests, not just this
   file. Keep a manifest of every moved field, method and nested type; search each
   (`rg -n --fixed-strings <name>`, plus semantic Find All References where available); and classify
   every hit as **definition**, **internal use**, **external production consumer**, **test seam**, or
   **documentation**. Subtract the hits inside the moved members. **Whatever remains is an external
   consumer that must become a named API** on the new collaborator — not something to discover
   mid-extraction. Doing this for Task 8 is what surfaces `_scanToken` at 260,
   `_pendingVersionSelection` at 1287 and `_lastScan` at 2040. **Re-run the same search after the
   extraction** to catch references left stale by the move.

Distinguish two kinds of survivor: a field that is genuinely moving (external references become the
new API) from a shared collaborator such as `_uiDispatcher` that merely appears in the moved code
(it stays on the view-model and is passed in). The count alone does not tell you which.

---

### Task 1: `ReconstructionLogBuffer` — the batched log

Cheapest and best-guarded first. ~60 lines with zero reads of other view-model state.

**Files:**
- Create: `ReScene.App.Core/ViewModels/Reconstruction/ReconstructionLogBuffer.cs`
- Modify: `ReScene.App.Core/ViewModels/ReconstructorViewModel.cs`

**Interfaces:**
- Consumes: `IUiDispatcher`, the view-model's `ObservableCollection<string> LogEntries` (by reference), `LogTarget`.
- Produces: `internal sealed class ReconstructionLogBuffer(IUiDispatcher uiDispatcher, ObservableCollection<string> logEntries)` with `Append(LogTarget target, string message)`, `Drain()`, `BeginNewGeneration()`, and nested `private readonly record struct PendingLogLine(string Line, int Generation)`.

Moves: `AppendLog` (2700), `ScheduleLogFlush` (2717), `FlushLogQueue` (2729), `DrainLogQueue` (2740), `BeginNewLogGeneration` (2759), `PendingLogLine` (2783), and fields `_logQueue`/`_logGeneration`/`_logFlushScheduled` (65-69) with their comment block.

**Three interlocking invariants move verbatim. Each has a named failure:**

| Invariant | Break it and |
|---|---|
| `BeginNewGeneration` clears → increments → resets the flag, **in that order** | a line enqueued between the clear and the increment survives into the next run's log |
| `Flush` releases the flag **before** draining | lines enqueued during a drain never schedule their own flush and are stranded until the next one |
| `Append` stamps `DateTime.Now` at **enqueue**, not at drain | every line in a batch gets the flush's timestamp instead of its own |

`Drain()` is also called **synchronously** from the run's `finally` (1808) as the final drain — it is not only a dispatcher callback.

- [x] **Step 1: Create the buffer**, transcribing the six members. `LogEntries` is held by reference; it is the bound collection.
- [x] **Step 2: Wire the view-model.** `AppendLog(t, m)` stays as a thin private forwarder to `_log.Append` — it has ~40 call sites and changing them is churn this task does not need. `_progress`'s `appendLog:` argument (120) keeps passing `AppendLog`. `BeginNewLogGenerationForTest` (2778) forwards to `_log.BeginNewGeneration()`.
- [x] **Step 3: Transcription diff** → all five bodies identical.
- [x] **Step 4: Build and both suites** → 0 warnings; 780 / 531.

Guards: `ReconstructorLoggingProgressTests.cs` (12), notably `ManyLogEvents_CoalesceIntoAtMostOneDispatch` (bounds dispatches at `<= 2` for 300 events, so losing the coalescing flag fails it) and the merged-order assertion at line 427.

- [x] **Step 5: Probe the three invariants** — make each inert in turn (increment before clear; release the flag after the drain; stamp the timestamp in `Drain`) and record which tests catch it. **Any invariant no test catches gets a characterization test in this task, before the commit.** Record all three probe results in the commit message regardless.
- [x] **Step 6: Codex review, then commit.**

---

### Task 2: `ReservedOutputTreeManager` — output-tree cleanup

~55 lines. Has direct tests already.

**Files:**
- Create: `ReScene.App.Core/ViewModels/Reconstruction/ReservedOutputTreeManager.cs`
- Modify: `ReScene.App.Core/ViewModels/ReconstructorViewModel.cs`

**`ClearReservedSubtrees` (2246) does THREE observable things on failure**: it logs, it calls `_fileDialog.ShowError("Error", $"Failed to clean output directory:\n{ex.Message}")`, and it returns `false`. A `(string outputPath, Action<string> log)` signature cannot transcribe that.

**Interfaces:**
- Produces: `internal static class ReservedOutputTreeManager` with
  - `ConfirmText(string outputPath)`,
  - `HasReconstructionArtifacts(string outputPath)`,
  - `ClearReservedSubtrees(string outputPath, Action<string> log, Action<string, string> showError)` returning `bool`.

The error callback takes (title, message) so the view-model forwarder passes `_fileDialog.ShowError` directly and the title stays where it is. **Log and dialog must fire in the original order.**

**The view-model keeps thin forwarders with their existing names and signatures** — `BeginnerWizardFactory.cs:211,218` calls two from production, and `ReconstructorOutputCleanupTests` calls them from tests.

- [x] **Step 1: Create the type**, transcribing the three methods.
- [x] **Step 2: Replace the bodies with forwarders**, every existing signature unchanged.
- [x] **Step 3: Transcription diff** → identical.
- [x] **Step 4: Build and both suites** → 0 warnings; counts unchanged.

Guards: `ReconstructorOutputCleanupTests.cs` (4). **If none of them covers the failure path** (log + `ShowError` + `false`), add that test in this task before the commit — the new signature exists specifically to preserve it.

- [x] **Step 5: Codex review, then commit.**

---

### Task 3: Characterization test — the `_verificationSnapshot` handoff

**Required before Task 4.**

**Files:**
- Modify: `ReScene.App.Core.Tests/ReconstructorRelocationRunTests.cs`, or create `ReconstructorStartValidationTests.cs` if the fixture does not fit.

`_verificationSnapshot` is assigned at the **parse point** (1625), *before* the later rejections for missing imported files, declined output cleanup, or cleanup failure. So **a rejected run leaves the newly parsed snapshot in place**, not the previous run's.

- [x] **Step 1: Write two tests**
  1. **Successful parse → read at 2022.** A run that parses a verification file and reaches the runner uses that snapshot's `VolumeNames`.
  2. **Rejected after parsing.** Drive a first run that parses to snapshot A; then a second run that parses to snapshot B and is rejected afterwards (declining the output-cleanup confirmation is the cheapest rejection). Assert a subsequent read sees **B**.

  Read the snapshot through an existing seam; if none exists, add one `internal …ForTest` in the established style rather than exposing the field.

- [x] **Step 2: Run** → **PASS**.
- [x] **Step 3: Prove teeth** — move `_verificationSnapshot = snapshot;` after the rejection branches; test 2 must fail.
- [x] **Step 4: Codex review, then commit.**

---

### Task 4: `ReconstructorStartValidator` — the validation gauntlet

~215 lines (1481-1697). Guarded by Task 3.

**Files:**
- Create: `ReScene.App.Core/ViewModels/Reconstruction/ReconstructorStartValidator.cs`
- Modify: `ReScene.App.Core/ViewModels/ReconstructorViewModel.cs`

- [x] **Step 1: Enumerate the boundary.** List every property AND field the gauntlet reads and mark each **live** or **run-scoped snapshot** per *Method*. **`_import` is live**: `SetImportStateForTest` (1825) replaces the holder, and the gauntlet reads it across an awaited confirmation. The gauntlet also reads properties *after awaited confirmations* — `VerificationPath` after the subdirectory warning, `OutputPath` again during cleanup — so those are **live** and become `Func<>` accessors invoked at the original sites. Modal dialogs do not make this unobservable: it is observable programmatically, and the tests drive the dialog through a fake.

**Interfaces:**
- Produces: `internal static class ReconstructorStartValidator` with nested `record Inputs` (accessors per step 1), nested `readonly record struct Result`, and `static Task<Result> ValidateAsync(Inputs inputs, Action<VerificationSnapshot> onParsed)`.

**No `CancellationToken` parameter.** Validation has no token today — `_cts` is created only at 1724, after validation — and the dialog calls do not accept one. Adding one would add behaviour; adding an unused one is a misleading API and an analyzer warning.

**The snapshot is delivered through `onParsed`, invoked at the parse point** — not returned in `Result`. Returning it only on success would change what Task 3 just pinned. "Retain snapshots only from accepted starts" is a separate behaviour fix; do not make it here.

**Two orderings are safety properties, not tidiness:**
1. Every reject decision runs **before** the destructive `ClearReservedSubtrees()`.
2. The verification file is parsed **before** output cleanup, because cleanup may delete it.

The two one-shot `Suppress…Confirm` flags are consumed into locals **before any early return**.

- [x] **Step 2: Create the validator**, transcribing the gauntlet.
- [x] **Step 3: Wire the start command** to call `ValidateAsync` and act on `Result`.
- [x] **Step 4: Transcription diff** → identical.
- [x] **Step 5: Build and both suites** → 0 warnings; counts unchanged from Task 3.
- [x] **Step 6: Re-run Task 3's teeth check** to confirm the pinned behaviour survived the move.
- [x] **Step 7: Codex review, then commit.**

---

### Task 5: Characterization test — busy-flag clear order and the staleness gate

**Required before Tasks 6 and 9b.** No test asserts either half today.

**Files:**
- Modify: `ReScene.App.Core.Tests/ReconstructorLoggingProgressTests.cs`

1. **`ElapsedText` is written (1783) before `IsRunning` clears (1784), and `IsRunning` clears before the guarded `IsCopying`/`IsVerifying` clears.** `ModalProgressWindowController` is constructed per busy flag and only ever sees its own flag, never `IsRunning`.
2. **A late queued progress `Post` is rejected by the staleness gate** (`if (!IsRunning) return;` at 2512 and 2555). Clearing `IsRunning` first is what makes those gates reject a late event that would otherwise re-open a closed dialog — so they must read the **live** flag, never a captured snapshot.

- [x] **Step 1: Write both tests.** For (1), record the order of property-changed notifications. For (2), queue a progress event that lands after the run's `finally` and assert the bound copy/verify state is untouched.

Use a **queued** dispatcher fake. If the only available fake posts inline, say so in the commit message and state plainly what the test therefore does *not* prove, rather than overclaiming.

- [x] **Step 2: Run** → **PASS**.
- [x] **Step 3: Prove teeth** — for (1) swap the clear order; for (2) change a gate to read a snapshot captured before the clear. Each must fail its own test.
- [x] **Step 4: Codex review, then commit.**

---

### Task 6: `ReconstructorViewModel.ProgressHandlers.cs` — mechanical partial split

~120 lines (2506-2627). Filing, no semantic change.

**Files:**
- Create: `ReScene.App.Core/ViewModels/ReconstructorViewModel.ProgressHandlers.cs`
- Modify: `ReScene.App.Core/ViewModels/ReconstructorViewModel.cs`

**The `Invoke`-vs-`Post` mix is load-bearing and must be unchanged**: `OnProgress` uses `Invoke`; the copy/verify handlers and the log flush use `Post`; `OnElapsedTimerTick` marshals not at all.

- [x] **Step 1: Move the four handlers verbatim** — no signature or body changes.
- [x] **Step 2: Transcription diff** → identical, comments included.
- [x] **Step 3: Build and both suites** → 0 warnings; counts unchanged.

Guard: **Task 5's queued-progress tests.** Note that after Task 1, `ManyLogEvents_CoalesceIntoAtMostOneDispatch` guards the *log buffer*, not these handlers — do not cite it here.

- [x] **Step 4: Codex review, then commit.**

---

### Task 7: Characterization test — `_suppressGroupSync`

**Required before Task 8.** Only `ManualLeafToggle_SyncsMajorBooleans` touches it today.

**Files:**
- Modify: `ReScene.App.Core.Tests/ReconstructorViewModelVersionsTests.cs`

There are **two independent suppression regions**, and a test covering one proves nothing about the other:
- `SetAllLeaves` (616-627), reached from the `SelectAllVersions`/`SelectNoVersions` commands;
- `RebuildVersionGroups` (708-728), reached from a programmatic scan/rebuild.

**Do not assert that the flag is cleared when the body throws.** Both regions use plain assignments with no `try`/`finally`, so an exception between `true` and `false` leaves suppression enabled. That is current behaviour; a test asserting otherwise would fail before any extraction. If exception safety is wanted, it is a separate approved commit.

- [x] **Step 1: Write one test per region** — a programmatic write must not trigger the group-sync path that a manual leaf toggle does.

**The `RebuildVersionGroups` region needs a reentrancy mechanism or its test cannot fail.** An
ordinary rebuild does not raise `SelectionChanged` at all: the old groups are detached, and the new
leaves are initialised *before* their group subscribes to them — so simply deleting
`_suppressGroupSync = true` there may have no observable effect, and a test written without this in
mind would be vacuous. Give it teeth with a reentrant `VersionGroups.CollectionChanged` observer that
mutates a newly added leaf *during* the rebuild, then assert the major synchronisation is deferred
until the rebuild finishes. This is expected to be reachable, because
`ObservableCollection.CollectionChanged` fires synchronously during `VersionGroups.Add`. **If it
turns out not to be: stop Task 7 and return for review — Task 8 stays blocked.** Do not fall back to
recording the region as unguarded; that would contradict this plan's mandatory teeth check and would
let the extraction proceed over a suppression flag nothing can verify.
- [x] **Step 2: Run** → **PASS**.
- [x] **Step 3: Prove teeth** — remove **each** `_suppressGroupSync = true;` independently; each removal must fail its own region's test.
- [x] **Step 4: Codex review, then commit.**

---

### Task 8: `VersionTreeCoordinator` — scan and reconcile (drag seam)

~190 lines (583-795). Guarded by Task 7. **Moved ahead of the runner**: the runner awaits `LastVersionScan` and builds settings from version-tree state, so doing this first gives the runner a stable boundary to sit on.

**Files:**
- Create: `ReScene.App.Core/ViewModels/Reconstruction/VersionTreeCoordinator.cs`
- Modify: `ReScene.App.Core/ViewModels/ReconstructorViewModel.cs`

- [x] **Step 1: Enumerate the boundary, inside AND outside the moving region.** The owned state is wider than the six `VersionN` bools: `VersionGroups` (by reference), `HasScannedVersions`, `ShowNoVersionsHint`, `WinRARPath`, `IUiDispatcher`, `LastVersionScan`, and the fields `_lastScan`, `_pendingVersionSelection`, `_scanToken`, `_suppressGroupSync`.

**Enumerating the region is not enough — search every reference to each moved field across the whole
file.** Doing that finds three consumers *outside* 583-795 that each need a named coordinator API,
and that would otherwise force a design decision mid-extraction:

| Consumer | What it does | Needs |
|---|---|---|
| `OnWinRARPathChanged` (249-260) | bumps `_scanToken` and starts the scan | one **atomic** invalidate-and-start method (see below) |
| SRR import (1287) | clears `_pendingVersionSelection`, then reconciles | a clear-pending-and-reconcile method |
| `BuildSharedSettingsAsync` (2040) | reads `_lastScan` | a read-only last-scan accessor |

**The `HasScannedVersions` gate around the last-scan read must stay at its original post-await read
site** (2038-2040) — the comment there explains that a rescan clears `HasScannedVersions`
synchronously but leaves `_lastScan` stale until the new scan lands.

**These must stay on the view-model as forwarders:**
- the three `[RelayCommand]` methods `RescanVersions`, `SelectAllVersions`, `SelectNoVersions` (607-614) — the generator binds them;
- `ApplyScanResult`, `LoadPendingVersionSelection`, `LastVersionScan` — `ReconstructorConfigMapper.cs:187` calls one from production;
- `SelectedLeafVersions` and `SelectedLeafFolders` (computed over `VersionGroups`) — decide keep-or-forward explicitly and say which in the commit.

**`OnWinRARPathChanged` must keep clearing `HasScannedVersions` and bumping `_scanToken` BEFORE
triggering the scan.** The six-line comment at 253-258 documents a real config-restore data-loss bug;
move it verbatim next to the code it explains. "Atomic" here means **one ordered coordinator
operation**, not locking — the point is that the three steps cannot be split across the seam:

```csharp
partial void OnWinRARPathChanged(string value)
{
    WinRARStatus = ReconstructorFieldGuidance.EvaluateWinRARPath(value);
    _versions.InvalidateAndStartScan();
}
```

with the coordinator doing `setHasScannedVersions(false); _scanToken++;` and then its existing
trigger logic.

**Do NOT pass `value` into the coordinator as the path.** `TriggerVersionScan` reads `WinRARPath`
*itself* at 639, at that later point — so the coordinator holds a live `Func<string>` accessor and
invokes it there. Passing `value` would convert a live read into a snapshot, which is this plan's
central risk.

**Keep BOTH `_scanToken++` increments.** There is a second one in `TriggerVersionScan`'s
invalid-path branch (644), with its own reason in the comment above it. Two increments on the
invalid path is current behaviour; do not "simplify" them into one.

- [x] **Step 2: Create the coordinator**, transcribing the scan/reconcile members.
- [x] **Step 3: Wire the view-model**, keeping every forwarder's exact shape.
- [x] **Step 4: Transcription diff** → identical.
- [x] **Step 5: Build and both suites** → 0 warnings; counts unchanged from Task 7.

Guards: `ReconstructorViewModelVersionsTests.cs` (11 + Task 7's), `VersionSelectionReconcilerTests.cs`, `ReconstructorConfigMapperTests.cs` (11).

- [x] **Step 6: Codex review, then commit.**

---

### Task 9a: `ReconstructionRunner` — contract and leaf helpers

The runner is ~380 lines and too large for one review gate, so it is split. This half establishes the boundary and moves the leaves.

**Files:**
- Create: `ReScene.App.Core/ViewModels/Reconstruction/ReconstructionRunner.cs`
- Modify: `ReScene.App.Core/ViewModels/ReconstructorViewModel.cs`

- [x] **Step 1: Enumerate the boundary mechanically and write it down.** A measured enumeration of the *loop* region (1829-2245) found it touches these private fields: `_bruteForceService`, `_cleanupWorkFilesThisRun`, `_fileMover`, `_import`, `_lastScan`, `_progress`, `_setStageLabel`, `_settingsService`, `_verificationSnapshot`; and reads these view-model properties live: `OutputPath`, `CompleteAllVolumes`, `LastVersionScan`, plus the option toggles consumed by `BuildSharedSettingsAsync` (`WinRARPath`, `ReleasePath`, `HasScannedVersions`, `SelectedLeafFolders`, `FileA`, `FileI`, `DeleteRARFiles`, `DeleteDuplicateCRCFiles`, `StopOnFirstMatch`, `RenameToReleaseNames`, `EnableHostOSPatching`, `UseOldVolumeNaming`). Re-derive this list against the code at implementation time rather than trusting it.

Classify each per *Method* and record the classification in the commit. Known constraints:

- **`_import` must NOT be captured in the constructor.** `SetImportStateForTest` (1825) replaces the holder, and many tests then call `RunArchiveSetsForTestAsync`. It is **live**.
- `OutputPath` and `CompleteAllVolumes` are read during relocation, *after* the brute-force awaits → **live**.
- `_cleanupWorkFilesThisRun` is captured only *after* awaiting the current version scan → a **run-scoped snapshot**, and the capture point is part of the contract.
- `LoadEmbeddedSfvBytes` reads `_import.SRRFilePath` **separately for each set** → live, per set.
- `_setStageLabel` stays a view-model field: `RunSingleSetAsync` writes it (1992) while the view-model-resident progress handler reads it live from the engine's callback thread (2685) — hence `volatile`. The runner sets it **through the sink**.

**`ReportSetSummary` sits at 2277, OUTSIDE that range** — the first enumeration missed it, which is
exactly the mistake this step exists to prevent. Enumerate it separately. It is where the runner's
direct bound-state writes actually live:

| Line | Writes |
|---|---|
| 2303 | `ProgressPercent` |
| 2304 | `ProgressPercentText` |
| 2307 | `TestCountText` |
| 2323 | `ProgressMessage` |
| 2324 | `PhaseDescription` |
| 2325 | `LastRunSucceeded` |

It also reads `VersionEntries` (2320) and `_progress.LastOperationSize` (2305, 2307), and logs.

**Derive the sink from exactly that.** The first draft invented seven methods, some of which nothing
calls; the loop region's own statement-level assignments turned out to be `BruteForceOptions`
initializer members inside `BuildSharedSettingsAsync` (2028-2065), not bound writes. **A method
nothing calls does not go in the interface.**

Decide explicitly, and record in the commit: which `_progress` operations (`SetActiveSet` at 1919,
`CompleteActiveVersion` at 1900/1935/1947/1959, `LastOperationSize`) stay direct calls on the tracker
rather than becoming sink methods.

**`BuildSharedSettingsAsync` STAYS on the view-model** and is supplied to the runner as a callback.
It reads a dozen option toggles plus `_lastScan` and `_verificationSnapshot`; moving it would explode
all of that into the runner's contract for no benefit.

- [x] **Step 2: Move the leaf helpers only** — `LoadEmbeddedSfvBytes`, `EmbeddedSfvMatchesSet` (2101), `RelocateVerifiedOutput`, `CleanupWorkRoot`. Keep any existing test-facing static forwarder on the view-model.

`SetOutcome` and `ReportSetSummary` move in **9b** instead: they are aggregate run-loop members, not
leaves, and moving the outcome record here would force 9a to expose a private type across the seam
temporarily for no gain.
- [x] **Step 3: Transcription diff** → identical.
- [x] **Step 4: Build and both suites** → 0 warnings; counts unchanged from Task 8.
- [x] **Step 5: Codex review, then commit.**

---

### Task 9b: `ReconstructionRunner` — the run loop

**Files:**
- Modify: `ReScene.App.Core/ViewModels/Reconstruction/ReconstructionRunner.cs`, `ReScene.App.Core/ViewModels/ReconstructorViewModel.cs`

Moves `RunArchiveSetsAsync`, `RunSingleSetAsync`, the `SetOutcome` record, and `ReportSetSummary`
— the two loops and the aggregate reporting that consumes their outcomes.

**`ExecuteReconstructionAsync`'s outer `try`/`finally` STAYS on the view-model.** Its `finally` does three things a collaborator cannot own:
- it writes `ElapsedText` (1783) **before** clearing `IsRunning` (1784);
- it disposes and nulls `_cts` (1803), which `Stop` (2341) reads;
- it calls the log drain (1808) synchronously.

**The `finally`'s guarded clears stay ordered**: `IsRunning = false` first, then the guarded `IsCopying`/`IsVerifying` clears — for the reason Task 5 pinned.

**`_progress.CompleteActiveVersion` stays paired with its own `outcomes.Add` at all four sites.**

**`RunArchiveSetsForTestAsync` (1827) must survive** as a view-model forwarder — many tests call it.

- [x] **Step 1: Move the two methods**; bound-state writes become sink calls per 9a's contract.
- [x] **Step 2: Wire `ExecuteReconstructionAsync`** to call the runner inside its existing, unchanged `try`/`finally`.
- [x] **Step 3: Transcription diff** → identical modulo the classified renames.
- [x] **Step 4: Build and both suites** → 0 warnings; counts unchanged.
- [x] **Step 5: Probe every sink method AND every live accessor**, one at a time. For a sink method, make it inert. For a live accessor, **replace it with a snapshot captured at run start** — that is the mutation that matches this plan's central risk, and an accessor whose snapshot mutation survives the suite is an unguarded live read.

**Every non-dead sink write and every surviving live accessor must end this task either covered by a
test or removed from the interface.** "Recorded as unguarded" is not sufficient here: this is the
task most able to break a run silently.
- [x] **Step 6: Codex review, then commit.**

---

### Task 10: Characterization tests — SRR import decisions and log order

**Required before Task 11.** No direct test exists for any of this.

**Files:**
- Modify: `ReScene.App.Core.Tests/ReconstructorViewModelVersionsTests.cs`, and a logging test file for the ordering test.

- [x] **Step 1: Write the decision tests.** Use `[Theory]` over the **material arms**, not one representative case:
  - `SetRARVersionsFromSRR` (method at 2793): the no-value early return, plus each distinct `unpVer` branch and what it selects — and that non-matching leaves are left alone.
  - `ApplyVolumeSize` (method at 2982, called at 1256): the nonpositive early return, plus all seven unit-selection arms. It selects by exact divisibility, not by rounding — do not write the test as if it rounds.
- [x] **Step 2: Write the import log-order test.** `ApplySwitchDiff`'s doc comment (2876-2881) states that it emits "the same log lines in the same order as the original inline mapping" — that is **documented, not asserted**, and a repository search finds no view-model-level assertion for these lines. Assert the full ordered sequence of import log lines for a representative SRR.
- [x] **Step 3: Run** → **PASS**.
- [x] **Step 4: Prove teeth** — perturb the version mapping by one; perturb the volume-size conversion by one unit; **swap two import log lines**. Each must fail its own test.
- [x] **Step 5: Codex review, then commit.**

---

### Task 11: `SRRImportApplier` — import decisions (drag seam)

Extracts the **decisions** only. Guarded by Task 10.

**Files:**
- Create: `ReScene.App.Core/ViewModels/Reconstruction/SRRImportApplier.cs`
- Modify: `ReScene.App.Core/ViewModels/ReconstructorViewModel.cs`

Extracts the decisions from `SetRARVersionsFromSRR`, `SetTimestampFlags`, `ApplySwitchDiff` and `ApplyVolumeSize`. **The ~170 lines of `SwitchMD… = true` assignment tables stay on the view-model** — they write bound properties and have no independent logic.

**`ImportSRRAsync` (1213) applies these in a deliberately interleaved order**: switch diff and its logs → three timestamp groups and their logs → other option resets → volume decision and log → volume naming → RAR-version selection and log → version-tree reconciliation.

**Computing one expanded diff and applying it once at the end would reorder property notifications and log lines even while producing identical final values.** Therefore: **each decision is applied at its original call site.** If an ordered operation list is used instead, the plan for it must be written out explicitly and reviewed before implementation — it is not an implementation detail.

- [x] **Step 1: Extract the decisions**, leaving each application at its original site.
- [x] **Step 2: Transcription diff** on the decision logic → identical.
- [x] **Step 3: Build and both suites** → 0 warnings; counts unchanged from Task 10.
- [x] **Step 4: Confirm Task 10's log-order test still passes** and re-run its teeth check.
- [x] **Step 5: Codex review, then commit.**

---

### Task 12: `ReconstructorViewModel.Bindings.cs` — file the pinned surface

**Filing, not decomposition — the commit message must say so.** It is the only mechanism that gets the primary file below its ~800-line generated-surface floor.

**Files:**
- Create: `ReScene.App.Core/ViewModels/ReconstructorViewModel.Bindings.cs`
- Modify: `ReScene.App.Core/ViewModels/ReconstructorViewModel.cs`

Moves the `[ObservableProperty]` declarations, the toggle regions (797-963) with their `On<X>Changed` hooks, and the nested `VersionEntry` bound row.

- [x] **Step 1: Feasibility probe — move `WinRARPath` only.** It is the right probe because that one move exercises every mechanism at once: the `[ObservableProperty]` in the new partial, `OnWinRARPathChanged` implemented in the old file, a `[NotifyCanExecuteChangedFor]` whose command is generated from a method in the old file, and `NotifyPropertyChangedFor` across the partial. Build; run the path/command tests. **If it fails, stop and report** rather than working around it.

There is no meaningful reverse case: this task does not move the command methods.

- [x] **Step 2: Move the rest** if and only if step 1 passed.
- [x] **Step 3: Build and both suites** → 0 warnings; counts unchanged.
- [x] **Step 4: Measure** — `wc -l` on both files. **Report the real number; do not force further extraction to hit a target.** The Creator plan projected ~800 and landed at 1,035; stopping at a coherent seam was the right call there and is here.
- [x] **Step 5: Codex review, then commit.**

---

## After this plan

1. Full solution verification: `dotnet build ReScene.Manager.slnx -c Release --no-incremental && dotnet test ReScene.Manager.slnx -c Release --no-build` — 0 warnings, all five suites green.
2. `CHANGELOG.md` `[Unreleased] / ### Changed` entry, noting the restructuring, that behaviour is unchanged, and any coverage gaps the seam probes revealed and closed.
3. Append an **Outcome** section here recording the final line counts and anything the plan did not anticipate.
4. Push (this repository only — the submodule is untouched by this plan).

## Revision note

The first version of this plan was rejected in review for six defects, all of which are fixed above and are worth stating because they are the plan's main risks:

1. **Task 9's `IRunSink` was invented, not derived** — seven methods, some of which nothing calls, and it omitted `_import`, `_settingsService`, `_cleanupWorkFilesThisRun`, `_progress`, live `OutputPath`/`CompleteAllVolumes` and more. Boundary enumeration is now an explicit first step of every extraction task.
2. **Task 4 proposed a `CancellationToken` the original does not have**, and left `Inputs` unclassified where the gauntlet reads properties after awaited confirmations.
3. **Task 7 asserted exception-safe clearing of `_suppressGroupSync`** — behaviour the code does not have, so the test would have failed before any extraction.
4. **Task 2's API dropped the `ShowError` dialog** on the cleanup-failure path.
5. **Task 11's "single `Apply(diff)`" would have reordered notifications and log lines**, and claimed a log-order assertion that does not exist.
6. **Several line numbers were wrong**: `ElapsedText`/`IsRunning` are 1783/1784 not 1781; the second staleness gate is 2555 not 2556; `_setStageLabel` is read at 2685 not 2683; `SetRARVersionsFromSRR` begins at 2793; `ApplyVolumeSize` is called at 1256 but defined at 2982; and 2876-2881 is documentation, not an assertion.

The runner was also split in two, and moved after the version coordinator.


---

## Outcome

All twelve tasks implemented and reviewed by Codex; where a commit was made before its review,
it was amended afterwards.
`ReconstructorViewModel` went from 3,091 lines in one file to 1,840 plus two partials (321 + 152),
with six new collaborators in `ViewModels/Reconstruction/`. Release-configuration verification across
the solution: **4,513 tests pass, 0 warnings.** App.Core's suite grew from 780 to 835.

The plan projected ~1,100 for the primary file. It landed at 1,840, and that is the honest stopping
point: 64 observable-property declarations remain, scattered among the behaviour they belong beside
rather than sitting in coherent blocks, and the plan's own instruction was to report the real number
rather than force further filing.

### What the boundary enumeration was worth

It was mandated after the first draft invented an `IRunSink` that did not match the code. It then
earned its place repeatedly:

- **Every extraction had external consumers** that would otherwise have surfaced mid-move: the
  version tree's `_scanToken`, `_pendingVersionSelection` and `_lastScan` each had one, and each
  became a named API rather than a surprise.
- **Enumerating by line range instead of by member missed `ReportSetSummary`**, which sits outside
  the run loop's range and is where the runner's bound writes actually live. Enumerating by member
  fixed it.

### The seams the suite did not guard

Probing each seam by making it inert found **eleven categories** of gap that 780 passing tests did
not cover - some rows below aggregate several mutations, such as two handlers or four sink writes.
Every one is now closed:

| Seam | Failures before | After |
|---|---|---|
| Log buffer: generation clear/increment order | 0 | 1 |
| Log buffer: flush-flag release before drain | 0 | 1 |
| Output cleanup: the error dialog on failure | 0 | 1 |
| `OnProgress` marshalling with `Invoke` | 0 | 1 |
| Copy/CRC marshalling with `Post` | 0 | 2 |
| Four of the seven run-completion sink writes | 0 | 2 |
| `OutputPath` read live during relocation | 0 | 1 |
| `CompleteAllVolumes` read live during relocation | 0 | 1 |
| Both `_suppressGroupSync` regions | 0 | 1 and 2 |
| The invalid-path branch's own `_scanToken++` | 0 | 1 |
| The SRR import's decision ORDER | 0 | 1 |

### Things that were wrong and had to be corrected

Recorded because they are the plan's real risks, not incidental:

1. **A "behaviour-preserving" rewrite that was not.** Replacing the six interleaved
   read-then-write pairs in `SyncMajorsFromTree` with a compute-all-then-write-all set produced
   identical values in identical order while losing the fact that a subscriber can mutate a later
   major from an earlier one's notification. The projection went back to the view-model, byte-identical.
2. **A conclusion drawn from an incomplete measurement.** `SetAllLeaves`'s suppression looked like a
   pure optimisation because the `VersionN` write sequence is identical with and without it. What
   differs is the interleaving with the tree's own `SelectionChanged`.
3. **Three tests that passed for the wrong reason** and had to be rewritten: the stale-scan test drove
   the path setter, where an earlier bump already invalidated the token; the mid-run output-path test
   asserted a file's existence rather than the mover's destination; and a decision-order mutation
   swapped only the first line of two multi-line calls, changing arguments rather than order.
4. **A false rationale in a comment.** `Invoke` was described as running the handler on the engine's
   callback thread. Both `Invoke` and `Post` marshal onto the UI thread; what differs is whether the
   engine callback waits.
5. **A verification script that reported three differences that did not exist**, because `^\s*`
   matched newlines and anchored the match on a preceding blank line.
6. **A build-warning check that read a cached second build**, reporting 0 while the real build had 26.
   Counting from a single `tee`'d invocation fixed it.
7. **An extraction that erased a subscriber's edit.** After the blanket clear, each branch of the
   version selection writes only the flags it owns - the 7.x branch never touches `Version3`, and no
   branch writes `Version7 = false`. Returning six plain bools and assigning them all wrote flags the
   original left alone, which is observable because `PropertyChanged` is synchronous. The selection
   carries nullable flags, where null means "do not write".
8. **A task commit that did not build in isolation.** Task 12's feasibility probe deleted a property
   declaration inside Task 11's commit while its replacement arrived in Task 12's. History was
   rewritten so each task commit stands alone, as the plan's per-task build gate requires.

### Deviations from the plan, all deliberate

- The start validator is a sealed instance returning `bool`, not a static returning a result struct.
- `SetStageLabel` was promoted from a nested private record to its own file so both sides of the
  runner seam can name it.
- Task 11's scope shrank: `SRRSwitchMapper` already owned the switch decision and `SetTimestampFlags`
  was already pure, so only two decisions actually moved.
- `SetRARVersionsFromSRR`'s inputs are synthesised with `UnsafeAccessor` rather than by widening
  ReScene.Lib's `InternalsVisibleTo` across the submodule boundary.
