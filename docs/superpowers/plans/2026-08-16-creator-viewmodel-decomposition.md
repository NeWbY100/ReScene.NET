# CreatorViewModel decomposition — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Decompose `ReScene.App.Core/ViewModels/CreatorViewModel.cs` from 2,295 lines into roughly 800, by extracting the folder-mode artifact staging, the per-file artifact generators, the scan session, the naming helpers, the field guidance, the folder-scan controller and the file-mode creation pipeline into collaborators — without changing a single observable behavior.

**Architecture:** This view-model is behavior-heavy, not binding-heavy: only 20 `[ObservableProperty]` members against 57 private methods. That inverts the usual MVVM decomposition problem — almost all of its bulk is genuinely extractable logic, and the pinned generated surface is small. Extractions follow the established house pattern in `ViewModels/Reconstruction/` (31 files): `internal` types, `internal static` when stateless, primary-constructor `internal sealed` when they hold a snapshot, nested records for inputs and outcomes, and `Action<string> log` for diagnostics instead of a back-reference to the view-model.

**Tech Stack:** C# / .NET 10, CommunityToolkit.Mvvm 8.4.2 source generators, xUnit 2.9.3, hand-rolled test doubles (no mocking library).

**Spec:** `docs/superpowers/specs/2026-08-15-hotspot-decomposition-design.md` (§2, plus Constraints, Sequencing and Testing)

## Global Constraints

- **Repository:** the parent repo `E:\Projects\ReScene.Manager`, branch `fix/analysis-issues`. `ReScene.Lib` is NOT touched by this plan.
- **`ConfigureAwait(false)` must NOT be added.** `CA2007` is suppressed in this repo's `.editorconfig` because the UI thread's synchronization context is load-bearing here. This is the **opposite** of `ReScene.Lib`, where `CA2007` is enforced — do not carry that habit across.
- **Analyzers:** `EnableNETAnalyzers`, `AnalysisLevel=latest-All`, `EnforceCodeStyleInBuild`, `GenerateDocumentationFile`. **Zero build warnings** is the standard; the tree is currently clean.
- **Extracted types are `internal`**, in `ReScene.App.Core/ViewModels/Creation/`, namespace `ReScene.App.Core.ViewModels.Creation`. `InternalsVisibleTo` already grants `ReScene.App.Core.Tests` access, so each gets its own test file directly — no reflection, no public surface.
- **One top-level type per file**, named after the type (`docs/coding-guidelines.md`). Nested types stay with their parent.
- **Acronym casing:** `SRR, SRS, RAR, MP3, MP4, MKV` are ALL-CAPS in identifiers; `Flac`, `Riff`, `Vob` stay PascalCase. Note the existing members use `Srr` in a few *local* names (e.g. `GenerateNestedSubtitleSrrsAsync`) — match the surrounding code when moving a member; do not rename as part of this plan.
- **Generated members cannot move:** 20 `[ObservableProperty]`, 16 `[RelayCommand]`, 3 `partial void On<X>Changed`, and `CanCreateSRR` (pinned by `[RelayCommand(CanExecute = nameof(CanCreateSRR))]`). Neither can the public API other code calls: `Reset`, `AddStoredFiles`, `IsStoredNameTaken`, `WarnDuplicateStoredName`, `BuildSampleAndSubtitlePlaceholders`, `SuppressOverwriteConfirm`, and the `internal LastFolderScan` test seam.
- **Four manual `CreateSRRCommand.NotifyCanExecuteChanged()` calls** exist because `_isMusicOnlyFolder` and `_folderScanInvalid` have no `[NotifyCanExecuteChangedFor]` backing them. Every one must keep firing in the same order relative to the `InputStatus` assignment beside it.
- **Behavior-preserving:** no task may change observable behavior — not stored-file ordering, not artifact splice positions, not log text, not when a collection is mutated relative to an await.
- **Test command:** `dotnet test ReScene.App.Core.Tests/ReScene.App.Core.Tests.csproj` (net10.0 only). Also run `dotnet test ReScene.Manager.Tests/ReScene.Manager.Tests.csproj` before committing any task that changes a bound member or a command, since the headless UI suite binds this view-model on two surfaces.

---

### Task 1: `CreatorArtifactNaming` — the parameter-driven helpers

**Files:**
- Create: `ReScene.App.Core/ViewModels/Creation/CreatorArtifactNaming.cs`
- Create: `ReScene.App.Core.Tests/CreatorArtifactNamingTests.cs`
- Modify: `ReScene.App.Core/ViewModels/CreatorViewModel.cs` (remove the 12 members, update call sites)

**Interfaces:**
- Consumes: nothing.
- Produces: `internal static class CreatorArtifactNaming` exposing, with signatures unchanged from their current private forms:
  `bool IsFilesystemRoot(string path)`, `bool IsRootError(ReleaseScanResult result)`, `string RootRelativeName(string releaseRoot, string fullPath)`, `int FindSampleArtifactSpliceIndex(List<StoredFileEntry> entries)`, `int FindSubtitleArtifactSpliceIndex(List<StoredFileEntry> entries)`, `bool IsUnderProofDirectory(string storedName)`, `bool HasMatchingSfv(string storedName, List<StoredFileEntry> entries)`, `bool IsRarBackedVobSample(string samplePath)`, `string FolderRelativeName(string releaseRoot, string sourcePath, string conventionalDir)`, `string FolderRelativeStem(string releaseRoot, string sourcePath, string conventionalDir)`, `string GeneratedStoredName(string releaseDir, string sourcePath, string newExtension, string conventionalDir)`, `List<string> DiscoverRARVolumes(string firstRARPath)`.

- [x] **Step 1: Create the class and move the twelve members verbatim**

Create `ReScene.App.Core/ViewModels/Creation/CreatorArtifactNaming.cs`:

```csharp
using ReScene.App.Core.Services;

namespace ReScene.App.Core.ViewModels.Creation;

/// <summary>
/// Naming, classification and splice-position helpers for Creator artifacts. Every member takes
/// everything it needs as a parameter and holds no view-model state, which is what makes them
/// movable — note that several of them (<see cref="DiscoverRARVolumes"/>,
/// <see cref="IsRarBackedVobSample"/>) do read the filesystem, so this is a parameter-driven
/// helper set rather than a side-effect-free one.
/// </summary>
internal static class CreatorArtifactNaming
{
    // The twelve members move here VERBATIM from CreatorViewModel, changing only `private static`
    // to `internal static` and keeping every doc comment. Their bodies must not be edited: the
    // splice finders in particular encode pyrescene ordering parity that byte-identity tests pin.
}
```

