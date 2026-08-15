# Hotspot decomposition — design

## Problem

Three units carry most of this codebase's edit risk:

| Unit | Size | Shape |
|---|---|---|
| `Manager.TryProcessCommandLinesAsync` (`ReScene.Lib/ReScene/Core/Manager.cs:860-1515`) | 656 lines, nesting to depth 7 | One method, 15 exit points, twin near-duplicate verification paths |
| `CreatorViewModel.cs` | 2,295 lines, 57 private methods | Behavior-heavy: only 20 `[ObservableProperty]` |
| `ReconstructorViewModel.cs` | 3,091 lines, 67 private methods | Binding-heavy: 122 `[ObservableProperty]`, 14 commands (+5 more properties on its nested row type) |

They are not one problem. The lib method is a *correctness* hazard; `CreatorViewModel` is a
*volume* problem with clean seams; `ReconstructorViewModel` is roughly half generated-binding
surface that cannot move at all.

The lib method's hazard is concrete and documented in its own source. Its assembly and legacy
per-volume verification blocks (1329-1349 and 1399-1438) are kept in sync by two comments —
"the gate is EXACTLY the legacy block's own below" (1323) — with no shared helper and no test
that fails if they drift. Their shared *evaluation core* — the gate expression, the
`.Select(v => HashCalculator.Calculate(HashType.CRC32, v))` projection, the `Evaluate` call, the
`CountMismatch` detail ternary, and the `LogTarget.Phase2` message — is textually identical apart
from one variable name. The blocks around that core differ materially (volume source, patching,
retention), which is why only the core is unified below. The compiler even forced a naming
workaround
(`assemblyExpectedInOrder` vs `expectedInOrder`, CS0136) that the author documented at 1106-1108
and 1325-1327.

Three defects surfaced while mapping, independent of any refactor:

1. **Lines 1400-1438 are executed by no test.** Reaching the legacy CAV verification needs
   `_useAssembly == false` **and** `CompleteAllVolumes` **and** a non-empty `ExpectedVolumeCrcs`.
   `AssemblyTestHost.Options` (`AssemblyTestHost.cs:95-102`) only populates `ExpectedVolumeCrcs`
   from a fixture, and every fixture supplies `SRRFilePath`, which engages assembly instead.
2. **Retention asymmetry.** On a volume mismatch the assembly path calls `ApplyMismatchRetention`,
   honouring `DeleteRARFiles` **or** (`duplicate && DeleteDuplicateCRCFiles`). The legacy path
   calls `DeleteRARFileAndVolumes` gated on `DeleteRARFiles` alone — its `isDuplicateHash` (1228)
   is scoped to a block that closed at 1254. Same situation, different disk retention.
3. **Reporting asymmetry.** On incomplete finalization the assembly path fires
   `FireAssemblyErrorRow` (a `CombinationFailed` progress row the UI renders as an error); the
   legacy path writes `_logger.Warning` (1454) and fires no event. Same failure, different UI.

## Constraints that bound every option

These are framework and language facts, not preferences. They cap how small each file can get.

- **`[ObservableProperty]`, `[RelayCommand]`, `[NotifyPropertyChangedFor]`,
  `[NotifyCanExecuteChangedFor]`, and `partial void On<X>Changed` are generated into the class the
  view binds.** They cannot move to a collaborator. `ReconstructorViewModel` itself pins 122
  properties + 14 commands + 9 hooks + 20 notify attributes, with a further 5 properties, 1 hook
  and 2 attributes on its nested `VersionEntry` row type; `CreatorViewModel` pins 20 + 16 + 3 + 4.
  A `[RelayCommand(CanExecute = nameof(X))]` additionally pins `X` to the same type.
  They may, however, be split across **partial files of the same class** — measured, not assumed:
  a probe confirmed a `partial void On<X>Changed` in one file fires for a property declared in
  another, and a `[NotifyCanExecuteChangedFor]` in one file correctly targets a `[RelayCommand]`
  declared in another. The generators bind against the complete partial type symbol.
