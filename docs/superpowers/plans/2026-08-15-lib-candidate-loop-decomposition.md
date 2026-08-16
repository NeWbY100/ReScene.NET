# ReScene.Lib candidate-loop decomposition — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Decompose `Manager.TryProcessCommandLinesAsync` from 656 lines at nesting depth 7 into a ~180-line loop over eight named collaborators, removing the comment-enforced duplication between its twin verification paths without changing any observable behavior.

**Architecture:** The method stays a single `for` loop over candidates; each phase becomes a private method or small type taking an immutable per-candidate context record. The only *unification* is the shared verification core, which takes a lazy volume factory so the legacy path's in-gate volume discovery and re-patching do not run when the gate is off. The producer handle is extracted last because it is the only seam touching the producer-observation invariant.

**Tech Stack:** C# / .NET (multi-targets `net8.0` and `net10.0`), xUnit 2.9.3, `FakeRunner`/`AssemblyTestHost` in-repo harness, CliWrap (production only — never exercised by these tests).

**Spec:** `docs/superpowers/specs/2026-08-15-hotspot-decomposition-design.md` (§1 and the Constraints, Sequencing and Testing sections)

## Global Constraints

- **Repository:** all work happens in the submodule `E:\Projects\ReScene.Manager\ReScene.Lib`, on branch `fix/analysis-issues`. Do **not** commit in the parent repo; the gitlink bump is a separate step after this plan completes.
- **Multi-target:** the library targets `net8.0;net10.0` and the test project now runs **both**. New code must compile on net8.0 — notably `System.Threading.Lock` does not exist there (use a plain `object` monitor); `IDE0330` is silenced repo-wide for this reason.
- **Analyzers:** `EnableNETAnalyzers`, `AnalysisLevel=latest-All`, `EnforceCodeStyleInBuild`. **Zero build warnings** is the standard; the tree is currently clean.
- **`CA2007` is enforced:** every `await` in library code needs `.ConfigureAwait(false)`.
- **One top-level type per file**, file named after the type (`docs/coding-guidelines.md`). Nested types stay in their parent's file. New collaborator types are `internal`.
- **Acronym casing:** `SRR, SRS, SRST, SRSF, RAR, MP3, MP4, MKV, ASF, WMV, EBML, OSO` are ALL-CAPS in identifiers. `Flac`, `Riff`, `Vob` stay PascalCase.
- **Public API snapshot:** `ReScene.Tests/PublicApi.ReScene.approved.txt` locks the public surface. Every type added by this plan is `internal` or `private`, so the snapshot must **not** change. If `PublicApiSnapshotTests` fails, something was accidentally made public — fix the accessibility, do not regenerate the baseline.
- **Behavior-preserving:** no task in this plan may change observable behavior — not log text, not log ordering, not progress-event count or order, not on-disk retention, not exception propagation. The two known asymmetries are fixed in a *separate* follow-up, not here.
- **Producer-observation invariant** (`Manager.cs:658-673`): no finalization, deletion, or next-candidate launch may happen while a launched process's task is unobserved. Quiet observation for cleanup/catch paths; plain (fault-propagating) `await` for the win path and the assembly retry.
- **Test command:** `dotnet test ReScene.Tests/ReScene.Tests.csproj` from the submodule root runs both TFMs. A task is done only when **both** legs pass.

---

### Task 1: Characterization test — legacy complete-all-volumes verification

The legacy CAV verification block (`Manager.cs:1399-1438`) is executed by **no existing test**. Task 5 refactors it, so it must be pinned first. No harness change is needed: `Hashes` and `ExpectedVolumeCrcs` are public mutable collections on `BruteForceOptions`, and `host.Options(fixture: null, …)` leaves `SRRFilePath` null so `_useAssembly` stays `false` (`Manager.cs:431`).

**Files:**
- Modify: `ReScene.Tests/ManagerProducerLifecycleTests.cs` (append two tests before the closing brace)

**Interfaces:**
- Consumes: `AssemblyTestHost` (`AssemblyTestHost.cs`), `FakeRunner`, and this file's existing private helpers `CarrierBytes`, `TriggerBytes`, `CarrierCrc()`, `SecondVolumePath(string)`, `NewHost()`, `WithTimeoutAsync(...)`.
- Produces: nothing consumed by later tasks; these are pure regression pins.

- [x] **Step 1: Write the two failing tests**

Append to `ReScene.Tests/ManagerProducerLifecycleTests.cs`, immediately before the class's closing brace:

