# Hotspot decomposition — design

## Problem

Three units carry most of this codebase's edit risk:

| Unit | Size | Shape |
|---|---|---|
| `Manager.TryProcessCommandLinesAsync` (`ReScene.Lib/ReScene/Core/Manager.cs:860-1515`) | 656 lines, nesting to depth 7 | One method, 15 exit points, twin near-duplicate verification paths |
| `CreatorViewModel.cs` | 2,295 lines, 57 private methods | Behavior-heavy: only 20 `[ObservableProperty]` |
| `ReconstructorViewModel.cs` | 3,091 lines, 67 private methods | Binding-heavy: 127 `[ObservableProperty]`, 13 commands |

They are not one problem. The lib method is a *correctness* hazard; `CreatorViewModel` is a
*volume* problem with clean seams; `ReconstructorViewModel` is roughly half generated-binding
surface that cannot move at all.

The lib method's hazard is concrete and documented in its own source. Its assembly and legacy
per-volume verification blocks (1329-1349 and 1399-1438) are kept in sync by two comments —
"the gate is EXACTLY the legacy block's own below" (1323) — with no shared helper and no test
that fails if they drift. ~14 lines are character-for-character identical, including the
`LogTarget.Phase2` message text. The compiler even forced a naming workaround
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
  view binds.** They cannot move to a collaborator. `ReconstructorViewModel` pins 127 properties +
  13 commands + 10 hooks + 22 notify attributes; `CreatorViewModel` pins 20 + 16 + 3 + 4. A
  `[RelayCommand(CanExecute = nameof(X))]` additionally pins `X` to the same type.
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
3. **`VerifyVolumeSet(IReadOnlyList<string> volumes, BruteForceOptions, string label)`** — the one
   unification. Builds `expectedInOrder`, applies the gate, hashes CRC32, evaluates, emits the
   single log line, returns `AllMatch` (or `true` when the gate is off). Both call sites shrink to
   ~4 lines. This dissolves the comment-enforced sync hazard *and* the `assemblyExpectedInOrder`
   naming workaround. Provably equivalent: gate expression, projection, and message text are
   already character-identical.
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
   a mutable `int? CompletedExitCode` (written from three phases: 1037, 1067, 1135), exposing
   `ObserveQuietlyAsync()` and `JoinForWinAsync()` — the two observation modes documented at
   661-673 become two named methods. Disposed **only** in the existing `finally` (1510).

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
   (1374-1922), ~549 lines, with `StagingInputs(ReleaseRoot, AppName, AutoCreateSrs,
   CreateVobsubSrr, Samples, SubtitleSfvs)`. Sources arrive as `IReadOnlyList<string>` snapshots,
   not the live collections. `releaseRoot` becomes a **non-nullable parameter**, removing the four
   `_releaseRoot!` null-forgiving reads (1540, 1557, 1722, 1774) — the one place this refactor
   strictly improves safety. The five-step ordering in `StageFolderArtifactsAsync` (1385-1419) and
   the strict two-pass structure of `GenerateSubtitleArtifactsAsync` (1696-1780) are byte-exact
   parity requirements and move unchanged. Keeps its concrete coupling to
   `ReleaseScanner.ResolveDedupKey`/`ApplyProofBeforeSfvReorder` (static, not on `IReleaseScanner`)
   — do not hide it behind the interface.
5. **`CreatorFieldGuidance`** (`internal static`) — `UpdateInputStatus`, `UpdateActionHint` as pure
   `FieldStatus`-returning functions (`ReconstructorFieldGuidance` precedent). The auto-output-path
   arbitration stays: it round-trips through `OnOutputPathChanged:358`, a generated hook.
6. **`FolderScanController`** (drag seam, in scope) — the scan lifecycle (1020-1370), ~351 lines.
   Takes the VM's `ObservableCollection`s by reference plus six setter delegates
   (`setIsScanning`, `setInputStatus`, `setOutputStatus`, `trySetAutoOutputPath`,
   `notifyCanExecuteChanged`, `notifyFolderModeChanged`) and `appendLog`, per the
   `ReconstructionProgressTracker` template. `_isFolderMode`/`_isMusicOnlyFolder`/
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
   validation gauntlet (1481-1697), ~215 lines. Returns the `VerificationSnapshot` rather than
   assigning `_verificationSnapshot`. The documented plan-before-mutate ordering is a safety
   property, not tidiness: every reject decision runs **before** the destructive
   `ClearReservedSubtrees()` (#3, #1, #17), and the verification file is parsed **before** output
   cleanup because cleanup may delete it (#14). The two one-shot `Suppress…Confirm` flags are
   consumed into locals before any early return.
4. **`ReconstructorViewModel.ProgressHandlers.cs`** — a mechanical `partial` split of the four
   engine handlers (2506-2627), ~120 lines, no semantic change. The `Invoke`-vs-`Post` mix is
   load-bearing and unchanged: `OnProgress` uses `Invoke`, the copy/verify handlers and the log
   flush use `Post`, `OnElapsedTimerTick` marshals not at all.
   `ManyLogEvents_CoalesceIntoAtMostOneDispatch` asserts an exact `PostCount`.
5. **`ReconstructionRunner`** (`internal sealed`) — `ExecuteReconstructionAsync`,
   `RunArchiveSetsAsync`, `RunSingleSetAsync`, `SetOutcome`, `LoadEmbeddedSfvBytes`,
   `RelocateVerifiedOutput`, `CleanupWorkRoot`, `ReportSetSummary`, ~380 moved lines. Bound-state
   writes go back through an `IRunSink` (`SetPhase`, `SetMessage`, `SetPercent`,
   `SetBusy(running, copying, verifying)`, `SetSucceeded`) the VM implements. The `finally`'s
   guarded clears stay ordered: `IsRunning = false` first, then the guarded `IsCopying`/
   `IsVerifying` clears — the head's `ModalProgressWindowController` generation tracking depends on
   it, and without them a cancelled mid-copy run strands a modal with no close path. The
   `if (!IsRunning) return;` staleness gates (2512, 2556) must read the **live** flag, never a
   captured snapshot. `_progress.CompleteActiveVersion` stays paired with its own `outcomes.Add` at
   all four sites (#23).
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
8. **`ReconstructorViewModel.Bindings.cs`** — a `partial` file holding the pinned surface: the 127
   `[ObservableProperty]` declarations, the toggle regions (797-963) with their `On<X>Changed`
   hooks, and the nested `VersionEntry` bound row. This is filing, not decomposition, and is
   labelled as such; it is the only mechanism that reduces the primary file below its ~800-line
   generated-surface floor.

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
  `ExpectedVolumeCrcs`, which today no test can construct. Required before step 1.3.
- **`_verificationSnapshot` handoff** (Reconstructor) — parse at 1625 → read at 2022. Required
  before step 3.3.
- **`SetRARVersionsFromSRR` and `ApplyVolumeSize`** (Reconstructor) — no direct test exists.
  Required before step 3.7.
- **`_suppressGroupSync`** (Reconstructor) — only `ManualLeafToggle_SyncsMajorBooleans` touches it.
  Required before step 3.6.
- **Legacy duplicate-hash retention arm** (lib, 1244-1249) — exercised on the assembly side only.
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

Every step is behavior-preserving and must leave all four suites green on both TFMs — currently
4,399 tests (1,559 × net8.0/net10.0 lib, 732 App.Core, 530 headless UI, 19 CLI) — with zero build
warnings under the `latest-All` analyzer regime now on all eight projects.

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