- **A `catch` body cannot be extracted** without `ref` locals (`combinationCounted` is written in
  `try` at 989 and read in `catch` at 1492), and `throw;` must stay lexically inside its `catch` to
  preserve rethrow semantics.
- **`ReconstructorConfigMapper`** (`ReconstructorConfigMapper.cs:14,102`) reads/writes ~70
  `ReconstructorViewModel` properties by name and calls `LoadPendingVersionSelection` (187). Any
  property leaving the VM breaks it and its 21 KB test file.
- **16 `internal …ForTest` seams** on `ReconstructorViewModel` and `LastFolderScan` on
  `CreatorViewModel` are called by tests and, in two cases, by production code. Every one survives
  as at minimum a forwarder.
- **`partial` across files is the sanctioned exception** to one-type-per-file
  (`docs/coding-guidelines.md:20-21`), and is the only tool that reduces pinned surface per file.

## Fix

House pattern throughout (`ViewModels/Reconstruction/`, 31 files): `internal` types; `internal
static class` when stateless, `internal sealed class` with a primary constructor when it holds a
snapshot; nested `internal sealed record Inputs` and `internal readonly record struct` outcomes;
`Action<string> log` for diagnostics, never a back-reference to the caller; bound
`ObservableCollection<T>` passed in and mutated in place with the view-model keeping all UI-thread
marshalling (`ReconstructionProgressTracker.cs:10-14`).

### 1. `ReScene.Lib` — `TryProcessCommandLinesAsync` (656 → ~180 lines)

Applied in this order; each step ships independently green.

1. **`CandidateContext`** — a `sealed record` bundling the per-candidate identity and argument
   set (`rarExeFilePath`, `rarVersionDirectoryName`, `rarOutputDir`, `rarFilePath`,
   `candidateSlug`, `assemblyDir`, `commandLineArguments`, `filteredArguments`, `displayArguments`,
   `finalArguments`, `executedArguments`, `inputTail`, `inputFileArguments`, progress totals).
   Composition only — the `File.WriteAllText` at 933-936 and the RAR6 skip at 906-915 stay at the
   call site so their documented ordering is untouched. Removes the duplicate `assemblyDir`
   computation (1113 vs 1287) and collapses `FireAssemblyErrorRow`'s 11 parameters.
2. **`NewRow(in CandidateContext, int progress, bool failed = false)`** — one factory for the five
   near-identical `BruteForceProgressEventArgs` initializers (951, 962, 1042, 1080, 1497) plus the
   one inside `FireAssemblyErrorRow`. The increment-vs-fire ordering is preserved exactly: 950
   increments *before* firing, 962 fires *before* incrementing, 1040-1042 increments then fires.
3. **`VerifyVolumeSet(Func<IReadOnlyList<string>> volumesFactory, BruteForceOptions, string label)`**
   — the one unification. Builds `expectedInOrder`, applies the gate, and **only if the gate is on**
   invokes `volumesFactory`, hashes CRC32, evaluates, and emits the single log line; returns
   `AllMatch`, or `true` when the gate is off. Both call sites shrink to ~4 lines. This dissolves
   the comment-enforced sync hazard *and* the `assemblyExpectedInOrder` naming workaround.
   **The factory must be lazy, not an eager `IReadOnlyList<string>` parameter.** The legacy block
   discovers volumes (`FindCreatedRARFile` + `GetAllVolumeFiles`) and re-patches them (1402-1411)
   *inside* the gate; an eager parameter would force that enumeration, patching, and its I/O
   failure modes to run even when `CompleteAllVolumes` is false or the CRC map is empty — a
   behavior change, and a write where there is currently none. With the lazy factory the shared
   core is equivalent by construction, because the two blocks' cores are already textually
   identical apart from one variable name.
4. **`TryLegacyGateAsync`** (1215-1254) → `(bool Matched, string Hash)`.
5. **`TryFinalizeLegacyWin`** (1396-1459) → `CommittedMatch?`. Synchronous; touches no producer
   state (the producer was joined at 1275).