```csharp
    /// <summary>
    /// The CRC32 <see cref="HashCalculator"/> reports for <paramref name="bytes"/>, computed via a
    /// disposable scratch file — the exact production code path, not a re-derivation.
    /// </summary>
    private string CrcOf(byte[] bytes)
    {
        string scratch = Path.Combine(TempDir, $"scratch-{Guid.NewGuid():N}.bin");
        File.WriteAllBytes(scratch, bytes);
        return HashCalculator.Calculate(HashType.CRC32, scratch);
    }

    // ---- Legacy (non-assembly) complete-all-volumes per-volume verification ----
    // Reaching this block needs _useAssembly == false (no SRRFilePath) AND CompleteAllVolumes AND a
    // non-empty ExpectedVolumeCrcs. AssemblyTestHost.Options only fills ExpectedVolumeCrcs from a
    // fixture, and every fixture supplies an SRR path (which engages assembly instead) — so these
    // populate the public collections directly. Before these tests, Manager.cs:1400-1438 was
    // executed by nothing in the suite.

    [Fact]
    public async Task LegacyCav_AllVolumeCrcsMatch_IsAMatch()
    {
        using AssemblyTestHost host = NewHost();
        BruteForceOptions options = host.Options(fixture: null, completeAllVolumes: true,
            originalRarFileNamesOverride: ["t.rar", "t.r00"]);
        options.Hashes.Add(CarrierCrc());
        options.ExpectedVolumeCrcs["t.rar"] = CarrierCrc();
        options.ExpectedVolumeCrcs["t.r00"] = CrcOf(TriggerBytes);

        host.Runner.OnLaunch = l =>
        {
            File.WriteAllBytes(l.OutputFilePath, CarrierBytes);
            File.WriteAllBytes(SecondVolumePath(l.OutputFilePath), TriggerBytes);
            l.Exit.TrySetResult(0);
        };

        BruteForceRunResult result = await WithTimeoutAsync(
            host.Manager.BruteForceRARVersionAsync(options), "the legacy CAV run to finish");

        Assert.True(result.Success);
        Assert.DoesNotContain(host.Log.Entries, e => e.Message.Contains("CRC mismatch", StringComparison.Ordinal));
        Assert.DoesNotContain(host.Log.Entries, e => e.Message.Contains("volume(s), expected", StringComparison.Ordinal));
    }

    [Fact]
    public async Task LegacyCav_SecondVolumeCrcMismatch_IsNoMatch_AndLogsTheMismatch()
    {
        // Volume 1's CRC stays correct so the first-volume gate still matches; only the SECOND
        // volume's expected CRC is wrong, so the per-volume block is the sole thing that can
        // reject this candidate. Pins both the rejection and the exact log wording.
        using AssemblyTestHost host = NewHost();
        BruteForceOptions options = host.Options(fixture: null, completeAllVolumes: true,
            originalRarFileNamesOverride: ["t.rar", "t.r00"]);
        options.Hashes.Add(CarrierCrc());
        options.ExpectedVolumeCrcs["t.rar"] = CarrierCrc();
        options.ExpectedVolumeCrcs["t.r00"] = "ffffffff"; // deliberately wrong

        host.Runner.OnLaunch = l =>
        {
            File.WriteAllBytes(l.OutputFilePath, CarrierBytes);
            File.WriteAllBytes(SecondVolumePath(l.OutputFilePath), TriggerBytes);
            l.Exit.TrySetResult(0);
        };

        BruteForceRunResult result = await WithTimeoutAsync(
            host.Manager.BruteForceRARVersionAsync(options), "the legacy CAV run to finish");

        Assert.False(result.Success);
        Assert.Contains(host.Log.Entries, e =>
            e.Message.Contains("first volume matched but", StringComparison.Ordinal)
            && e.Message.Contains("CRC mismatch", StringComparison.Ordinal));
    }
```

- [x] **Step 2: Run the tests to verify they pass against current behavior**

Run: `dotnet test ReScene.Tests/ReScene.Tests.csproj --filter "FullyQualifiedName~LegacyCav"`

Expected: **PASS on both TFMs.** These are characterization tests — they pin behavior that already exists, so passing immediately is correct here and is the whole point.

If either FAILS, stop and investigate: it means the block behaves differently from what its code reads like, which is exactly the kind of surprise this task exists to surface. Do not "fix" the test to match; report the discrepancy.

- [x] **Step 3: Prove the tests have teeth**

Temporarily change `Manager.cs:1400` from
`if (options.RAROptions.CompleteAllVolumes && expectedInOrder.Count > 0)` to `if (false)`.

Run: `dotnet test ReScene.Tests/ReScene.Tests.csproj --filter "FullyQualifiedName~LegacyCav"`

Expected: `LegacyCav_SecondVolumeCrcMismatch_IsNoMatch_AndLogsTheMismatch` **FAILS** (the run now reports success). This proves the test reaches the block. Revert the edit and re-run to confirm both pass again.

- [x] **Step 4: Commit**

```bash
git add ReScene.Tests/ManagerProducerLifecycleTests.cs
git commit -m "test(lib): pin the legacy complete-all-volumes verification path

Manager.cs:1400-1438 was executed by no test: reaching it needs no SRR path
(so assembly stays disengaged) plus CompleteAllVolumes plus a non-empty
ExpectedVolumeCrcs, and the harness only populates that from a fixture whose
SRR path engages assembly instead. Populates the public collections directly.
Verified to have teeth by stubbing the gate to false.

Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>"
```

---

### Task 2: Characterization test — legacy duplicate-hash retention arm

`Manager.cs:1244-1249` (`DeleteDuplicateCRCFiles && isDuplicateHash`) is exercised only on the assembly side. Task 6 moves this code, so pin it first.

**Files:**
- Modify: `ReScene.Tests/ManagerProducerLifecycleTests.cs`

**Interfaces:**
- Consumes: same helpers as Task 1, plus `AssemblyTestHost.AddSecondVersion()`.
- Produces: nothing consumed later.

- [x] **Step 1: Write the failing test**

Append to the same class:

```csharp
    [Fact]
    public async Task Legacy_DuplicateHashAcrossCandidates_SecondCarrierDeleted_WhenDeleteDuplicatesSet()
    {
        // Two versions produce byte-identical non-matching carriers. The first records the hash;
        // the second sees it already in fileHashes (isDuplicateHash) and — with
        // DeleteDuplicateCRCFiles set and DeleteRARFiles NOT set — must delete its own carrier
        // while the first candidate's file stays. Pins the legacy duplicate arm, which is
        // otherwise covered only on the assembly side.
        using AssemblyTestHost host = NewHost();
        host.AddSecondVersion();
        BruteForceOptions options = host.Options(fixture: null, completeAllVolumes: false,
            deleteRarFiles: false, deleteDuplicates: true);
        options.Hashes.Add("ffffffff"); // never matches, so every candidate is a mismatch

        List<string> written = [];
        host.Runner.OnLaunch = l =>
        {
            File.WriteAllBytes(l.OutputFilePath, CarrierBytes); // identical bytes => duplicate hash
            written.Add(l.OutputFilePath);
            l.Exit.TrySetResult(0);
        };

        BruteForceRunResult result = await WithTimeoutAsync(
            host.Manager.BruteForceRARVersionAsync(options), "the duplicate-hash run to finish");

        Assert.False(result.Success);
        Assert.Equal(2, written.Count);
        Assert.True(File.Exists(written[0]), "the first candidate's carrier is not a duplicate and must be kept");
        Assert.False(File.Exists(written[1]), "the second candidate's carrier is a duplicate and must be deleted");
    }
```