Move each member from `CreatorViewModel.cs` (current lines 1349, 1363, 1369, 1431, 1464, 1483, 1509, 1655, 1908, 1917, 2205, 2214) into it, changing only the accessibility modifier. **Copy every XML doc comment with the member** — `FindSampleArtifactSpliceIndex` and `FindSubtitleArtifactSpliceIndex` carry multi-paragraph rationale describing exactly which pyrescene pass they reproduce, and that rationale is the reason the code is correct.

- [x] **Step 2: Update the call sites**

In `CreatorViewModel.cs`, add `using ReScene.App.Core.ViewModels.Creation;` and prefix each call with `CreatorArtifactNaming.`. There are roughly 25 call sites; the compiler will find every one you miss.

- [x] **Step 3: Build and run both suites**

Run: `dotnet build ReScene.App.Core/ReScene.App.Core.csproj --no-incremental`
Expected: 0 warnings, 0 errors.

Run: `dotnet test ReScene.App.Core.Tests/ReScene.App.Core.Tests.csproj`
Expected: **PASS**, same total as before this task (732).

- [x] **Step 4: Add a direct test file for the moved helpers**

The helpers were previously reachable only through the view-model. Now that they are `internal`, test them directly. Create `ReScene.App.Core.Tests/CreatorArtifactNamingTests.cs`:

```csharp
using ReScene.App.Core.ViewModels.Creation;

namespace ReScene.App.Core.Tests;

/// <summary>
/// Direct tests for the naming helpers extracted from CreatorViewModel. They were previously
/// private and exercised only end-to-end through folder-mode staging; testing them directly pins
/// the string arithmetic without building a whole release tree.
/// </summary>
public class CreatorArtifactNamingTests
{
    [Theory]
    [InlineData(@"C:\rel", @"C:\rel\Sample\clip.mkv", "Sample/clip.mkv")]
    [InlineData(@"C:\rel", @"C:\rel\a.nfo", "a.nfo")]
    public void RootRelativeName_IsRootRelative_WithForwardSlashes(string root, string full, string expected)
        => Assert.Equal(expected, CreatorArtifactNaming.RootRelativeName(root, full));

    [Fact]
    public void FolderRelativeName_SourceOutsideRoot_FallsBackToConventionalDir()
    {
        string outside = Path.Combine(Path.GetTempPath(), "elsewhere", "clip.mkv");
        string actual = CreatorArtifactNaming.FolderRelativeName(@"C:\rel", outside, "Sample");
        Assert.Equal("Sample/clip.mkv", actual);
    }

    [Theory]
    [InlineData("Proof/x.rar", true)]
    [InlineData("proof/x.rar", true)]
    [InlineData("Proofread/x.rar", false)]   // substring, not a directory segment
    [InlineData("x-proof.rar", false)]
    public void IsUnderProofDirectory_MatchesDirectorySegmentsOnly(string storedName, bool expected)
        => Assert.Equal(expected, CreatorArtifactNaming.IsUnderProofDirectory(storedName));
}
```

Adjust the expected values to whatever the moved implementations actually produce — read each method before writing its assertion, and if a case surprises you, the implementation is the source of truth here, not your expectation. Do not change an implementation to satisfy a guess.

- [x] **Step 5: Run and commit**

Run: `dotnet test ReScene.App.Core.Tests/ReScene.App.Core.Tests.csproj`
Expected: **PASS**, total increased by the number of new test cases.

```bash
git add ReScene.App.Core/ViewModels/Creation/CreatorArtifactNaming.cs ReScene.App.Core/ViewModels/CreatorViewModel.cs ReScene.App.Core.Tests/CreatorArtifactNamingTests.cs
git commit -m "refactor(creator): extract the artifact naming helpers

Twelve parameter-driven helpers move to CreatorArtifactNaming verbatim,
keeping their rationale comments - the splice finders encode pyrescene
ordering parity that byte-identity tests pin. Now internal, so they get
direct tests instead of being reachable only through folder-mode staging.

Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>"
```

---

### Task 2: `FolderScanSession` — the generation and cancellation holder

This isolates the sharpest edge in the file. It reduces risk more than line count.

**Files:**
- Create: `ReScene.App.Core/ViewModels/Creation/FolderScanSession.cs`
- Modify: `ReScene.App.Core/ViewModels/CreatorViewModel.cs`

**Interfaces:**
- Consumes: nothing.
- Produces: `internal sealed class FolderScanSession` with
  `(int Generation, CancellationTokenSource Cts, CancellationToken Token) Begin()`,
  `void CancelInFlight()`,
  `bool IsCurrent(int generation, CancellationTokenSource cts)`,
  `void CompleteIfCurrent(CancellationTokenSource cts)`,
  `int BumpGeneration()`.

- [x] **Step 1: Create the session type**

```csharp
namespace ReScene.App.Core.ViewModels.Creation;

/// <summary>
/// Owns a folder scan's generation counter and in-flight cancellation source. Extracted so the
/// scan's cancel/claim discipline lives in one place instead of being spread across the
/// view-model's input-path hook, its reset, and the scan continuation.
/// </summary>
/// <remarks>
/// Every member is called on the UI thread. The rules this type exists to keep:
/// a scan result is applied only when BOTH its generation and its cancellation-source reference
/// still match the current ones (generation alone is not enough — a superseded scan can share a
/// generation with its replacement across a reset), and a source is disposed only by the scan that
/// owns it, never by a later caller.
/// </remarks>
internal sealed class FolderScanSession
{
    private int _generation;
    private CancellationTokenSource? _cts;

    /// <summary>Bumps the generation without starting a scan — for a reset or a mode exit.</summary>
    public int BumpGeneration() => ++_generation;

    /// <summary>Cancels and forgets any in-flight scan. The scan itself disposes its own source.</summary>
    public void CancelInFlight()
    {
        _cts?.Cancel();
        _cts = null;
    }

    /// <summary>
    /// Starts a new scan: bumps the generation and installs a fresh source. The token is captured
    /// EAGERLY here, because <c>CancellationTokenSource.Token</c>'s getter throws
    /// <see cref="ObjectDisposedException"/> once the source is disposed, and the scan body may not
    /// reach its first read until after a rapid input change has disposed it.
    /// </summary>
    public (int Generation, CancellationTokenSource Cts, CancellationToken Token) Begin()
    {
        var cts = new CancellationTokenSource();
        _cts = cts;
        return (++_generation, cts, cts.Token);
    }

    /// <summary>Whether a completed scan's result may still be applied.</summary>
    public bool IsCurrent(int generation, CancellationTokenSource cts)
        => generation == _generation && ReferenceEquals(cts, _cts);

    /// <summary>Clears the in-flight reference if it is still the given scan's own source.</summary>
    public void CompleteIfCurrent(CancellationTokenSource cts)
    {
        if (ReferenceEquals(cts, _cts))
        {
            _cts = null;
        }
    }
}
```