6. **`FinalizeAssemblyWinAsync`** (1278-1394) → `CommittedMatch?`. Same reason.
7. **`TryQuickGateAsync`** (1111-1214) → `(GateOutcome, SRRReconstructionResult? Quick, bool
   IsDuplicate, string? Hash)`. The hoisted `quick`/`isDuplicateAssemblyHash` (1109-1110) become
   return values — exactly what the author's comment wishes for. `continue` becomes
   `return NextCandidate`.
8. **`CandidateProducer`** — last, because it is the only seam touching the producer-observation
   invariant. A `sealed class : IDisposable` holding `Task<int>?`, `CancellationTokenSource?`, and
   a mutable `int? CompletedExitCode` (written from three phases: 1037, 1067, 1135). It needs
   **four** operations, not two — the method observes the task in two further ways beyond the
   documented pair:
   - `ObserveQuietlyAsync()` and `JoinForWinAsync()` — the two modes documented at 661-673.
   - `AwaitLaunchOrSecondVolumeAsync()` — the `Task.WhenAny` + immediate `IsFaulted` rethrow at
     1012-1027, which must keep surfacing a faulted producer to the generic catch.
   - `SampleRetryEligibility()` — captures `is { IsCompleted: false }` **before**
     `AssembleCandidateAsync` runs (1116-1124). Sampling it afterwards is explicitly wrong: it
     loses the incomplete-snapshot retry, which is the real race the check exists to catch.

   Disposed **only** in the existing `finally` (1510); disposing anywhere else makes the later
   `Cancel()` inside `ObserveProducerQuietlyAsync` (687, outside its own `try`) throw
   `ObjectDisposedException`.

**Finalization is deliberately NOT unified.** Nine externally observable differences (volume
source, patching, retention, finalizer, log/finalize ordering, match-log content, incomplete
disposition, success cleanup, and the CAV-only re-assembly step) would require a strategy object
with five delegates and two flags — more surface than the ~40 lines removed, and every parameter
turns "behavior-preserving" from a mechanical fact into a claim.

**Blocked:** the two `catch` bodies (1461-1473, 1474-1507). At most their progress row routes
through step 2.

### 2. `CreatorViewModel` (2,295 → ~800 lines)

New folder `ReScene.App.Core/ViewModels/Creation/`.

1. **`CreatorArtifactNaming`** (`internal static`) — the 12 already-`private static` pure helpers:
   `RootRelativeName`, `FolderRelativeName`, `FolderRelativeStem`, `GeneratedStoredName`,
   `IsFilesystemRoot`, `IsRootError`, `DiscoverRARVolumes`, `IsUnderProofDirectory`,
   `HasMatchingSfv`, `FindSampleArtifactSpliceIndex`, `FindSubtitleArtifactSpliceIndex`,
   `IsRarBackedVobSample`. ~160 lines, zero state. `IsRootError`'s `StartsWith("Cannot scan '")`
   prefix heuristic moves verbatim, with its fragility noted in the doc comment.
2. **`FolderScanSession`** (`internal sealed`, `ComparePane`-shaped) — owns `_scanGeneration` and
   `_scanCts` behind `Begin()`, `CancelInFlight()`, `TryClaim(generation, cts)`. ~60 lines. The
   eager `CancellationToken token = cts.Token` capture at 1132 moves **verbatim**: `cts.Token`'s
   getter throws `ObjectDisposedException` after disposal, which is the bug
   `RapidInputSwitching_WithoutAwaiting_NeverThrows` exists to catch.