- [x] **Step 2: Run the test**

Run: `dotnet test ReScene.Tests/ReScene.Tests.csproj --filter "FullyQualifiedName~Legacy_DuplicateHashAcrossCandidates"`

Expected: **PASS on both TFMs** (characterization).

If it fails because both files still exist, the duplicate arm is not reached — read `Manager.cs:1227-1253` and adjust the *test setup* (not the assertion) until the arm is genuinely exercised, then re-verify with Step 3.

- [x] **Step 3: Prove it has teeth**

Temporarily change the `DeleteDuplicateCRCFiles` condition at `Manager.cs:1244` to `if (false)`. Re-run: the test must **FAIL** on the second assertion. Revert and confirm PASS.

- [x] **Step 4: Commit**

```bash
git add ReScene.Tests/ManagerProducerLifecycleTests.cs
git commit -m "test(lib): pin the legacy duplicate-hash retention arm

Manager.cs:1244-1249 was exercised only through the assembly path's
ApplyMismatchRetention. Two versions producing byte-identical carriers drive
the legacy arm directly.

Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>"
```

---

### Task 3: `CandidateContext` — the per-candidate identity record

**Files:**
- Create: `ReScene/Core/CandidateContext.cs`
- Modify: `ReScene/Core/Manager.cs:892-942` (compose the record), and every later reference within the loop

**Interfaces:**
- Consumes: nothing.
- Produces: `internal sealed record CandidateContext` with the members listed below. Tasks 4-9 all take `CandidateContext ctx` as their first parameter.

- [x] **Step 1: Create the record**

Create `ReScene/Core/CandidateContext.cs`:

```csharp
namespace ReScene.Core;

/// <summary>
/// One brute-force candidate's immutable identity and composed command line: the version under
/// test, the paths it reads and writes, and the exact arguments it will run with. Composed once at
/// the top of a candidate iteration and passed to every phase, so no phase recomputes a path or a
/// joined argument string — notably the assembly output directory, which the pre-refactor code
/// derived identically in two places.
/// </summary>
/// <param name="Version">The rar version under test (e.g. 560).</param>
/// <param name="VersionDirectoryPath">The version's installation directory.</param>
/// <param name="VersionDirectoryName">That directory's leaf name, used in log lines and output names.</param>
/// <param name="RarExeFilePath">The resolved rar executable for this version.</param>
/// <param name="InputFilesDir">The prepared input directory the candidate packs from.</param>
/// <param name="RarOutputDir">The run's output subdirectory that receives produced archives.</param>
/// <param name="RarFilePath">This candidate's intended output archive path.</param>
/// <param name="CandidateSlug">The output archive's file name without extension.</param>
/// <param name="AssemblyDir">Where SRR-guided assembly writes this candidate's assembled set.</param>
/// <param name="CommandLineArguments">The unfiltered switch combination this candidate represents.</param>
/// <param name="FilteredArguments">Those switches after version/format filtering.</param>
/// <param name="DisplayArguments">The filtered switches joined for display and log lines.</param>
/// <param name="FinalArguments">The actual argument list, including engine-added switches.</param>
/// <param name="ExecutedArguments">Those final arguments joined and quoted for a runnable command line.</param>
/// <param name="InputTail">Explicit input operands, or <see langword="null"/> for rar's own mask.</param>
/// <param name="InputFileArguments">The input tail rendered for progress events; empty for a mask run.</param>
/// <param name="TotalProgressSize">The run's progress denominator, carried so progress rows need no extra parameter.</param>
/// <param name="BruteForceStartDateTime">The run's start instant, carried for the same reason.</param>
internal sealed record CandidateContext(
    int Version,
    string VersionDirectoryPath,
    string VersionDirectoryName,
    string RarExeFilePath,
    string InputFilesDir,
    string RarOutputDir,
    string RarFilePath,
    string CandidateSlug,
    string AssemblyDir,
    RARCommandLineArgument[] CommandLineArguments,
    List<string> FilteredArguments,
    string DisplayArguments,
    List<string> FinalArguments,
    string ExecutedArguments,
    IReadOnlyList<string>? InputTail,
    string InputFileArguments,
    int TotalProgressSize,
    DateTime BruteForceStartDateTime);
```

`TotalProgressSize` and `BruteForceStartDateTime` are method parameters of
`TryProcessCommandLinesAsync` (confirmed at `Manager.cs:865,867`), not fields — carrying them on the
context is what lets Task 4's row factory take no extra arguments. Do **not** promote them to
`Manager` fields; the loop must stay free of new mutable run state.

- [x] **Step 2: Build to verify it compiles on both TFMs**

Run: `dotnet build ReScene/ReScene.csproj`
Expected: succeeds, 0 warnings. (`RARCommandLineArgument` lives in `ReScene.Core.Diagnostics`; add the `using` if the build reports it missing.)

- [x] **Step 3: Compose the record in the loop and switch every reference to it**

In `Manager.cs`, after the existing `ComposeInputFileArguments` call and `BuildFinalArguments` call (~line 941), introduce:

```csharp
            var ctx = new CandidateContext(
                version, rarVersionDirectoryPath, rarVersionDirectoryName, rarExeFilePath,
                inputFilesDir, rarOutputDir, rarFilePath,
                Path.GetFileNameWithoutExtension(rarFilePath),
                Path.Combine(rarOutputDir, $"assembled-{Path.GetFileNameWithoutExtension(rarFilePath)}"),
                commandLineArguments, filteredArguments, displayArguments,
                finalArguments, executedArguments, inputTail, inputFileArguments,
                totalProgressSize, bruteForceStartDateTime);
```

Then replace the loop body's uses of the individual locals with `ctx.` members. Delete `candidateSlug` (was line 1105) and both `assemblyDir` computations (were 1113 and 1287), using `ctx.CandidateSlug` and `ctx.AssemblyDir`.

**Do not move** the `File.WriteAllText` list-file materialization (929-936) or the RAR 6.x timestamp skip (906-915) — their position in the candidate sequence is documented and load-bearing. **Do not** move the `_cts.IsCancellationRequested` early return (893-896); it must stay before the record is composed.

- [x] **Step 4: Run the full suite**

Run: `dotnet test ReScene.Tests/ReScene.Tests.csproj`
Expected: **PASS on both TFMs**, same totals as before this task, 0 warnings.

- [x] **Step 5: Commit**

```bash
git add ReScene/Core/CandidateContext.cs ReScene/Core/Manager.cs
git commit -m "refactor(core): introduce CandidateContext for the candidate loop

Bundles one candidate's identity, paths and composed command line into an
immutable record passed to every phase. Removes the duplicated assembly-dir
computation and stops each phase re-deriving the candidate slug. Pure
composition - the list-file write, the RAR6 timestamp skip and the pre-try
cancellation return keep their exact positions in the sequence.

Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>"
```

---

### Task 4: `NewRow` — the progress-event factory

**Files:**
- Modify: `ReScene/Core/Manager.cs` (add the factory; replace 5 initializers + the one inside `FireAssemblyErrorRow`)

**Interfaces:**
- Consumes: `CandidateContext` (Task 3).
- Produces: `private BruteForceProgressEventArgs NewRow(CandidateContext ctx, string releaseDirectoryPath, int currentProgress, bool combinationFailed = false)`.

- [x] **Step 1: Add the factory**

Add near `FireBruteForceProgress` in `Manager.cs`:

```csharp
    /// <summary>
    /// Builds one candidate's progress row. Every field is derived from the candidate context and
    /// the run's counters, so the five emission sites cannot drift apart in what they report.
    /// Callers remain responsible for WHEN they fire and for whether they have already incremented
    /// <paramref name="currentProgress"/> — that ordering differs per site and is load-bearing.
    /// </summary>
    private BruteForceProgressEventArgs NewRow(
        CandidateContext ctx, string releaseDirectoryPath, int currentProgress, bool combinationFailed = false)
        => new(releaseDirectoryPath, ctx.VersionDirectoryPath, ctx.DisplayArguments,
            ctx.TotalProgressSize, currentProgress, ctx.BruteForceStartDateTime)
        {
            PhaseDescription = "Phase 2: Full RAR Creation",
            InputDirectoryPath = ctx.InputFilesDir,
            OutputFilePath = ctx.RarFilePath,
            ExecutedArguments = ctx.ExecutedArguments,
            InputFileArguments = ctx.InputFileArguments,
            CombinationFailed = combinationFailed
        };
```

`releaseDirectoryPath` is passed explicitly (as `options.ReleaseDirectoryPath`) rather than read from
the `BruteForceOptions` property. They are the same object today only by convention — the spec's risk
list notes that `RARCompressDirectoryAsync` reads the *property* while the CAV branch reads the
*parameter*, and this factory must not deepen that coupling.

- [x] **Step 2: Replace the six sites**

Replace the initializers at (pre-refactor lines) 951-958, 962-969, 1042-1049, 1080-1088, 1497-1505, and the one inside `FireAssemblyErrorRow` (1548-1557) with `NewRow(...)` calls.

**Preserve each site's increment/fire ordering exactly:**
- 950: increments **before** firing.
- 962: fires **before** incrementing.
- 1040-1042: increments, **then** fires.
- 1080 and 1497: pass `combinationFailed: true`.

Site 1497 lives inside the generic `catch`. Routing its row through `NewRow` is the **only** change
this plan makes to either catch block. Both catch bodies stay inline for the whole plan: extracting
them would need `ref` locals (`combinationCounted` is written in the `try` and read in the `catch`),
and `throw;` must stay lexically inside `catch (OperationCanceledException)` to preserve rethrow
semantics and the stack trace.

- [x] **Step 3: Run the full suite**

Run: `dotnet test ReScene.Tests/ReScene.Tests.csproj`
Expected: **PASS on both TFMs.** `ManagerHelpersTests` asserts exact `ExecutedArguments` strings and four lifecycle tests assert `Assert.Single(progressEvents, e => e.CombinationFailed)` — if any fails, a site's ordering or its `combinationFailed` flag was changed.

- [x] **Step 4: Commit**

```bash
git add ReScene/Core/Manager.cs
git commit -m "refactor(core): single factory for candidate progress rows

Five near-identical BruteForceProgressEventArgs initializers plus the one in
FireAssemblyErrorRow now derive every field from CandidateContext. Each site
keeps its own increment-vs-fire ordering, which differs deliberately.

Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>"
```

---

### Task 5: `VerifyVolumeSet` — unify the shared verification core

This is the task the plan exists for. It removes the duplication the source itself flags as
"the gate is EXACTLY the legacy block's own below".

**Files:**
- Modify: `ReScene/Core/Manager.cs` (add the helper; rewrite both call sites)

**Interfaces:**
- Consumes: `CandidateContext` (Task 3).
- Produces: `private bool VerifyVolumeSet(Func<IReadOnlyList<string>> volumesFactory, BruteForceOptions options, string label)` — returns `true` when the set verifies **or** when verification is not configured; `false` when it mismatches (the caller then applies its own retention and continues).