- [x] **Step 2: Replace the two fields and rewire**

In `CreatorViewModel.cs`, replace `private int _scanGeneration;` and `private CancellationTokenSource? _scanCts;` with `private readonly FolderScanSession _scan = new();`.

Then rewire, preserving each site's exact position:
- `Reset` (current line ~248) and `OnInputPathChanged` (~320): `CancelInFlightScan()` → `_scan.CancelInFlight()`, and the generation bump → `_scan.BumpGeneration()`. **The bump in `OnInputPathChanged` must stay BEFORE the `Directory.Exists` branch** — `StartFolderScan` reads the generation afterwards, and that ordering is load-bearing.
- `StartFolderScan` (~1089): take `(generation, cts, token)` from `_scan.Begin()`.
- `RunFolderScanAsync`'s two guard points: replace the paired generation-and-reference checks with `_scan.IsCurrent(generation, cts)`, keeping them as **hard bails** (`return`), not "skip applying".
- The scan's own cleanup: `_scan.CompleteIfCurrent(cts)` plus the existing `cts.Dispose()`.

**Do not** change which thread any of this runs on, and do not move the eager token capture.

- [x] **Step 3: Build and run**

Run: `dotnet build ReScene.App.Core/ReScene.App.Core.csproj --no-incremental` → 0 issues.
Run: `dotnet test ReScene.App.Core.Tests/ReScene.App.Core.Tests.csproj` → **PASS**, unchanged total.

The load-bearing guard is `RapidInputSwitching_WithoutAwaiting_NeverThrows` (`CreatorViewModelFolderModeTests.cs:647`), together with `StaleScan_Discarded_WhenNewerInputSupersedes` (:223) and the `InputChange_ToNonFolder_DiscardsStaleFolderScan` theory (:493). If any of those fail, the claim/cancel discipline moved — revert and redo rather than adjusting the test.

- [x] **Step 4: Prove the eager token capture still matters**

Temporarily change `Begin()` to return `cts` without the token and have `StartFolderScan` read `cts.Token` inside the scan body instead.
Run: `dotnet test ReScene.App.Core.Tests/ReScene.App.Core.Tests.csproj --filter "FullyQualifiedName~RapidInputSwitching"`
Expected: **FAIL** with `ObjectDisposedException`. Revert and confirm PASS.

If it does NOT fail, say so in the commit message rather than claiming the guard is proven — the race may simply not reproduce on this machine, and a silent claim would be worse than an honest note.

- [x] **Step 5: Commit**

```bash
git add ReScene.App.Core/ViewModels/Creation/FolderScanSession.cs ReScene.App.Core/ViewModels/CreatorViewModel.cs
git commit -m "refactor(creator): extract the folder-scan session

Generation counter and in-flight cancellation source move into
FolderScanSession, so the claim/cancel discipline lives in one place: a result
applies only when BOTH generation and source reference still match, and a
source is disposed only by the scan that owns it. The eager token capture
moves verbatim - CancellationTokenSource.Token throws once disposed, which is
the race RapidInputSwitching_WithoutAwaiting_NeverThrows exists to catch.

Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>"
```

---

### Task 3: `ArtifactFileGenerator` — the per-file generators

Extracted **before** the stager so the stager comes out clean, and because this is where the one cross-mode call currently violates the file's own section boundaries.

**Files:**
- Create: `ReScene.App.Core/ViewModels/Creation/ArtifactFileGenerator.cs`
- Modify: `ReScene.App.Core/ViewModels/CreatorViewModel.cs`

**Interfaces:**
- Consumes: `ISRRCreationService`, `ISRSCreationService` (already injected into the view-model).
- Produces: `internal sealed class ArtifactFileGenerator(ISRRCreationService srrService, ISRSCreationService srsService, Action<string> log)` with
  `Task<string?> GenerateSRSFileAsync(string samplePath, string tempDir, int index, SRSCreationOptions srsOptions, CancellationToken ct)`,
  `Task<string?> GenerateNestedSRRFileAsync(string sfvPath, string tempDir, int index, SRRCreationOptions options, CancellationToken ct)`,
  `Task<List<StoredFileEntry>> GenerateNestedSubtitleSrrsAsync(...)` (keep the current parameter list verbatim),
  and the `static` `GenerateAndRecordAsync<TSource>` helper.

- [x] **Step 1: Create the generator with a primary constructor**

```csharp
using ReScene.App.Core.Services;
using ReScene.SRR;
using ReScene.SRS;

namespace ReScene.App.Core.ViewModels.Creation;

/// <summary>
/// Creates the individual artifact files a Creator run stores alongside a release: a sample's
/// `.srs`, and the nested `.srr` for a subtitle `.sfv`'s RAR chain. Extracted from
/// CreatorViewModel so both the folder-mode stager and the file-mode/wizard paths call one
/// implementation — before this existed, the file-mode vobsub path reached across into the
/// folder-mode section to reuse the nested-SRR generator.
/// </summary>
/// <param name="srrService">Creates SRR files; per-instance, so its progress stream stays this view-model's.</param>
/// <param name="srsService">Creates SRS files, likewise per-instance.</param>
/// <param name="log">Receives user-facing progress and failure lines.</param>
internal sealed class ArtifactFileGenerator(
    ISRRCreationService srrService, ISRSCreationService srsService, Action<string> log)
{
    // Members move here VERBATIM from CreatorViewModel (current lines 1802, 1896, 1996, 2033,
    // 2079), with `_sRRService`/`_sRSService`/`Log(...)` rewritten to the constructor parameters.
}
```