3. **`ArtifactFileGenerator`** (`internal sealed`, primary ctor over the two creation services +
   `Action<string> log`) — `GenerateSRSFileAsync`, `GenerateNestedSRRFileAsync`,
   `GenerateAndRecordAsync<T>`, `GenerateNestedSubtitleSrrsAsync`, `BuildNestedSubtitleStoredFiles`.
   ~270 lines. This is the natural home for the one cross-mode call
   (`CreateVobsubSRRsAsync:1978` → `GenerateNestedSubtitleSrrsAsync:1782`) that currently violates
   the file's own section boundaries. Extracted **before** the stager so the stager comes out clean.
   The three independent `nestedOptions` constructions (1615, 1833, 2044) may share one factory but
   must keep `AllowCompressed = true, ComputeOSOHashes = false` and must never forward the outer
   `options`.
4. **`CreatorArtifactStager`** (`internal sealed`, primary ctor) — all of folder-mode staging
   (1374-1922), ~549 lines. **Snapshot timing must stay phase-local.** Today samples are
   snapshotted when sample generation begins (1527), subtitles only *after* all sample generation
   completes (1696), and `CreateVobsubSRR` is read after sample generation (1411). Building one
   up-front `StagingInputs` carrying both snapshots and both toggles would change behavior if
   those public collections or properties change across an await. So the stager takes the live
   `ExtraSampleFiles`/`ExtraSubtitleSfvFiles` and reads each toggle at its current phase boundary,
   exactly where the VM does now; only the immutable `ReleaseRoot`/`AppName` are passed up front.
   `releaseRoot` becomes a **non-nullable parameter**, removing the four
   `_releaseRoot!` null-forgiving reads (1540, 1557, 1722, 1774) — the one place this refactor
   strictly improves safety. The five-step ordering in `StageFolderArtifactsAsync` (1385-1419) and
   the strict two-pass structure of `GenerateSubtitleArtifactsAsync` (1696-1780) are byte-exact
   parity requirements and move unchanged. Keeps its concrete coupling to
   `ReleaseScanner.ResolveDedupKey`/`ApplyProofBeforeSfvReorder` (static, not on `IReleaseScanner`)
   — do not hide it behind the interface.
5. **`CreatorFieldGuidance`** (`internal static`) — two pure functions: one returning the
   `FieldStatus` for `UpdateInputStatus`, one returning the **string** `ActionHint`
   (`UpdateActionHint` computes text, not a `FieldStatus`). `ReconstructorFieldGuidance` precedent.
   The auto-output-path arbitration stays: it round-trips through `OnOutputPathChanged:358`, a
   generated hook.
6. **`FolderScanController`** (drag seam, in scope) — the scan lifecycle (1020-1370), ~351 lines.
   Takes the VM's `ObservableCollection`s by reference plus **eight** setter delegates —
   `setIsScanning`, `setInputStatus`, `setOutputStatus`, `trySetAutoOutputPath`,
   `notifyCanExecuteChanged`, `notifyFolderModeChanged`, plus `clearSelections` (
   `ClearFolderScanResults:1072` also clears `SelectedStoredFile`, `SelectedExtraSample` and
   `SelectedExtraSubtitle`) and `updateActionHint` (every scan outcome calls it, **including
   successful completion** at 1300) — and `appendLog`, per the `ReconstructionProgressTracker`
   template. It must additionally expose explicit `Reset()` and `ExitFolderMode()` operations:
   `Reset:237` and `OnInputPathChanged:313` both live *outside* the extracted 1020-1370 range yet
   mutate the generation counter, the CTS, the mode flags, the release root and the collections, so
   once those fields move inside, both callers need a controller API rather than field access.
   `_isFolderMode`/`_isMusicOnlyFolder`/
   `_folderScanInvalid`/`_releaseRoot` move in and are re-exposed as read-only properties the VM
   forwards, because `CanCreateSRR:726-731` and `CreateSRRAsync:775-797` read them. **All four
   manual `CreateSRRCommand.NotifyCanExecuteChanged()` calls (1107, 1204, 1233, 1301) must keep
   firing in the same order relative to the `InputStatus` assignment beside them** — those gates
   have no `[NotifyCanExecuteChangedFor]` backing them. `ExitFolderMode` must keep clearing
   `IsScanning` **synchronously**.