- [x] **Step 1: Add the helper**

```csharp
    /// <summary>
    /// The per-volume CRC gate shared by the assembly and legacy win paths. Returns
    /// <see langword="true"/> when the whole produced set matches, or when verification is not
    /// configured (no CompleteAllVolumes, or no CRC map — the first-volume hash was the whole gate).
    /// Returns <see langword="false"/> after logging the mismatch; the caller applies its own
    /// retention policy, which differs between the two paths.
    /// </summary>
    /// <param name="volumesFactory">
    /// Produces the ordered volume paths to hash. Invoked ONLY when the gate is on: the legacy
    /// caller discovers volumes and re-patches them inside this callback, and neither may happen
    /// when verification is disabled.
    /// </param>
    /// <param name="options">The run's options, carrying the expected per-volume CRCs.</param>
    /// <param name="label">"{versionDirectoryName} / {displayArguments}", used in the log line.</param>
    private bool VerifyVolumeSet(Func<IReadOnlyList<string>> volumesFactory, BruteForceOptions options, string label)
    {
        IReadOnlyList<(string Name, string Crc)> expectedInOrder = BuildExpectedInOrder(options);
        if (!options.RAROptions.CompleteAllVolumes || expectedInOrder.Count == 0)
        {
            return true;
        }

        List<string> volumes = [.. volumesFactory()];
        var producedCrcs = volumes
            .Select(v => HashCalculator.Calculate(HashType.CRC32, v))
            .ToList();

        VolumeMatchResult verify = VolumeMatchEvaluator.Evaluate(producedCrcs, expectedInOrder);
        if (verify.AllMatch)
        {
            return true;
        }

        VolumeMatch? m = verify.FirstMismatch;
        string detail = verify.CountMismatch
            ? $"produced {producedCrcs.Count} volume(s), expected {expectedInOrder.Count}"
            : $"{m?.ExpectedName} CRC mismatch (expected {m?.ExpectedCrc}, got {m?.ActualCrc})";
        _logger.Information(this, $"{label}: first volume matched but {detail} — continuing", LogTarget.Phase2);
        return false;
    }
```

- [x] **Step 2: Rewrite the assembly call site**

Replace pre-refactor lines 1329-1349 with:

```csharp
                if (!VerifyVolumeSet(() => assembled.WrittenPaths, options,
                        $"{ctx.VersionDirectoryName} / {ctx.DisplayArguments}"))
                {
                    ApplyMismatchRetention(ctx.AssemblyDir, actualRARFilePath, options, isDuplicateAssemblyHash);
                    continue;
                }
```

- [x] **Step 3: Rewrite the legacy call site**

Replace pre-refactor lines 1399-1438 with:

```csharp
                string? completed = null;
                if (!VerifyVolumeSet(
                        () =>
                        {
                            // Discovery and re-patching live INSIDE the gate: neither may run when
                            // verification is not configured. Patching is idempotent, so re-applying
                            // it over volume 1 here is safe.
                            completed = MatchedRARWriter.FindCreatedRARFile(ctx.RarFilePath);
                            if (completed == null)
                            {
                                return []; // deliberate count mismatch
                            }

                            if (options.RAROptions.NeedsPatching)
                            {
                                PatchRARFilesHostOS(completed, options.RAROptions);
                            }

                            return MatchedRARWriter.GetAllVolumeFiles(completed);
                        },
                        options,
                        $"{ctx.VersionDirectoryName} / {ctx.DisplayArguments}"))
                {
                    if (options.RAROptions.DeleteRARFiles && completed != null)
                    {
                        DeleteRARFileAndVolumes(completed);
                    }

                    continue;
                }
```

- [x] **Step 4: Run the full suite**

Run: `dotnet test ReScene.Tests/ReScene.Tests.csproj`
Expected: **PASS on both TFMs.** The Task 1 tests are the ones that prove the legacy site still behaves identically; `Cav_EndToEnd_ExtTimeScenario_MatchesAndVerifiesAllVolumes`, `Cav_FullVerifyMismatch_IsNoMatch_NotSuccess` and `NoCrcMap_FirstHashOnly_ParityPreserved` prove the assembly site does.

- [x] **Step 5: Commit**

```bash
git add ReScene/Core/Manager.cs
git commit -m "refactor(core): unify the per-volume verification core

The assembly and legacy win paths ran semantically identical gate, CRC
projection, evaluation and log-line code - differing only in local names and
the volume source - kept in sync only by a comment saying so. Both now call VerifyVolumeSet. The volume list arrives as a LAZY
factory because the legacy path discovers and re-patches volumes inside the
gate - an eager parameter would perform that enumeration and patching even
when verification is disabled. Retention stays per-path and unchanged.

Also dissolves the assemblyExpectedInOrder/expectedInOrder naming workaround
that CS0136 forced on the two blocks.

Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>"
```

---

### Task 6: `TryLegacyGateAsync` — extract the legacy first-volume gate

**Files:**
- Modify: `ReScene/Core/Manager.cs` (extract pre-refactor 1215-1254)

**Interfaces:**
- Consumes: `CandidateContext` (Task 3).
- Produces: `private async Task<(bool Matched, string Hash)> TryLegacyGateAsync(CandidateContext ctx, string actualRARFilePath, HashSet<string> fileHashes, Task<int>? runningProcessTask, CancellationTokenSource? processCts, BruteForceOptions options)`. Returns `Matched: false` to mean "continue to the next candidate". Task 10 collapses the two producer parameters into a single `CandidateProducer`.

- [x] **Step 1: Extract the method**