Move `GenerateNestedSubtitleSrrsAsync` (1802), `BuildNestedSubtitleStoredFiles` (1896), `GenerateSRSFileAsync` (1996), `GenerateNestedSRRFileAsync` (2033) and `GenerateAndRecordAsync<TSource>` (2079).

**`BuildNestedSubtitleStoredFiles()` returns `null` and must keep its twelve-line comment.** That comment records a deliberate shipped-behavior decision (the subtitle SFV's own bytes are already stored in the outer SRR, so the nested SRR stores nothing additional). Inlining it to `null` at the call site loses the rationale — leave it as a method.

**The three `nestedOptions` constructions** (currently at ~1615, ~1833, ~2044) each build `AllowCompressed = true, ComputeOSOHashes = false` independently. They may share one private factory inside this class, but must never forward the caller's outer `options` — four tests guard that (`SubtitleNestedSrr_ForcesComputeOSOHashesFalse_…`, `RarBackedVobSample_NestedSrr_ForcesComputeOSOHashesFalse_…`, `AdvancedTab_VobsubScan_OuterOsoTrue_…`, `WizardPlaceholder_NestedSubtitleSrr_OuterOsoTrue_HasNoOso`).

- [x] **Step 2: Construct it in the view-model and update the three call sites**

Add `private readonly ArtifactFileGenerator _artifacts;` and build it in the constructor:

```csharp
        _artifacts = new ArtifactFileGenerator(sRRService, sRSService, message => Log(message));
```

Update the three callers — `MaterializePlaceholdersAsync`, `CreateSRSForSamplesAsync`, `CreateVobsubSRRsAsync` — to call through `_artifacts`. **They keep owning the `StoredFiles` mutation**; only the file creation moves.

- [x] **Step 3: Build and run both suites**

Run: `dotnet build ReScene.App.Core/ReScene.App.Core.csproj --no-incremental` → 0 issues.
Run: `dotnet test ReScene.App.Core.Tests/ReScene.App.Core.Tests.csproj` → **PASS**, unchanged total.

Guards: `SubtitleSfv_MultipleRarChains_YieldsOneNestedSrrPerChain_…`, `SubtitleSfv_SpacedRarName_…`, `SubtitleSfv_DotSlashContinuation_…`, `AdvancedTab_VobsubScan_*`, `WizardPlaceholder_NestedSubtitleSrr_*`, `CreateSRR_TwoSamplesSameBasename_GenerateDistinctTempFiles`.

- [x] **Step 4: Commit**

```bash
git add ReScene.App.Core/ViewModels/Creation/ArtifactFileGenerator.cs ReScene.App.Core/ViewModels/CreatorViewModel.cs
git commit -m "refactor(creator): extract the per-file artifact generators

SRS creation, nested-SRR creation and the shared generate-and-record helper
move into ArtifactFileGenerator, with the creation services and a log callback
injected. This gives the one cross-mode call a proper home: the file-mode
vobsub path previously reached into the folder-mode section to reuse the
nested-SRR generator.

The three nestedOptions constructions keep forcing AllowCompressed=true and
ComputeOSOHashes=false independently of the caller's outer options, and
BuildNestedSubtitleStoredFiles keeps its rationale comment rather than being
inlined to null.

Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>"
```

---

### Task 4: Characterization test — phase-local snapshot timing

Required before Task 5. The stager reads `ExtraSampleFiles` when sample generation begins, but reads `ExtraSubtitleSfvFiles` and `CreateVobsubSRR` only **after** all sample generation completes. Nothing currently pins that, so an extraction that hoists both reads to the top would pass the suite while changing behavior.

**Files:**
- Modify: `ReScene.App.Core.Tests/CreatorViewModelArtifactTests.cs`

**Interfaces:**
- Consumes: the file's existing `CreateVm` folder-mode factory and its fake creation services.
- Produces: nothing consumed later.

- [x] **Step 1: Add a mid-call hook to the existing SRS double**

`RecordingSRSCreationService` (top of `CreatorViewModelArtifactTests.cs`) already supports `Configure` and `ConfigureThrow`. Add one more, in the same style:

```csharp
        /// <summary>Runs inside CreateAsync, before it returns — lets a test mutate view-model
        /// state DURING sample generation to pin when a later phase reads its own inputs.</summary>
        public Action<string>? OnCreate
        {
            get; set;
        }
```

and invoke it as the first statement of `CreateAsync`, immediately after `CallsInOrder.Add(sampleFilePath);`:

```csharp
            OnCreate?.Invoke(sampleFilePath);
```

- [x] **Step 2: Write the test**

```csharp
    [Fact]
    public async Task Staging_ReadsSubtitleInputsAfterSampleGeneration_NotUpFront()
    {
        // Pins phase-local snapshot timing. Sample inputs are read when sample generation begins;
        // subtitle inputs are read only AFTER all sample generation completes. An extraction that
        // gathers both up front into one inputs record would still produce a valid SRR and would
        // still pass every other test in this file — but a subtitle SFV that arrived while samples
        // were generating would silently stop being stored.
        string root = Path.Combine(TempDir, "rel-" + Guid.NewGuid().ToString("N"));
        string sample = Path.Combine(root, "Sample", "clip.mkv");
        Directory.CreateDirectory(Path.GetDirectoryName(sample)!);
        File.WriteAllBytes(sample, [1, 2, 3]);

        var scan = new ReleaseScanResult([], [sample], [], [], [], []);
        (CreatorViewModel vm, RecordingSRSCreationService srs, RecordingSRRCreationService srr, _) = CreateVm(scan);

        // Arrives mid-sample-generation, exactly as a scanner result or an Add Subtitle click
        // landing while an earlier phase is awaiting would.
        string lateSfv = WriteSfv(Path.Combine(root, "Subs", "late.sfv"), "sub.rar");
        srs.OnCreate = _ => vm.ExtraSubtitleSfvFiles.Add(lateSfv);

        IReadOnlyList<StoredFileEntry> stored = await RunCreateAsync(vm, root, srr);

        Assert.Contains(stored, e => e.StoredName.EndsWith("late.sfv", StringComparison.OrdinalIgnoreCase));
    }
```

If `RunCreateAsync`'s scan overwrites `ExtraSubtitleSfvFiles` before staging runs, add the late SFV from `OnCreate` as written above (which fires after the scan) — that is why the hook is on the SRS service rather than set before the run.

- [x] **Step 3: Run it**

Run: `dotnet test ReScene.App.Core.Tests/ReScene.App.Core.Tests.csproj --filter "FullyQualifiedName~Staging_ReadsSubtitleInputsAfterSampleGeneration"`
Expected: **PASS** against current behavior (characterization).

If it fails, the reads are not phase-local the way the spec claims — stop and report, do not adjust the production code.

- [x] **Step 4: Prove it has teeth**

Temporarily hoist the subtitle read: in `StageFolderArtifactsAsync`, capture `List<string> subtitleSnapshot = [.. ExtraSubtitleSfvFiles];` as the **first** statement and make `GenerateSubtitleArtifactsAsync` use that snapshot.
Run the same filter. Expected: **FAIL** — the late-added SFV is missing. Revert and confirm PASS.

- [x] **Step 5: Commit**

```bash
git add ReScene.App.Core.Tests/CreatorViewModelArtifactTests.cs
git commit -m "test(creator): pin phase-local snapshot timing in folder-mode staging

Sample inputs are read when sample generation begins; subtitle inputs and
CreateVobsubSRR only after it completes. Nothing pinned that, so an extraction
gathering both up front would pass the suite while silently dropping a
subtitle SFV added mid-run. Verified to have teeth by hoisting the read.

Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>"
```

---

### Task 5: `CreatorArtifactStager` — folder-mode staging

The largest single extraction in this plan (~549 lines).

**Files:**
- Create: `ReScene.App.Core/ViewModels/Creation/CreatorArtifactStager.cs`
- Modify: `ReScene.App.Core/ViewModels/CreatorViewModel.cs`

**Interfaces:**
- Consumes: `ArtifactFileGenerator` (Task 3), `CreatorArtifactNaming` (Task 1).
- Produces: `internal sealed class CreatorArtifactStager(ArtifactFileGenerator generator, Func<string> releaseRoot, Action<string> log)` with
  `Task<List<StoredFileEntry>> StageAsync(List<StoredFileEntry> baseline, string workDir, SRRCreationOptions options, bool autoCreateSrs, bool createVobsubSrr, IReadOnlyList<string> samples, IReadOnlyList<string> subtitleSfvs, CancellationToken ct)`.

**The release root is a `Func<string>`, not a captured string.** The current code re-reads `_releaseRoot` at each phase point, across awaits, while `InputPath` stays user-editable during a run. Capturing it once would be a behavior change.

**Sample and subtitle inputs use DIFFERENT shapes, deliberately.** Samples arrive as a materialized `IReadOnlyList<string>` (the caller passes `[.. ExtraSampleFiles]`, snapshotted at the call, which is when the current code reads them). Subtitles arrive as `Func<IReadOnlyList<string>> subtitleSfvs`, invoked by the stager only after sample generation has completed — which is when the current code reads them, and what Task 4 pins. Do not "simplify" both to the same shape: making samples lazy or subtitles eager each changes behavior in one direction.

So the signature is:

```csharp
    public Task<List<StoredFileEntry>> StageAsync(
        List<StoredFileEntry> baseline,
        string workDir,
        SRRCreationOptions options,
        bool autoCreateSrs,
        IReadOnlyList<string> samples,
        Func<IReadOnlyList<string>> subtitleSfvs,
        Func<bool> createVobsubSrr,
        CancellationToken ct)
```

`createVobsubSrr` is likewise a `Func<bool>`: the current code reads that property after sample generation, not before.

- [x] **Step 1: Create the stager**

Move `StageFolderArtifactsAsync` (1385), `GenerateSampleArtifactsAsync` (1527) and `GenerateSubtitleArtifactsAsync` (1696) verbatim, rewriting `_releaseRoot!` to the accessor, `Log(...)` to the callback, and the generator calls to the injected `ArtifactFileGenerator`.

**These orderings are byte-exact parity requirements and move unchanged:**
1. Samples are generated first, so the subtitle pass's already-stored-RAR check sees the post-sample list.
2. `.srs` supersede runs **before** the splice.
3. `kept.InsertRange(FindSampleArtifactSpliceIndex(kept), samples)` — the index is computed against the **already-superseded** list.
4. Subtitles are spliced after.
5. `ReleaseScanner.ApplyProofBeforeSfvReorder` is re-applied over the **complete merged** list, last.

And inside `GenerateSubtitleArtifactsAsync`, the strict two-pass structure: **all** nested SRRs first, then **all** SFV entries. Its own comment records that per-SFV interleaving was tried and produced wrong output.

Keep the concrete `ReleaseScanner.ResolveDedupKey` / `ReleaseScanner.ApplyProofBeforeSfvReorder` calls as concrete — they are `internal static` on the class, not on `IReleaseScanner`, and hiding them behind the interface would be a lie about the coupling.

- [x] **Step 2: Wire the view-model**

Construct the stager in the view-model's constructor and replace the `CreateSRRAsync` folder-mode branch's call with `_stager.StageAsync(...)`. The workdir creation and its `finally` cleanup stay in `CreateSRRAsync`.

- [x] **Step 3: Build and run both suites**

Run: `dotnet build ReScene.App.Core/ReScene.App.Core.csproj --no-incremental` → 0 issues.
Run: `dotnet test ReScene.App.Core.Tests/ReScene.App.Core.Tests.csproj` → **PASS**, unchanged total.
Run: `dotnet test ReScene.Manager.Tests/ReScene.Manager.Tests.csproj` → **PASS** (530).

Guards: essentially all of `CreatorViewModelArtifactTests.cs` (~40 tests), including Task 4's new one, `RealScanner_SubtitleSfv_StoredExactlyOnce_NestedSrrImmediatelyBeforeIt` (which uses the **real** `ReleaseScanner`, so it also guards the stager↔scanner interplay), `Cancellation_DuringArtifactStaging_RemovesWorkingDir_DoesNotSwallowOce`, and the proof-reorder ordering tests.

- [x] **Step 4: Commit**

```bash
git add ReScene.App.Core/ViewModels/Creation/CreatorArtifactStager.cs ReScene.App.Core/ViewModels/CreatorViewModel.cs
git commit -m "refactor(creator): extract folder-mode artifact staging

StageFolderArtifactsAsync and the sample/subtitle generation passes move into
CreatorArtifactStager. The release root arrives as a live accessor rather than
a captured string, because the current code re-reads it at each phase point
across awaits while InputPath stays user-editable during a run.

Every ordering that byte-identity depends on moves unchanged: samples before
subtitles, supersede before splice, splice index against the already-superseded
list, and the proof-before-sfv reorder last over the merged list; and inside
the subtitle pass, all nested SRRs before all SFV entries. The concrete
ReleaseScanner static calls stay concrete - they are not on IReleaseScanner and
pretending otherwise would misrepresent the coupling.

Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>"
```

---

### Task 6: `CreatorFieldGuidance` — input status and action hint

**Files:**
- Create: `ReScene.App.Core/ViewModels/Creation/CreatorFieldGuidance.cs`
- Create: `ReScene.App.Core.Tests/CreatorFieldGuidanceTests.cs`
- Modify: `ReScene.App.Core/ViewModels/CreatorViewModel.cs`

**Interfaces:**
- Produces: `internal static class CreatorFieldGuidance` with
  `FieldStatus BuildInputStatus(...)` and `string BuildActionHint(...)` — exact parameter lists derived from what `UpdateInputStatus` (373) and `UpdateActionHint` (396) currently read.

Note `UpdateActionHint` returns a **string** (`ActionHint`), not a `FieldStatus`. Neither function is side-effect-free: `UpdateInputStatus` calls `File.Exists` and `CountReleaseArchives`. Pass the inputs it needs and let it keep reading the filesystem exactly where it does now.

- [x] **Step 1: Read both methods and design the parameter lists**

Read `CreatorViewModel.cs:373-414`. List every view-model member each one reads. Those become parameters. Do not pass the view-model itself.

- [x] **Step 2: Create the class, move the logic, leave two-line assigners behind**

The view-model keeps:

```csharp
    private void UpdateInputStatus(string value)
        => InputStatus = CreatorFieldGuidance.BuildInputStatus(value, /* … */);

    private void UpdateActionHint()
        => ActionHint = CreatorFieldGuidance.BuildActionHint(/* … */);
```

The auto-output-path arbitration (`_outputPathAutoGenerated`, `_lastAutoOutputPath`, `TrySetAutoOutputPath`, `AutoSetFolderOutputPath`) **stays on the view-model** — it round-trips through `OnOutputPathChanged`, a generated hook that cannot move.

- [x] **Step 3: Add direct tests**

Create `ReScene.App.Core.Tests/CreatorFieldGuidanceTests.cs` covering at least: a blank input, a nonexistent path, a valid file, a valid folder, and the busy/scanning state. Read the implementation for the exact expected strings rather than guessing them.

- [x] **Step 4: Run and commit**

Run both suites. Expected: **PASS**.

```bash
git add ReScene.App.Core/ViewModels/Creation/CreatorFieldGuidance.cs ReScene.App.Core/ViewModels/CreatorViewModel.cs ReScene.App.Core.Tests/CreatorFieldGuidanceTests.cs
git commit -m "refactor(creator): extract input-status and action-hint computation

Both become functions returning the value the view-model assigns - a
FieldStatus and a string respectively. Neither is side-effect-free
(UpdateInputStatus reads the filesystem), so they take their inputs as
parameters and keep reading it where they did. The auto-output-path
arbitration stays behind: it round-trips through a generated hook.

Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>"
```

---

### Task 7: Characterization tests — scan-outcome side effects

Required before Task 9. `ClearFolderScanResults` also clears the three `Selected*` properties, and **every** scan outcome calls `UpdateActionHint` — including successful completion. Neither is currently asserted, and both would be easy to drop when moving the lifecycle behind delegates.

**Files:**
- Modify: `ReScene.App.Core.Tests/CreatorViewModelFolderModeTests.cs`

- [x] **Step 1: Write two tests**

```csharp
    [Fact]
    public async Task Scan_ClearsSelectionsAlongWithTheCollections()
    {
        // ClearFolderScanResults clears SelectedStoredFile, SelectedExtraSample and
        // SelectedExtraSubtitle as well as the collections themselves. Moving the lifecycle behind
        // setter delegates makes it easy to forget the selections, leaving a selection pointing at
        // an item no longer in its list.
        string root = CreateFolder();
        CreatorViewModel vm = CreateVm(new StubReleaseScanner(), out _);

        vm.ExtraSampleFiles.Add(@"C:\stale\clip.mkv");
        vm.SelectedExtraSample = vm.ExtraSampleFiles[0];
        vm.ExtraSubtitleSfvFiles.Add(@"C:\stale\subs.sfv");
        vm.SelectedExtraSubtitle = vm.ExtraSubtitleSfvFiles[0];

        vm.InputPath = root;
        await vm.LastFolderScan!;

        Assert.Null(vm.SelectedExtraSample);
        Assert.Null(vm.SelectedExtraSubtitle);
        Assert.Null(vm.SelectedStoredFile);
    }

    [Fact]
    public async Task Scan_UpdatesActionHint_OnSuccessfulCompletionToo()
    {
        // Every scan outcome calls UpdateActionHint — success included, not only the failure paths.
        string root = CreateFolder();
        CreatorViewModel vm = CreateVm(new StubReleaseScanner(), out _);

        vm.ActionHint = "sentinel — must be recomputed by the scan's completion";

        vm.InputPath = root;
        await vm.LastFolderScan!;

        Assert.NotEqual("sentinel — must be recomputed by the scan's completion", vm.ActionHint);
    }
```

If `ActionHint` has no public setter, capture its value before the scan into a local and assert it *changed* instead of writing a sentinel. Read the property first; do not add a setter to make the test convenient.

- [x] **Step 2: Run** → **PASS** (characterization).

- [x] **Step 3: Prove teeth** — comment out the selection clears in `ClearFolderScanResults`, confirm the first test fails; comment out the success-path `UpdateActionHint()` call, confirm the second fails. Revert both, confirm PASS.

- [x] **Step 4: Commit**

```bash
git add ReScene.App.Core.Tests/CreatorViewModelFolderModeTests.cs
git commit -m "test(creator): pin scan-outcome selection clearing and action-hint updates

ClearFolderScanResults clears the three Selected* properties as well as the
collections, and every scan outcome updates the action hint including success.
Neither was asserted; both are easy to drop when the lifecycle moves behind
setter delegates. Verified to have teeth.

Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>"
```

---

### Task 8: Characterization test — cross-instance isolation

Required before Task 9. Two `CreatorViewModel` instances coexist — the Advanced tab's and the Beginner wizard's — wired with **different** creation-service instances so progress never crosses. Nothing tests that they coexist.

**Files:**
- Modify: `ReScene.App.Core.Tests/CreatorViewModelTests.cs`

- [x] **Step 1: Write the test**

```csharp
    [Fact]
    public void TwoInstances_DoNotShareState_OrProgressStreams()
    {
        // MainWindowViewModel constructs two CreatorViewModels: the Advanced tab's (seeded with the
        // shared creation services) and the wizard's (given its own). The constructor subscribes
        // _sRRService.Progress and never unsubscribes, so an extraction that changes when or where
        // that subscription happens can make one instance's progress stream into the other's log.
        CreatorViewModel advanced = CreateVm(out FakeSRRCreationService advancedSrr);
        CreatorViewModel wizard = CreateVm(out FakeSRRCreationService wizardSrr);

        Assert.NotSame(advancedSrr, wizardSrr);

        advanced.StoredFiles.Add(new CreatorViewModel.StoredFileItem { FullPath = @"C:\a.nfo", StoredName = "a.nfo" });
        int wizardLogBefore = wizard.LogEntries.Count;

        advancedSrr.RaiseProgress();

        Assert.Empty(wizard.StoredFiles);
        Assert.Equal(wizardLogBefore, wizard.LogEntries.Count);
        Assert.False(wizard.IsCreating);
    }
```

`FakeSRRCreationService` may not expose a progress-raising hook yet. If it does not, add one in the same style as the other doubles in this file:

```csharp
        public void RaiseProgress() =>
            Progress?.Invoke(this, new SRRCreationProgressEventArgs(/* minimal valid args */));
```

changing its `Progress` from a discarding `{ add { } remove { } }` accessor to a real event only if that is what it already is — read it first, and keep the change additive.

- [x] **Step 2: Run** → **PASS** (characterization).

- [x] **Step 3: Prove teeth** — temporarily construct both with the *same* service double and confirm the test fails. Revert.

- [x] **Step 4: Commit**

```bash
git add ReScene.App.Core.Tests/CreatorViewModelTests.cs
git commit -m "test(creator): pin cross-instance isolation

The Advanced tab and the Beginner wizard each own a CreatorViewModel wired with
its own creation-service instances precisely so progress never crosses, and the
constructor's Progress subscription is never removed. Nothing tested that they
coexist. Verified to have teeth by sharing one double.

Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>"
```

---

### Task 9: `FolderScanController` — the scan lifecycle (drag seam)

In scope per the spec's "everything" decision, but this is the seam that drags the most back: it needs eight setter delegates plus two explicit lifecycle entry points.

**Files:**
- Create: `ReScene.App.Core/ViewModels/Creation/FolderScanController.cs`
- Modify: `ReScene.App.Core/ViewModels/CreatorViewModel.cs`

**Interfaces:**
- Consumes: `FolderScanSession` (Task 2), `IReleaseScanner`, `IUiDispatcher`, `CreatorArtifactNaming` (Task 1).
- Produces: `internal sealed class FolderScanController` taking the view-model's four `ObservableCollection`s by reference plus **eight** callbacks — `setIsScanning`, `setInputStatus`, `setOutputStatus`, `trySetAutoOutputPath`, `notifyCanExecuteChanged`, `notifyFolderModeChanged`, `clearSelections`, `updateActionHint` — and `appendLog`; exposing `Start(string releaseRoot)`, `ExitFolderMode()`, `Reset()`, `Task? LastScan`, and read-only `IsFolderMode`, `IsMusicOnly`, `IsInvalid`, `ReleaseRoot`.

`Reset()` and `ExitFolderMode()` are required because `CreatorViewModel.Reset` and `OnInputPathChanged` sit **outside** the moved range yet mutate the moved fields; once those fields live in the controller, both callers need an API rather than field access.

- [x] **Step 1: Create the controller and move the lifecycle**

Move `ExitFolderMode` (1028), `CancelInFlightScan` (1049), `ClearFolderScanResults` (1072), `StartFolderScan` (1089), `RunFolderScanAsync`, `ApplyFolderScanResult`, `AutoSetFolderOutputPath` and `TrySetAutoOutputPath`'s folder-mode half. `_isFolderMode`, `_isMusicOnlyFolder`, `_folderScanInvalid` and `_releaseRoot` move in and are re-exposed read-only.

**All four manual `CreateSRRCommand.NotifyCanExecuteChanged()` calls must keep firing in the same order relative to the `InputStatus` assignment beside them** — `_isMusicOnlyFolder` and `_folderScanInvalid` have no `[NotifyCanExecuteChangedFor]`, so those manual calls are the only thing refreshing the command.

`ExitFolderMode` must keep clearing `IsScanning` **synchronously**; a discarded scan will not do it, and deferring it strands the UI in "Scanning…" with Create disabled.

- [x] **Step 2: Wire the view-model**

`CanCreateSRR` and `CreateSRRAsync` read the controller's read-only properties. `Reset` and `OnInputPathChanged` call `_folderScan.Reset()` / `.ExitFolderMode()` / `.Start(value)`. The `internal LastFolderScan` seam forwards to `_folderScan.LastScan` — it is used 40 times across three test files and cannot change shape.

- [x] **Step 3: Build and run both suites** → **PASS**, unchanged totals.

Guards: all of `CreatorViewModelFolderModeTests.cs` (including Task 7's two new tests), all of `CreatorViewModelDetectedSetsTests.cs`, and Task 8's isolation test.

- [x] **Step 4: Commit**

```bash
git add ReScene.App.Core/ViewModels/Creation/FolderScanController.cs ReScene.App.Core/ViewModels/CreatorViewModel.cs
git commit -m "refactor(creator): extract the folder-scan controller

The scan lifecycle moves behind eight setter callbacks plus explicit Reset and
ExitFolderMode entry points - required because Reset and OnInputPathChanged sit
outside the moved range yet mutate the moved fields. The mode flags and release
root move in and are re-exposed read-only for CanCreateSRR and CreateSRRAsync.

All four manual CreateSRRCommand.NotifyCanExecuteChanged calls keep firing in
the same order beside their InputStatus assignment - the gates they refresh have
no NotifyCanExecuteChangedFor backing - and ExitFolderMode still clears
IsScanning synchronously.

Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>"
```

---

### Task 10: `FileModeCreationPipeline` — the file-mode branch (drag seam)

In scope per the spec, and the lowest payoff-to-risk ratio in this plan. Its coverage is thinner than the stager's, and its central constraint is subtle.

**Files:**
- Create: `ReScene.App.Core/ViewModels/Creation/FileModeCreationPipeline.cs`
- Modify: `ReScene.App.Core/ViewModels/CreatorViewModel.cs`

**Interfaces:**
- Consumes: `ArtifactFileGenerator` (Task 3), `ITempDirectoryService`.
- Produces: `internal sealed class FileModeCreationPipeline` with a method per phase, operating on the view-model's `ObservableCollection<StoredFileItem> StoredFiles` passed by reference.

- [x] **Step 1: Move the file-mode phases**

Move the file-mode branch of `CreateSRRAsync`, plus `AutoScanReleaseFiles`, `CreateSRSForSamplesAsync`, `CreateVobsubSRRsAsync` and `StoreFixRARFile`.

**`StoredFiles` must continue to be appended to INCREMENTALLY during the run**, while `IsCreating == true`. Generation order is storage order, live, in the bound DataGrid. Batching the appends into a single add at the end would produce an identical SRR and would still pass `CreateSRR_PassesStoredFilesToLibInCollectionOrder` — while changing what the user sees. The appends also stay on the awaiting continuation and must **not** be moved behind `_uiDispatcher.Post`, which would reorder them relative to the posted progress updates.

- [x] **Step 2: Build and run both suites** → **PASS**, unchanged totals.

Guards: `FileModeCreate_StillCallsCreateFromSFVAsync_Regression`, `CreateSRR_PassesStoredFilesToLibInCollectionOrder`, `CreateSRR_CollidingStoredNames_LogsWarning`, `CreateSRR_BackslashAndSlashName_TreatedAsOneEntry`, `CreateSRR_MaterializesPlaceholders_InListOrder`, `CreateSRR_RetryAfterFailure_RematerializesPlaceholders`.

- [x] **Step 3: Measure the result**

Run: `wc -l ReScene.App.Core/ViewModels/CreatorViewModel.cs`
Expected: roughly 800 lines, down from 2,295. If it is materially larger, record which phase did not extract and why in the commit message rather than forcing it.

- [x] **Step 4: Commit**

```bash
git add ReScene.App.Core/ViewModels/Creation/FileModeCreationPipeline.cs ReScene.App.Core/ViewModels/CreatorViewModel.cs
git commit -m "refactor(creator): extract the file-mode creation pipeline

The file-mode branch of CreateSRRAsync plus auto-scan, create-time SRS/vobsub
generation and fix-RAR storage move into FileModeCreationPipeline.

StoredFiles is still appended to incrementally during the run rather than
batched at the end: generation order is storage order, live, in the bound grid.
Batching would produce an identical SRR and still pass the collection-order
test while changing what the user sees. The appends stay on the awaiting
continuation, never behind a dispatcher post.

Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>"
```

---

## After this plan

1. Full solution verification: `dotnet build ReScene.Manager.slnx -c Release && dotnet test ReScene.Manager.slnx -c Release --no-build` — 0 warnings, 0 errors, all suites green.
2. Add an `[Unreleased] / ### Changed` entry to the root `CHANGELOG.md` noting the internal restructuring and that behavior is unchanged.
3. `ReconstructorViewModel` is the remaining target and gets its own plan.

---

## Outcome

All ten tasks implemented, each reviewed by Codex before commit. `CreatorViewModel` went from
2,295 lines to 1,035 — short of the ~800 the plan projected for Task 10; the remainder is
observable properties, commands and stored-file editing, which have no seam left to move behind.
Release-configuration verification across the whole solution: 4,458 tests pass, 0 warnings.

Five things the plan did not anticipate, recorded because they cost real time:

1. **The Create-gate notification was entirely untested.** Probing the extraction by making each
   `FolderScanController` hook inert found seven of eight caught and `NotifyCanExecuteChanged`
   caught by nothing. `CanExecute(null)` re-evaluates the predicate on demand, so every existing
   assertion of that shape is structurally incapable of detecting a missing notification. Four
   new tests record the gate at each `CanExecuteChanged` and assert the value at the final
   re-query.
2. **Task 10's first cut changed behaviour.** Passing the file-mode options as a value record
   snapshotted state the original read at each phase boundary, so a toggle cleared mid-run would
   have been ignored and an output path edited mid-run would have sent the build to the old
   destination. The transcription diff could not catch it: `Property` -> `inputs.Property` is
   structurally a rename and semantically a different read time.
3. **Two "teeth checks" were worthless when first run.** One compared a tuple of `string[]` with
   a single `Assert.Equal`, which compares arrays by reference — the test could only ever fail,
   so its apparent mutation-sensitivity proved nothing. Another used an ungated scanner whose
   completion could run inline before the property setter returned. Both were caught by
   re-running green *before* trusting any mutation result; that ordering is now the rule.
4. **A mutation-probe script's `git checkout` destroyed uncommitted work twice.** Mutations are
   now reverted by inverse substitution, and extraction work is committed before probing.
5. **Mechanical renames need guarding.** A `StoredFiles` -> `storedFiles` rename collided with a
   local `List<StoredFileEntry>` of the same name, which would have silently written an SRR with
   no stored files; another pass rewrote identifiers inside prose comments. The generator now
   skips comment lines, and every moved body is diffed back against the original.