7. **`FileModeCreationPipeline`** (drag seam, in scope) — the file-mode branch (821-914) plus
   `AutoScanReleaseFiles`, `CreateSRSForSamplesAsync`, `CreateVobsubSRRsAsync`, `StoreFixRARFile`,
   ~215 lines. **`StoredFiles` must continue to be appended to incrementally during the run**
   (1941, 1980, 2129) while `IsCreating == true`: generation order is storage order, live, in the
   bound DataGrid. Batching the appends would keep the produced SRR identical while changing
   observable UI ordering — and `CreateSRR_PassesStoredFilesToLibInCollectionOrder` would still
   pass. The appends stay on the awaiting continuation, never behind `_uiDispatcher.Post`.

**Blocked:** the list-editing commands (428-590, 687-718). Nine are `[RelayCommand]` methods whose
generated `…Command` properties the XAML binds by name; only their bodies could delegate, which
buys nothing.

The constructor's `_sRRService.Progress += OnProgress` (line 40) does not move. The two instances
are wired with *different* service instances by design (`MainWindowViewModel.cs:199` vs `:216`) so
progress never crosses, and the subscription is never removed.

### 3. `ReconstructorViewModel` (3,091 → ~1,100 + a bindings partial)

New collaborators in the existing `ViewModels/Reconstruction/` folder.

1. **`ReconstructionLogBuffer`** (`internal sealed`) — `AppendLog`, `ScheduleLogFlush`,
   `FlushLogQueue`, `DrainLogQueue`, `BeginNewLogGeneration`, `PendingLogLine`, and fields 65-69.
   ~60 lines, zero reads of other VM state. Three interlocking invariants move verbatim:
   `BeginNewLogGeneration` clears → increments → resets the flag *in that order*; `FlushLogQueue`
   releases the flag **before** draining; `DrainLogQueue` is also called **synchronously** from the
   run's `finally` (1808). `AppendLog` stamps `DateTime.Now` at **enqueue**, not at drain.
   `_progress`'s `appendLog:` argument (line 120) becomes `_logBuffer.Append`.
2. **`ReservedOutputTreeManager`** (`internal static`) — `OutputCleanupConfirmText`,
   `OutputHasReconstructionArtifacts`, `ClearReservedSubtrees`, ~55 lines. The VM keeps thin
   forwarders: `BeginnerWizardFactory.cs:211,218` and `ReconstructorOutputCleanupTests` call them.