Move the body of pre-refactor lines 1215-1254 **verbatim** into a private method with the signature above, changing only local names that now come from `ctx`. It must:
- patch volume 1 only (`allVolumes: false`),
- compute `hash`, log it,
- record it in `fileHashes` **after** testing membership (duplicate detection precedes recording),
- on mismatch: observe the producer quietly **before** any deletion, apply `DeleteRARFiles` / `DeleteDuplicateCRCFiles && isDuplicateHash`, and return `(false, hash)`,
- on match: return `(true, hash)`.

- [x] **Step 2: Rewrite the call site**

```csharp
            else
            {
                (bool matched, string legacyHash) = await TryLegacyGateAsync(
                    ctx, actualRARFilePath, fileHashes, runningProcessTask, processCts, options).ConfigureAwait(false);
                if (!matched)
                {
                    continue;
                }

                hash = legacyHash;
            }
```

- [x] **Step 3: Run the full suite**

Run: `dotnet test ReScene.Tests/ReScene.Tests.csproj`
Expected: **PASS on both TFMs.** Task 2's duplicate-retention test and `QuickMismatch_ObservesProducer_BeforeNextCandidateLaunch` are the primary guards.

- [x] **Step 4: Commit**

```bash
git add ReScene/Core/Manager.cs
git commit -m "refactor(core): extract the legacy first-volume gate

Patch, hash, duplicate-detect, compare and mismatch-retention move into
TryLegacyGateAsync returning (Matched, Hash). The producer is still observed
before any deletion, and duplicate detection still precedes recording.

Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>"
```

---

### Task 7: `TryFinalizeLegacyWin` — extract the legacy win path

**Files:**
- Modify: `ReScene/Core/Manager.cs` (extract pre-refactor 1396-1459, minus the verification now in Task 5)

**Interfaces:**
- Consumes: `CandidateContext`, `VerifyVolumeSet`.
- Produces: `private CommittedMatch? TryFinalizeLegacyWin(CandidateContext ctx, string hash, string actualRARFilePath, BruteForceOptions options)` — `null` means "continue to the next candidate". Synchronous: this range contains no `await`.

- [x] **Step 1: Extract the method**

Body order must stay: verify (Task 5 call) → `LogMatchDetails` → `RenameMatchedOutput` → if incomplete, `_logger.Warning` and return `null` → otherwise build `WinningCombo` and return the `CommittedMatch`.

Note the log-before-finalize ordering here is the opposite of the assembly path's finalize-before-log. That asymmetry is deliberate and stays.

- [x] **Step 2: Rewrite the call site**

```csharp
                CommittedMatch? legacyMatch = TryFinalizeLegacyWin(ctx, hash, actualRARFilePath, options);
                if (legacyMatch is null)
                {
                    continue;
                }

                return (true, currentProgress, legacyMatch);
```

- [x] **Step 3: Run the full suite**

Run: `dotnet test ReScene.Tests/ReScene.Tests.csproj`
Expected: **PASS on both TFMs.** Task 1's match test plus `RenameMatchedOutputTests` guard this.

- [x] **Step 4: Commit**

```bash
git add ReScene/Core/Manager.cs
git commit -m "refactor(core): extract the legacy win path

Verification, match logging, rename and the incomplete-placement bail move
into TryFinalizeLegacyWin returning CommittedMatch?. Log-before-finalize
ordering is preserved (the assembly path deliberately does the reverse).

Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>"
```

---

### Task 8: `FinalizeAssemblyWinAsync` — extract the assembly win path

**Files:**
- Modify: `ReScene/Core/Manager.cs` (extract pre-refactor 1278-1394)

**Interfaces:**
- Consumes: `CandidateContext`, `VerifyVolumeSet`, and the quick gate's hoisted results.
- Produces: `private async Task<CommittedMatch?> FinalizeAssemblyWinAsync(CandidateContext ctx, SRRReconstructionResult quick, bool isDuplicateAssemblyHash, string hash, string actualRARFilePath, BruteForceOptions options, CancellationToken cancellationToken)` — `null` means "continue".

This block touches **no** producer state: the producer was already joined unconditionally at pre-refactor line 1275, before this range. That is what makes it cleanly extractable.

- [x] **Step 1: Extract the method**

Preserve in order: the CAV full-set re-assembly with its `Error`/`SourceExhausted` classification → `VerifyVolumeSet` (finalization runs **outside** that gate) → `FinalizeAssembledSet` → on incomplete, `FireAssemblyErrorRow` and return `null` → the five match log lines → carrier deletion when `DeleteRARFiles` → best-effort recursive removal of the now-empty assembly directory, keeping its `catch (IOException or UnauthorizedAccessException)` so a cleanup failure never converts a committed match into a failure.

- [x] **Step 2: Rewrite the call site**

```csharp
                CommittedMatch? assemblyMatch = await FinalizeAssemblyWinAsync(
                    ctx, quick!, isDuplicateAssemblyHash, hash, actualRARFilePath, options, _cts.Token).ConfigureAwait(false);
                if (assemblyMatch is null)
                {
                    continue;
                }

                return (true, currentProgress, assemblyMatch);
```

- [x] **Step 3: Run the full suite**

Run: `dotnet test ReScene.Tests/ReScene.Tests.csproj`
Expected: **PASS on both TFMs.** `NonCav_QuickMatch_NeverCommitsCarrierUnderOriginalName` and `Cav_QualifiedSetNames_FinalizeAndCleanupHandleSubdirectories` are the byte-level and cleanup guards; `ManagerAssemblyFinalizeTests.RetentionMatrix` covers retention.

- [x] **Step 4: Commit**

```bash
git add ReScene/Core/Manager.cs
git commit -m "refactor(core): extract the assembly win path

Full-set re-assembly, verification, finalization, match logging, carrier
deletion and empty-dir cleanup move into FinalizeAssemblyWinAsync. The block
touches no producer state - the producer is joined unconditionally before it -
so the invariant is unaffected. Finalize-before-log ordering and the
best-effort cleanup catch are preserved.

Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>"
```

