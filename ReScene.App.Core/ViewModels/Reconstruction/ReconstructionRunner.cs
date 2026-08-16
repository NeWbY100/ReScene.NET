using System.Collections.ObjectModel;
using ReScene.App.Core.Services;
using ReScene.Core;
using ReScene.SRR;

namespace ReScene.App.Core.ViewModels.Reconstruction;

/// <summary>
/// The reconstruction run's per-set work: resolving each set's embedded SFV, committing a verified
/// result out of the scratch tree, and clearing the scratch afterwards.
/// </summary>
/// <remarks>
/// <para>
/// This half holds the contract and the leaf helpers; the two loop methods follow. The classification
/// of each dependency is deliberate and is the thing most able to break a run silently:
/// </para>
/// <list type="table">
/// <item><term><paramref name="fileMover"/></term><description>STABLE - a collaborator for the object's lifetime.</description></item>
/// <item><term><paramref name="import"/></term><description>LIVE, invoked at each original read. The
/// <c>SetImportStateForTest</c> seam REPLACES the holder, and many tests then drive the run, so
/// caching it here would pin whichever instance existed when the runner was built. It is also read
/// separately per set.</description></item>
/// <item><term><paramref name="outputPath"/>, <paramref name="completeAllVolumes"/></term>
/// <description>LIVE. Both are read during relocation, after the brute-force awaits, and neither
/// control is disabled while a run is in progress.</description></item>
/// <item><term><c>_cleanupWorkFilesThisRun</c></term><description>A RUN-SCOPED snapshot, captured
/// once inside the loop after the version-scan await so a mid-run settings save cannot flip cleanup
/// behaviour between sets. It is a field rather than an accessor precisely because it must NOT be
/// re-read.</description></item>
/// <item><term><paramref name="sink"/></term><description>The bound state the loop writes back. See
/// <see cref="IRunSink"/> for why it has exactly these members.</description></item>
/// </list>
/// </remarks>
internal sealed class ReconstructionRunner(
    IBruteForceService bruteForceService,
    IFileMover fileMover,
    IAppSettingsService? settingsService,
    ReconstructionProgressTracker<ReconstructorViewModel.VersionEntry> progress,
    ObservableCollection<ReconstructorViewModel.VersionEntry> versionEntries,
    IRunSink sink,
    Func<ReconstructionImportState> import,
    Func<string> outputPath,
    Func<bool> completeAllVolumes,
    Func<Task?> lastVersionScan,
    Func<CancellationToken, Task<SharedReconstructionSettings>> buildSharedSettingsAsync,
    Action<string> log)
{
    /// <summary>
    /// The run-scoped cleanup decision, captured once after the version-scan await so a mid-run
    /// settings save cannot flip cleanup behaviour between sets.
    /// </summary>
    private bool _cleanupWorkFilesThisRun;

    /// <summary>
    /// Reads the embedded SFV bytes for a set from the imported SRR's stored files. For a single
    /// flat set (empty key) any stored .sfv matches. Otherwise a stored .sfv matches this set when
    /// either its archive-set key equals the set key (handles directory-prefixed stored names such
    /// as "DVD1\aln-re4a.sfv" → key "DVD1/aln-re4a"), OR its base name equals the set's base name
    /// (handles a flat "aln-re4a.sfv" matched to key "DVD1/aln-re4a"). Returns null when no SRR
    /// was imported or no stored .sfv matches.
    /// </summary>
    private byte[]? LoadEmbeddedSfvBytes(SRRArchiveSet set)
    {
        string? srrPath = import().SRRFilePath;
        if (string.IsNullOrWhiteSpace(srrPath) || !File.Exists(srrPath))
        {
            return null;
        }

        try
        {
            var srr = SRRFile.Load(srrPath);
            return srr.ReadStoredFile(srrPath, name => EmbeddedSfvMatchesSet(name, set));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            log($"Could not read embedded SFV for {set.Key}: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Whether a stored file is the .sfv for the given set. See <see cref="LoadEmbeddedSfvBytes"/>
    /// for the matching rules. Shared with the embedded-SFV resolution test so both use one predicate.
    /// </summary>
    internal static bool EmbeddedSfvMatchesSet(string storedName, SRRArchiveSet set)
    {
        if (!storedName.EndsWith(".sfv", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        // Single flat set: any stored .sfv is its SFV.
        if (string.IsNullOrEmpty(set.Key))
        {
            return true;
        }

        // Key match: handles a directory-prefixed stored name (e.g. "DVD1\aln-re4a.sfv").
        if (RARVolumeIdentifier.GetArchiveSetKey(storedName).Equals(set.Key, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // Base-name match: handles a flat stored name (e.g. "aln-re4a.sfv") whose set key carries a
        // directory prefix. The set's base name is the last '/'-segment of its key.
        string setBaseName = set.Key[(set.Key.LastIndexOf('/') + 1)..];
        string storedBaseName = Path.GetFileNameWithoutExtension(storedName);
        return storedBaseName.Equals(setBaseName, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Relocates a set's verified volumes out of its guarded scratch work-root into the real output
    /// tree (<c>OutputPath\output\…</c>) via <see cref="VerifiedOutputRelocator"/>, then clears the
    /// now-emptied scratch — or keeps it, per the work-files setting (see
    /// <see cref="CleanupWorkRoot"/>). The legacy single-root set (empty key, work dir == OutputPath) is a no-op:
    /// its output already sits at <c>OutputPath\output\</c>, byte-identical to before.
    /// </summary>
    /// <returns>
    /// <c>Relocated</c> is true when the verified volumes reached their final location (or for the
    /// legacy no-op set); <c>ScratchPreserved</c> is true when a failed relocation could not fully roll
    /// back, so the caller must NOT delete the scratch work-root (recoverable output still lives there).
    /// </returns>
    private (bool Relocated, bool ScratchPreserved) RelocateVerifiedOutput(
        string workRoot, SRRArchiveSet set, int setCount, BruteForceRunResult result)
    {
        // Legacy single-root set: its brute-force output is already at OutputPath\output — nothing to move.
        if (string.IsNullOrEmpty(set.Key))
        {
            return (true, false);
        }

        bool custom = result.CustomPackerFiles.Count > 0;
        VerifiedOutputRelocator.Branch branch = custom
            ? VerifiedOutputRelocator.Branch.CustomPacker
            : VerifiedOutputRelocator.Branch.BruteForce;
        IReadOnlyList<string> files = custom
            ? result.CustomPackerFiles
            : (result.Matches.Count > 0 ? result.Matches[0].Files : []);

        VerifiedOutputRelocator.RelocationOutcome outcome = VerifiedOutputRelocator.Relocate(
            outputPath(), workRoot, set, setCount, branch, completeAllVolumes(), files, fileMover,
            message => log(message));

        if (outcome.Success)
        {
            CleanupWorkRoot(workRoot, set); // clear or keep the now-emptied scratch per the work-files setting
            return (true, false);
        }

        return (false, outcome.ScratchPreserved);
    }

    /// <summary>
    /// Removes a set's guarded scratch work-root (a strict descendant of the reserved
    /// <c>.rescene-work</c> tree) — but only when the user opted into clearing work files
    /// (<c>AppSettings.CleanupReconstructionWorkFiles</c>, captured at run start); by default
    /// the work-root is KEPT for diagnostics and its path is logged. No-op for the legacy single-root
    /// set (empty key) whose work dir is <c>OutputPath</c> itself, and for a work-root a junction
    /// would redirect outside the reserved scratch tree (fail-closed).
    /// </summary>
    private void CleanupWorkRoot(string workRoot, SRRArchiveSet set)
    {
        if (string.IsNullOrEmpty(set.Key))
        {
            return;
        }

        if (!_cleanupWorkFilesThisRun)
        {
            // Only log a path that actually exists: a set can fail before its scratch is ever created
            // (e.g. an unsatisfiable per-set version requirement throws in BuildOptionsForSet), and
            // pointing the user at a non-existent diagnostics folder would mislead.
            if (Directory.Exists(workRoot))
            {
                log($"Work files kept: {workRoot}");
            }

            return;
        }

        try
        {
            string scratchRoot = ReconstructionPathGuard.ResolveScratchRoot(outputPath());
            if (Directory.Exists(workRoot) && ReconstructionPathGuard.IsStrictDescendant(scratchRoot, workRoot))
            {
                Directory.Delete(workRoot, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            log($"Failed to clean up work dir for {set.Key}: {ex.Message}");
        }
    }

    public async Task RunArchiveSetsAsync(CancellationToken token)
    {
        // Await any in-flight version scan (e.g. a manual Rescan whose Task hasn't landed yet) BEFORE
        // capturing the shared settings below — RescanVersions does not clear HasScannedVersions for a
        // valid folder, so without this a still-running rescan's stale _lastScan could be captured
        // even though HasScannedVersions correctly reads true.
        await (lastVersionScan() ?? Task.CompletedTask);

        // Run-scoped capture: a mid-run settings save must not flip cleanup behaviour between sets.
        _cleanupWorkFilesThisRun = settingsService?.Load().CleanupReconstructionWorkFiles ?? false;

        SharedReconstructionSettings shared = await buildSharedSettingsAsync(token);

        // For the legacy / no-SRR single flat set the original RAR names may be empty; fall back to
        // the verification snapshot's RAR-volume entries so output renaming still works (matches the
        // old ResolveOutputRenameNames behaviour). When an SRR was imported its names take precedence.
        IReadOnlyList<string> flatNames = import().OriginalRARFileNames.Count > 0
            ? import().OriginalRARFileNames
            : shared.Verification.VolumeNames;

        IReadOnlyList<SRRArchiveSet> sets = ArchiveSetPlanner.ResolveSets(
            import().ArchiveSets, import().SRRFilePath, flatNames, import().ArchiveFiles);

        var outcomes = new List<SetOutcome>();
        WinningCombo? seed = null;

        if (sets.Count > 1)
        {
            log($"Reconstructing {sets.Count} archive sets independently.");
        }

        for (int i = 0; i < sets.Count; i++)
        {
            SRRArchiveSet set = sets[i];
            string label = string.IsNullOrEmpty(set.Key) ? "(release)" : set.Key;
            if (sets.Count > 1)
            {
                log($"=== Set {i + 1}/{sets.Count}: {label} ===");
            }

            byte[]? embedded = LoadEmbeddedSfvBytes(set);
            Dictionary<string, string> expected = ArchiveSetPlanner.BuildExpectedVolumeCrcs(set, embedded, shared.Verification);

            // Full-volume verification needs a per-volume CRC for every volume; without them we
            // cannot honestly verify the set, so skip it rather than report a false success.
            // Note: SHA1 runs (no per-volume CRC source) and zero-coverage cases are NOT skipped —
            // the engine still runs and gates on the first-volume hash. Only partial CRC32 coverage
            // (some volumes have CRCs but not all) is an honest skip.
            if (ArchiveSetPlanner.ShouldSkipUnverifiableSet(shared.CompleteAllVolumes, shared.HashType, expected.Count, set.VolumeNames.Count))
            {
                log($"Set {label}: no per-volume CRCs to verify; supply its .sfv. Skipping.");
                outcomes.Add(new SetOutcome(set, label, Success: false, Skipped: true));
                continue;
            }

            // The work-root path is resolved before the per-set try (it never depends on the set's own
            // command/version matrix). Its own guarded resolution can throw a path-resolution error
            // (e.g. a keyed set's scratch child real-resolves through an un-inspectable or junction-
            // redirected reserved root): keep that scoped to THIS set — the loop records a failing set
            // and continues — instead of letting it abort every remaining set. The `continue` runs
            // BEFORE the outer try/finally below, so the finally never sees (nor tries to clean) an
            // uncomputed work root. A per-set matrix failure (#6 — no selected WinRAR version can
            // produce this set's format) is likewise raised INSIDE the try and handled there.
            string workRoot;
            try
            {
                workRoot = ArchiveSetPlanner.WorkRootFor(shared, set);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                log($"Set {label} failed: {ex.Message}");
                progress.CompleteActiveVersion("No Match");
                outcomes.Add(new SetOutcome(set, label, Success: false, Skipped: false));
                continue;
            }

            bool committed = false;
            bool preserveScratch = false;
            try
            {
                BruteForceRunResult result;
                try
                {
                    // Build this set's own per-set command/version matrix off the UI thread (#6) —
                    // it can rebuild the full cartesian matrix via RARCommandLineBuilder, matching how
                    // BuildSharedSettingsAsync already offloads the global build.
                    BruteForceOptions options = await Task.Run(
                        () => ArchiveSetPlanner.BuildOptionsForSet(set, shared, expected, token), token);

                    // Tell the progress tracker which set is active so new rows are stamped with the label.
                    progress.SetActiveSet(sets.Count > 1 ? label : string.Empty);

                    result = await RunSingleSetAsync(label, options, seed, i + 1, sets.Count, token);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    // A set's own failure (e.g. an InvalidDataException from input-CRC validation, or
                    // an InvalidOperationException from an unsatisfiable per-set format/version
                    // requirement) must not abort the whole run — record it and move on to the next set.
                    log($"Set {label} failed: {ex.Message}");
                    // Finalize THIS set's own row now, from its own outcome (#23) — a later set's
                    // progress events must never be the ones that decide whether this row reads Match.
                    progress.CompleteActiveVersion("No Match");
                    outcomes.Add(new SetOutcome(set, label, Success: false, Skipped: false));
                    continue;
                }

                if (token.IsCancellationRequested)
                {
                    break;
                }

                if (!result.Success)
                {
                    progress.CompleteActiveVersion("No Match");
                    outcomes.Add(new SetOutcome(set, label, Success: false, Skipped: false));
                    continue;
                }

                seed ??= result.Combo;

                // Relocate the verified volumes out of the guarded scratch work-root into the real
                // output tree. Only a successful relocation counts as a committed set; a relocation
                // failure whose rollback could not complete preserves the scratch for recovery.
                (bool relocated, preserveScratch) = RelocateVerifiedOutput(workRoot, set, sets.Count, result);
                committed = relocated;
                progress.CompleteActiveVersion(relocated ? "Match" : "No Match");
                outcomes.Add(new SetOutcome(set, label, relocated, Skipped: false));
            }
            finally
            {
                // A committed set's scratch was already handled by the relocation (cleared or kept per
                // the work-files setting); a set whose rollback could not complete keeps its scratch
                // (recoverable output). Everything else — a failed, errored, or cancelled set — goes
                // through the same setting-gated CleanupWorkRoot here.
                if (!committed && !preserveScratch)
                {
                    CleanupWorkRoot(workRoot, set);
                }
            }
        }

        ReportSetSummary(outcomes, sets.Count, token.IsCancellationRequested);
    }

    /// <summary>
    /// Runs one set's brute force. For later sets a captured winning combo is tried first (seeding);
    /// only if it fails (and the run was not cancelled) is the full option matrix run. Returns the full
    /// run result (success, winning combo for seeding, and the committed/custom-packer file paths the
    /// relocation moves out of the scratch work-root).
    /// </summary>
    private async Task<BruteForceRunResult> RunSingleSetAsync(
        string label, BruteForceOptions options, WinningCombo? seed, int setIndex, int setCount, CancellationToken token)
    {
        BruteForceRunResult result;
        if (seed is not null && setCount > 1)
        {
            // Label this set's progress as the seeded attempt so its high-% progress and the full
            // attempt's fresh low-% progress read as distinct stages, not a rewind within the set (#24).
            sink.SetStageLabel(new SetStageLabel(setIndex, setCount, "seed"));
            BruteForceOptions narrowed = ArchiveSetPlanner.NarrowToCombo(options, seed);
            result = await Task.Run(() => bruteForceService.RunAsync(narrowed, token), token);
            if (!result.Success && !token.IsCancellationRequested)
            {
                log($"Seed combo did not reproduce {label}; running full search.");
                sink.SetStageLabel(new SetStageLabel(setIndex, setCount, "full"));
                result = await Task.Run(() => bruteForceService.RunAsync(options, token), token);
            }
        }
        else
        {
            sink.SetStageLabel(new SetStageLabel(setIndex, setCount, "full"));
            result = await Task.Run(() => bruteForceService.RunAsync(options, token), token);
        }

        return result;
    }

    /// <summary>
    /// Logs a per-set pass/fail/skip/cancelled summary and sets the overall progress message and
    /// <c>LastRunSucceeded</c>. Overall success requires every set to have passed with none
    /// skipped and no cancellation.
    /// </summary>
    private void ReportSetSummary(IReadOnlyList<SetOutcome> outcomes, int totalSets, bool cancelled)
    {
        bool multi = totalSets > 1;

        if (multi)
        {
            log("=== Reconstruction summary ===");
            foreach (SetOutcome o in outcomes)
            {
                string mark = o.Skipped ? "skipped" : o.Success ? "OK" : "failed";
                log($"  [{mark}] {o.Label}");
            }

            int notAttempted = totalSets - outcomes.Count;
            if (notAttempted > 0)
            {
                log($"  [not attempted] {notAttempted} set(s)");
            }
        }

        if (cancelled)
        {
            // The outer cancellation handler owns the final version-row status and progress message.
            return;
        }

        sink.SetProgressPercent(100);
        sink.SetProgressPercentText("100%");
        if (progress.LastOperationSize > 0)
        {
            sink.SetTestCountText($"Test {progress.LastOperationSize:N0} of {progress.LastOperationSize:N0}");
        }

        // Each set's own row was already finalized from its own outcome at set completion (#23) — no
        // per-row relabeling here. This method only owns the run-wide aggregate below.
        bool attemptedAll = outcomes.Count == totalSets;
        bool allOk = attemptedAll && outcomes.All(o => o is { Success: true, Skipped: false });

        // Surface the count of combinations the engine could not run (e.g. a rar binary without the
        // execute bit) in the completion heading — a run-wide "existence of errors" aggregate (WCAG
        // 4.1.3) that a blind user would otherwise have to hunt cell-by-cell, and that gives sighted
        // users an at-a-glance signal too. The heading is a Polite live region, so this announces once
        // at completion.
        int errorCount = versionEntries.Count(v => v.Status == "Error");
        string errorSuffix = errorCount > 0 ? $" ({errorCount} could not run)" : string.Empty;

        sink.SetProgressMessage(allOk ? "Match found!" : "No match found.");
        sink.SetPhaseDescription((allOk ? "Complete — Match Found!" : "Complete — No Match") + errorSuffix);
        sink.SetLastRunSucceeded(allOk);
        log(allOk
            ? "Brute-force completed: all sets matched!"
            : "Brute-force completed: not all sets matched.");

        // Existence-of-errors aggregate: the scannable "did anything fail?" marker at the end of the
        // log, matching the completion heading's "(N could not run)". The per-failure [P2] WARNINGs sit
        // earlier in the same merged log, so the line points up rather than at a separate pane.
        if (errorCount > 0)
        {
            log($"{errorCount} combination(s) could not run — each failure is logged above.");
        }
    }

    private readonly record struct SetOutcome(SRRArchiveSet Set, string Label, bool Success, bool Skipped);
}