3. **`ReconstructorStartValidator`** (`internal static`, `Inputs` record + `Result` struct) — the
   validation gauntlet (1481-1697), ~215 lines. **The snapshot must still be assigned at the parse
   point (1625), not only on an accepted result.** Today `_verificationSnapshot` is written
   immediately after parsing, *before* the later rejections for missing imported files, declined
   output cleanup, or cleanup failure — so a rejected run leaves the newly parsed snapshot in
   place, not the previous run's. Returning it only on success would silently change that, and
   deferring the write past an awaited confirmation would change its observable timing. The
   validator therefore takes an `Action<VerificationSnapshot> onParsed` callback; "retain snapshots
   only from accepted starts" would be a separate behavior fix, not part of this refactor.
   The documented plan-before-mutate ordering is a safety
   property, not tidiness: every reject decision runs **before** the destructive
   `ClearReservedSubtrees()` (#3, #1, #17), and the verification file is parsed **before** output
   cleanup because cleanup may delete it (#14). The two one-shot `Suppress…Confirm` flags are
   consumed into locals before any early return.
4. **`ReconstructorViewModel.ProgressHandlers.cs`** — a mechanical `partial` split of the four
   engine handlers (2506-2627), ~120 lines, no semantic change. The `Invoke`-vs-`Post` mix is
   load-bearing and unchanged: `OnProgress` uses `Invoke`, the copy/verify handlers and the log
   flush use `Post`, `OnElapsedTimerTick` marshals not at all.
   `ManyLogEvents_CoalesceIntoAtMostOneDispatch` bounds the dispatch count (`<= 2` for 300 events),
   so converting a `Post` into an `Invoke` — or losing the coalescing flag — fails it.
5. **`ReconstructionRunner`** (`internal sealed`) — `RunArchiveSetsAsync`, `RunSingleSetAsync`,
   `SetOutcome`, `LoadEmbeddedSfvBytes`, `RelocateVerifiedOutput`, `CleanupWorkRoot`,
   `ReportSetSummary`, ~380 moved lines. Bound-state writes go back through an `IRunSink`
   (`SetPhase`, `SetMessage`, `SetPercent`, `SetElapsed`, `SetStageLabel`, `SetBusy`,
   `SetSucceeded`) the VM implements.
   **`ExecuteReconstructionAsync`'s outer `try`/`finally` wrapper stays on the VM** rather than
   moving with the loop. Its `finally` does three things a collaborator cannot own: it writes
   `ElapsedText` *before* clearing `IsRunning` (1781), and it disposes and nulls `_cts` (1803),
   which `Stop` (2341) reads. `_setStageLabel` likewise stays a VM field — `RunSingleSetAsync`
   writes it (1992) while the VM-resident progress handler reads it live from the engine's callback
   thread (2683) — so the runner sets it through the sink rather than owning it.
   The `finally`'s guarded clears stay ordered: `IsRunning = false` first, then the guarded
   `IsCopying`/`IsVerifying` clears. The reason is the **queued progress handlers**, not the head:
   `ModalProgressWindowController` is constructed per busy flag and only ever sees its own
   `IsCopying`/`IsVerifying`, never `IsRunning`. Clearing `IsRunning` first is what makes the
   `if (!IsRunning) return;` staleness gates (2512, 2556) reject a late queued `Post` that would
   otherwise re-open a closed dialog — so those gates must read the **live** flag, never a captured
   snapshot. `_progress.CompleteActiveVersion` stays paired with its own `outcomes.Add` at all four
   sites (#23).
6. **`VersionTreeCoordinator`** (drag seam, in scope) — scan + reconcile (583-795), ~190 lines,
   bridged to the six `VersionN` bools by `Func<HashSet<int>> readMajors` /
   `Action<HashSet<int>> writeMajors`. `OnWinRARPathChanged` must keep clearing
   `HasScannedVersions` and bumping `_scanToken` **before** triggering the scan (the 6-line comment
   at 253-258 documents a real config-restore data-loss bug). `ApplyScanResult`,
   `LoadPendingVersionSelection`, and `LastVersionScan` survive as internal forwarders —
   `ReconstructorConfigMapper.cs:187` calls one from production.
7. **`SRRImportApplier`** (drag seam, in scope) — extracts the *decisions* from
   `SetRARVersionsFromSRR`, `SetTimestampFlags`, `ApplySwitchDiff`, `ApplyVolumeSize` into an
   expanded diff record; the ~170 lines of `SwitchMD… = true` assignment tables stay on the VM
   behind one mechanical `Apply(diff)` switchboard. Log-line content and order are asserted
   (2876-2881) and move unchanged.
8. **`ReconstructorViewModel.Bindings.cs`** — a `partial` file holding the pinned surface: the 122
   `[ObservableProperty]` declarations, the toggle regions (797-963) with their `On<X>Changed`
   hooks, and the nested `VersionEntry` bound row. This is filing, not decomposition, and is
   labelled as such; it is the only mechanism that reduces the primary file below its ~800-line
   generated-surface floor. Feasibility is measured, not assumed (see Constraints): hooks and
   `[NotifyCanExecuteChangedFor]` targets resolve across partial files in both directions.

## Sequencing

Lib first (it is isolated in the submodule and can be pushed independently), then `CreatorViewModel`
(clean seams, strongest test coverage), then `ReconstructorViewModel`. Within each target, the
order above holds: cheapest and best-guarded first, invariant-touching last.

**Each of the three targets gets its own implementation plan.** They share no code, land in
different commits (two of them in different repositories), and are individually sized for one
session; one combined plan would be ~20 steps and would couple three independent risk profiles.

Missing characterization tests are written **before** the extraction they guard, and land as their
own commits:

- **Legacy CAV verification** (lib) — a legacy-path run with `CompleteAllVolumes` and a non-empty
  `ExpectedVolumeCrcs`. No test constructs this today, but it needs no harness change: `Hashes` and
  `ExpectedVolumeCrcs` are public mutable collections on `BruteForceOptions`, and
  `host.Options(fixture: null, …)` leaves `SRRFilePath` null so `_useAssembly` stays false (431).
  Required before step 1.3.
- **`_verificationSnapshot` handoff** (Reconstructor) — must cover **both** the successful parse →
  read at 2022 *and* a run rejected after parsing, which today still retains the newly parsed
  snapshot. Required before step 3.3.
- **`SetRARVersionsFromSRR` and `ApplyVolumeSize`** (Reconstructor) — no direct test exists.
  Required before step 3.7.
- **`_suppressGroupSync`** (Reconstructor) — only `ManualLeafToggle_SyncsMajorBooleans` touches it.
  Required before step 3.6.
- **Busy-flag clear order** (Reconstructor) — no test asserts that `IsRunning` clears before
  `IsCopying`/`IsVerifying`, nor that a late queued progress event is rejected by the staleness
  gate. Required before step 3.5.
- **Legacy duplicate-hash retention arm** (lib, 1244-1249) — exercised on the assembly side only.
- **Phase-local snapshot timing** (Creator) — that subtitles are snapshotted only after all sample
  generation completes, and `CreateVobsubSRR` is read at that same boundary. Required before
  step 2.4.
- **Selection clearing and action-hint updates on every scan outcome** (Creator), plus
  `Reset`/input-change forwarding into the controller. Required before step 2.6.
- **Cross-instance isolation** (Creator) — nothing tests that the two `CreatorViewModel` instances
  coexist. Required before step 2.6.

The two twin-path asymmetries are fixed **after** the lib decomposition lands, as their own
commits with their own tests, so each behavior change is visible in isolation:

- `DeleteDuplicateCRCFiles` honoured on the legacy mismatch path.
- A `CombinationFailed` progress row on legacy incomplete finalization.

## Out of scope

- Unifying the two finalization tails (nine observable differences; see §1).
- Extracting `catch` bodies, `[ObservableProperty]`/`[RelayCommand]`/`On<X>Changed` members, or the
  Creator's list-editing commands — blocked by the language and the source generators.
- `Directory.Build.props` / central package management: a root props file applies to the submodule's
  projects when built in-tree but not standalone.
- Behavior changes of any kind inside a decomposition commit. The asymmetry fixes are separate.
- The `TODO(-rr)` recovery-record marker (`ArchiveSetPlanner.cs:302`), which is deliberate.

## Testing

Every step is behavior-preserving and must leave all four suites green — currently 4,399 test
results: the lib's 1,559 tests run on **both** net8.0 and net10.0 (3,118 results), and App.Core
(732), headless UI (530) and CLI (19) run on net10.0 only. Zero build warnings under the
`latest-All` analyzer regime now on all eight projects.

- **Per step**: run the guarding suite named above for that seam, then the full solution before
  committing. No step lands red or with a new warning.
- **Extracted types get their own test files**, matching the house pattern where 14 of the 31
  existing `Reconstruction/` helpers already do (`ReconstructionPreflightTests`,
  `VerifiedOutputRelocatorTests`, `ReconstructionPathGuardTests`).
- **Codex reviews every diff before commit**, as in the preceding fix series; its findings are
  verified against the code rather than applied on faith.
- **Ordering-sensitive behavior is asserted, not assumed**: progress-event order and count, log
  line order and content, busy-flag transition order, and artifact splice order all have existing
  assertions that must keep passing unchanged. Where an invariant currently has only a comment, the
  extraction adds the missing test rather than trusting the comment.