---

### Task 9: `TryQuickGateAsync` — extract the SRR-guided quick gate

**Files:**
- Modify: `ReScene/Core/Manager.cs` (extract pre-refactor 1111-1214)

**Interfaces:**
- Consumes: `CandidateContext`.
- Produces:

```csharp
internal enum GateOutcome { Match, NextCandidate }
```
and
`private async Task<(GateOutcome Outcome, SRRReconstructionResult? Quick, bool IsDuplicate, string? Hash)> TryQuickGateAsync(CandidateContext ctx, string actualRARFilePath, HashSet<string> fileHashes, Task<int>? runningProcessTask, CancellationTokenSource? processCts, BruteForceOptions options)`

The hoisted `quick` and `isDuplicateAssemblyHash` locals become return values — which is exactly what the pre-refactor comment at 1106-1108 wished for.

- [x] **Step 1: Add the enum**

Create `ReScene/Core/GateOutcome.cs` (one top-level type per file):

```csharp
namespace ReScene.Core;

/// <summary>What a candidate's first-volume gate decided.</summary>
internal enum GateOutcome
{
    /// <summary>The first volume matched; proceed to the win path.</summary>
    Match,

    /// <summary>No match (or an unusable result); move to the next candidate.</summary>
    NextCandidate
}
```

- [x] **Step 2: Extract the method**

Critical orderings that must survive verbatim:
- `retryEligible` is sampled **before** the first `AssembleCandidateAsync`, never after.
- The retry's `await runningProcessTask` is a **plain** await — faults must propagate to the generic catch, not be quietly observed.
- Duplicate detection precedes recording in `fileHashes`.
- The pack-order diagnostic reads **both** files **before** `ApplyMismatchRetention` may delete one.
- `ObserveProducerQuietlyAsync` runs **before** the retention deletion.
- `skipRetentionCleanup` is `true` only for the persistent `Error` case.

- [x] **Step 3: Rewrite the call site**

```csharp
            if (_useAssembly)
            {
                (GateOutcome outcome, SRRReconstructionResult? quickResult, bool duplicate, string? quickHash) =
                    await TryQuickGateAsync(ctx, actualRARFilePath, fileHashes,
                        runningProcessTask, processCts, options).ConfigureAwait(false);
                if (outcome == GateOutcome.NextCandidate)
                {
                    continue;
                }

                quick = quickResult;
                isDuplicateAssemblyHash = duplicate;
                hash = quickHash!;
            }
```

- [x] **Step 4: Run the full suite**

Run: `dotnet test ReScene.Tests/ReScene.Tests.csproj`
Expected: **PASS on both TFMs.** `Cav_IncompleteSnapshot_RetriesOnceWithFreshSource`, `Cav_ProducerCompletesDuringAttempt_RetryStillTriggers`, the pack-order once-per-run tests, and the `RetentionMatrix` rows are the guards.

- [x] **Step 5: Commit**

```bash
git add ReScene/Core/GateOutcome.cs ReScene/Core/Manager.cs
git commit -m "refactor(core): extract the SRR-guided quick gate

Assembly attempt, incomplete-snapshot retry, classification, pack-order
diagnostic and mismatch retention move into TryQuickGateAsync. The hoisted
quick/isDuplicateAssemblyHash locals become return values. Retry eligibility
is still sampled before the attempt, the retry await is still plain (faults
propagate), and the pack-order diagnostic still reads both files before
retention may delete one.

Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>"
```

---

### Task 10: `CandidateProducer` — the producer handle

Last, because it is the only seam touching the producer-observation invariant.

**Files:**
- Create: `ReScene/Core/CandidateProducer.cs`
- Modify: `ReScene/Core/Manager.cs` (launch block and the nine touch points)

**Interfaces:**
- Consumes: `CandidateContext`.
- Produces: `internal sealed class CandidateProducer : IDisposable` with the four operations below. Tasks 6 and 9's two `Task<int>? / CancellationTokenSource?` parameters collapse into one `CandidateProducer` parameter.

- [x] **Step 1: Create the type**

```csharp
namespace ReScene.Core;

/// <summary>
/// One candidate's launched rar process: the task, its linked cancellation source, and the exit
/// code once known. Exists to make the producer-observation invariant — no finalization, deletion,
/// or next-candidate launch while a launched task is unobserved — expressible as named operations
/// rather than as repeated inline await patterns.
/// </summary>
/// <remarks>
/// The two observation modes are NOT interchangeable. <see cref="ObserveQuietlyAsync"/> swallows
/// faults and is for cleanup, mismatch and catch paths. <see cref="JoinForWinAsync"/> awaits
/// plainly so a fault propagates to the caller's generic catch and becomes an error row — a
/// winning candidate must never be committed on top of a faulted producer.
/// </remarks>
internal sealed class CandidateProducer : IDisposable
{
    /// <summary>The launched task, or null when this candidate used the early-termination path.</summary>
    public Task<int>? Task { get; init; }

    /// <summary>The linked source cancelling <see cref="Task"/>, or null.</summary>
    public CancellationTokenSource? Cts { get; init; }

    /// <summary>The exit code once known, from launch, quiet observation, or the assembly retry.</summary>
    public int? CompletedExitCode { get; set; }

    /// <summary>
    /// Captures whether an assembly attempt may retry on an incomplete snapshot. MUST be called
    /// BEFORE the attempt: sampling afterwards always observes a completed producer and silently
    /// loses the retry, which is the real race the check exists to catch.
    /// </summary>
    public bool SampleRetryEligibility() => Task is { IsCompleted: false };

    public void Dispose() => Cts?.Dispose();
}
```

`AwaitLaunchOrSecondVolumeAsync`, `ObserveQuietlyAsync` and `JoinForWinAsync` are added as methods in Step 2, moving the existing bodies verbatim.

- [x] **Step 2: Move the four operations**

- `AwaitLaunchOrSecondVolumeAsync` — the `Task.WhenAny(runningProcessTask, monitorTask)` plus the immediate `if (runningProcessTask.IsFaulted) await runningProcessTask` rethrow (pre-refactor 1009-1027). The rethrow must stay, so a faulted producer still reaches the generic catch.
- `ObserveQuietlyAsync(bool cancelFirst)` — wraps the existing `ObserveProducerQuietlyAsync`.
- `JoinForWinAsync()` — the **unconditional** plain await when the task is non-null (pre-refactor 1258-1276). Never gate this on `IsCompleted`; `LateFault_AlreadyCompletedAtWinningCheck_IsFailedCombination_NeverAMatch` documents that doing so finalizes a false match.
- `SampleRetryEligibility()` — as above.

**Dispose only in the existing `finally`.** Disposing anywhere else makes the later `Cancel()` inside `ObserveProducerQuietlyAsync` (line 687, which sits outside its own `try`) throw `ObjectDisposedException` and propagate.

- [x] **Step 3: Run the full suite**

Run: `dotnet test ReScene.Tests/ReScene.Tests.csproj`
Expected: **PASS on both TFMs.** All nine `ManagerProducerLifecycleTests` are the guards; `AssertBlockedAsync` polls for 250 ms to prove the invariant genuinely blocks rather than merely not having raced yet.

- [x] **Step 4: Verify the loop hit its size target**

Run: `awk '/private async Task<\(bool|int|CommittedMatch\)> TryProcessCommandLinesAsync/,/^    }$/' ReScene/Core/Manager.cs | wc -l`
Expected: roughly 180 lines (down from 656), nesting no deeper than 4 levels. If it is materially larger, note which phase did not extract and why in the commit message rather than forcing it.

- [x] **Step 5: Commit**

```bash
git add ReScene/Core/CandidateProducer.cs ReScene/Core/Manager.cs
git commit -m "refactor(core): extract the candidate producer handle

Task, linked CTS and exit code move into CandidateProducer with four named
operations: AwaitLaunchOrSecondVolumeAsync (WhenAny + faulted rethrow),
ObserveQuietlyAsync (cleanup/catch, swallows faults), JoinForWinAsync
(unconditional plain await so a fault becomes an error row, never a false
match) and SampleRetryEligibility (captured BEFORE the assembly attempt).
Disposal stays in the existing finally so the later cancel cannot throw.

Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>"
```

---

## After this plan

1. Run the parent repo's full solution build and test to confirm the app still builds against the changed lib:
   `cd E:\Projects\ReScene.Manager && dotnet build ReScene.Manager.slnx -c Release && dotnet test ReScene.Manager.slnx -c Release --no-build`
2. Add a `[Unreleased] / ### Changed` entry to `ReScene.Lib/CHANGELOG.md` noting the internal restructuring and that behavior is unchanged.
3. Bump the parent gitlink in a `chore: lib pointer` commit — **after** the lib commits are pushed, or CI cannot fetch the submodule SHA.
4. The two twin-path asymmetries (legacy `DeleteDuplicateCRCFiles` retention; legacy incomplete-finalization progress row) are fixed next, as their own commits with their own tests. They are **behavior changes** and must not be folded into any task above.


---

## Outcome

All ten tasks implemented, each reviewed by Codex before commit, and pushed on `fix/analysis-issues`
(`672097a` … `a6d86a7`, plus `81292f6` for a changelog heading fix).

`TryProcessCommandLinesAsync` went from **656 lines at nesting depth 7 to 334 at depth 5** — 185 of
those code lines, against a projected ~180. So the *code* is the size the plan aimed for; the raw
line count is nearly double it, because the rationale comments were preserved verbatim rather than
trimmed. Depth 5 rather than the projected 4: the remaining nesting is the loop's own
try/foreach/if structure, which no further extraction removes without inventing a collaborator per
`if`.

Three units came out of it: `CandidateContext` (the per-candidate identity record), `GateOutcome`,
and `CandidateProducer` (the producer handle carrying the observation invariant).

### What this plan established that the later two inherited

This was the first of the three targets, and the working discipline came from it:

- **Characterization tests before the extraction they guard**, as their own commits. Tasks 1 and 2
  exist purely to cover the legacy complete-all-volumes path and the duplicate-hash retention arm,
  neither of which any test touched.
- **Mandatory teeth checks.** Task 2's first attempt was worthless: the mutation script printed
  "perturbation applied" unconditionally without verifying the replacement matched, and
  `OperationProgressEventArgs` clamps its progress value, so a `+99` perturbation on a
  denominator-1 run was invisible. Every later mutation script asserts the target was found.
- **Claims that cannot be proven are documented as unproven.** The eager-token-capture behaviour in
  Task 2's sibling work could not be made to fail under mutation; the commit says so rather than
  implying coverage.

### Two things that went wrong here and shaped everything after

1. **A BOM was introduced and pushed.** Python scripts read with `utf-8-sig` and wrote with
   `utf-8-sig`, adding a BOM to `Manager.cs` and `ManagerProducerLifecycleTests.cs`. Caught by
   Codex after it had already reached the remote. Fixed by a census of every tracked `.cs` in both
   repositories, then committed and pushed separately (`a6d86a7`). Every later script writes plain
   UTF-8 and the census is part of the pre-push check.
2. **A `sed` pass corrupted prose comments** — "for a given version;" became "ctx.Version;". After
   that, every rename operated on code lines only, with comment lines passed through untouched.

### The twin-path asymmetries

Fixed after the decomposition landed, as their own commits with their own tests
(`b34b886`, `2cb0700`), exactly as the plan required: they are behaviour changes and were never
folded into a refactor commit.
