using ReScene.App.Core.Services;
using ReScene.SRR;
using ReScene.SRS;

namespace ReScene.App.Core.ViewModels.Creation;

/// <summary>
/// Folder mode's generated-artifact staging: creates the samples' and subtitles' artifacts and
/// splices them into the stored-file list at the positions pyrescene's own passes would put them.
/// </summary>
/// <remarks>
/// <para>
/// Input timing is deliberately NOT uniform, because the code this replaces did not read its inputs
/// at one moment. Samples arrive already materialized (read when staging begins, as before), while
/// the subtitle SFVs and the vobsub toggle arrive as accessors invoked only AFTER sample generation
/// has completed — which is when the original read them. Making either side match the other would
/// change behavior: a subtitle SFV or a toggle change that lands while samples are generating is
/// currently observed, and must stay observed.
/// </para>
/// <para>
/// The release root is likewise a live accessor rather than a captured string: the original re-read
/// it at each phase point, across awaits, while the input path stays user-editable during a run.
/// </para>
/// </remarks>
/// <param name="srrService">Creates SRR files — used directly for a RAR-backed vob sample's nested SRR.</param>
/// <param name="srsService">Creates SRS files — used directly, with staging's own naming and logging.</param>
/// <param name="generator">Shared per-file generators; supplies the per-chain nested subtitle SRRs.</param>
/// <param name="releaseRoot">The current release root, re-read at each phase point.</param>
/// <param name="log">Receives the user-facing progress and failure lines staging emits.</param>
internal sealed class CreatorArtifactStager(
    ISRRCreationService srrService,
    ISRSCreationService srsService,
    ArtifactFileGenerator generator,
    Func<string> releaseRoot,
    Action<string> log)
{
    /// <summary>
    /// Generates folder mode's samples/subtitles artifacts (an .srs, its failure .txt, and a
    /// RAR-backed .vob's nested .srr; a subtitle SFV's nested .srr) and splices them into
    /// <paramref name="baseline"/> (the current stored-files snapshot) at the excerpt's category
    /// positions, then re-applies the pass-10 proof-before-sfv reorder over the complete, merged
    /// list. Samples are generated and spliced in BEFORE subtitles (matching the excerpt's own pass
    /// order — samples are pass 6, subtitles pass 9) so the subtitle pass's already-stored-RAR check
    /// sees the fully-current stored list, not just the pre-sample baseline.
    /// </summary>
    /// <param name="baseline">The current stored-file list to splice into.</param>
    /// <param name="workDir">This run's working directory for generated files.</param>
    /// <param name="options">The outer run's creation options.</param>
    /// <param name="autoCreateSrs">Whether sample SRS artifacts are generated at all.</param>
    /// <param name="appName">The app name stamped into generated SRS files.</param>
    /// <param name="samples">Sample sources, materialized when staging begins.</param>
    /// <param name="subtitleSfvs">Subtitle SFV sources, read only after sample generation completes.</param>
    /// <param name="createVobsubSrr">The vobsub toggle, likewise read only after sample generation.</param>
    /// <param name="ct">Cancels staging.</param>
    public async Task<List<StoredFileEntry>> StageAsync(
        List<StoredFileEntry> baseline,
        string workDir,
        SRRCreationOptions options,
        bool autoCreateSrs,
        string appName,
        IReadOnlyList<string> samples,
        Func<IReadOnlyList<string>> subtitleSfvs,
        Func<bool> createVobsubSrr,
        CancellationToken ct)
    {
        // Folder mode honors AutoCreateSRS just as file mode does for pyrescene --no-srs parity.
        // When off, generate no sample SRS artifacts — a sample's ONLY stored output is its .srs
        // (the sample MEDIA itself is never stored), so an empty list simply stores nothing.
        List<StoredFileEntry> generated = autoCreateSrs
            ? await GenerateSampleArtifactsAsync(samples, appName, workDir, options, ct)
            : [];

        // Generated `.srs` SUPERSEDES a same-relative-path pre-existing `.srs` in the stored list:
        // drop the baseline entry at any logical name a freshly-generated SRS also produced — no
        // collision error, the generated one simply replaces it.
        var supersededNames = new HashSet<string>(
            generated.Where(e => e.StoredName.EndsWith(".srs", StringComparison.OrdinalIgnoreCase)).Select(e => e.StoredName),
            StringComparer.OrdinalIgnoreCase);
        List<StoredFileEntry> kept = [.. baseline.Where(e => !supersededNames.Contains(e.StoredName))];

        // Splice positions are derived from the CURRENT (possibly user-edited) stored-files
        // snapshot rather than re-derived from the raw scan categories, so a manual edit between
        // scan and Create is respected — an accepted approximation: the byte-identity guarantee
        // only covers an unedited scan's output, which is what these two finders locate exactly
        // (see their own remarks for how).
        kept.InsertRange(CreatorArtifactNaming.FindSampleArtifactSpliceIndex(kept), generated);

        // For pyrescene --vobsub-srr parity: the toggle gates ONLY the nested-SRR generation
        // (pass 9) inside the subtitle pass — the subtitle-SFV storage (pass 10) always runs,
        // matching pyrescene, which without --vobsub-srr still stores extra_sfvs and only skips
        // create_srr_for_subs.
        //
        // Both accessors are invoked HERE, after sample generation, exactly where the original read
        // the corresponding properties.
        List<StoredFileEntry> subtitles = await GenerateSubtitleArtifactsAsync(
            kept, subtitleSfvs(), workDir, options, createVobsubSrr(), ct);
        kept.InsertRange(CreatorArtifactNaming.FindSubtitleArtifactSpliceIndex(kept), subtitles);

        return ReleaseScanner.ApplyProofBeforeSfvReorder(kept, static e => e.StoredName);
    }

    /// <summary>
    /// Creates one .srs per sample (+its failure .txt when creation fails and the SAMPLE FILE is
    /// non-empty; +a nested .srr when the sample is a RAR-backed .vob). Collision keying matches the
    /// excerpt's <c>same_srs_name</c> exactly: by FULL RELATIVE STEM (directory included) — only a
    /// stem shared by more than one sample keeps the full source extension in its SRS name.
    /// </summary>
    private async Task<List<StoredFileEntry>> GenerateSampleArtifactsAsync(
        IReadOnlyList<string> samples, string appName, string workDir, SRRCreationOptions options, CancellationToken ct)
    {
        if (samples.Count == 0)
        {
            return [];
        }

        // FolderRelativeStem/FolderRelativeName fall back to "Sample/<basename>" for a source
        // OUTSIDE the release root — the sample list is shared with the file-mode Advanced tab's
        // "Add Sample" command, so a folder-mode run can still see a manually-added, out-of-root
        // sample; the raw root-relative path would keep an invalid "../" the writer's
        // CanonicalizeRelative rejects.
        List<string> stems = [.. samples.Select(s => CreatorArtifactNaming.FolderRelativeStem(releaseRoot(), s, "Sample"))];
        var collisionStems = stems
            .GroupBy(s => s, StringComparer.Ordinal)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToHashSet(StringComparer.Ordinal);

        var srsOptions = new SRSCreationOptions
        {
            AppName = string.IsNullOrWhiteSpace(appName) ? "ReScene Manager" : appName
        };

        var result = new List<StoredFileEntry>();
        for (int i = 0; i < samples.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            string sample = samples[i];
            string relPath = CreatorArtifactNaming.FolderRelativeName(releaseRoot(), sample, "Sample");
            string srsLogicalName = (collisionStems.Contains(stems[i]) ? relPath : stems[i]) + ".srs";
            string physicalSrsPath = Path.Combine(workDir, $"{i}_{Path.GetFileName(sample)}.srs");

            SRSCreationResult srsResult = await srsService.CreateAsync(physicalSrsPath, sample, srsOptions, ct);
            if (!srsResult.Success)
            {
                log($"SRS failed for {Path.GetFileName(sample)}: {srsResult.ErrorMessage}");

                // The excerpt gates the failure .txt on the SAMPLE FILE's own size —
                // `sample_size = os.path.getsize(sample); if sample_size > 0: keep_txt = True` —
                // unconditionally on the error, NOT on the error text's length (the old gate here).
                // A genuinely 0-byte sample is the only case that differs: pyrescene suppresses the
                // .txt for it regardless of what stderr captured.
                long sampleSize;
                try
                {
                    sampleSize = new FileInfo(sample).Length;
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    sampleSize = 0;
                }

                if (sampleSize > 0)
                {
                    string physicalTxtPath = Path.Combine(workDir, $"{i}_{Path.GetFileName(sample)}.txt");
                    await File.WriteAllTextAsync(physicalTxtPath, srsResult.ErrorMessage ?? string.Empty, ct);
                    result.Add(new StoredFileEntry(relPath + ".txt", physicalTxtPath));
                }

                continue;
            }

            log($"Created SRS: {srsLogicalName}");
            result.Add(new StoredFileEntry(srsLogicalName, physicalSrsPath));

            // Per the excerpt, a RAR-backed .vob sample keeps its SRS AND gets a nested SRR (both,
            // not a replacement). Only attempted once the SRS itself exists (a [DIVERGENCE:
            // hardening] over the excerpt, which would try to load an SRS that was never written
            // when creation failed for a .vob — see IsRarBackedVobSample's remarks).
            if (!CreatorArtifactNaming.IsRarBackedVobSample(sample))
            {
                continue;
            }

            string vobSrrLogicalName = srsLogicalName[..^".srs".Length] + ".srr";
            string physicalVobSrrPath = Path.Combine(workDir, $"{i}_{Path.GetFileName(sample)}.vob.srr");

            // pyrescene's create_srr_single_volume (main.py) — the routine that writes this
            // .vob/single-volume nested SRR — emits ONLY a header block, a RarFile block, and the
            // raw RAR block bytes; it has NO oso_hash parameter or logic at all, so its output NEVER
            // contains OSO blocks. Force our own dedicated nested options (oso off, compressed
            // allowed) IDENTICAL to the two sibling nested-SRR paths instead of forwarding the OUTER
            // `options`, so a user enabling ComputeOSOHashes for the outer SRR cannot leak OSO
            // blocks pyrescene omits into this nested one (a byte divergence the oso-off golden
            // can't catch). This also corrects the secondary AllowCompressed forwarding.
            var nestedOptions = new SRRCreationOptions
            {
                AppName = options.AppName,
                AllowCompressed = true,
                ComputeOSOHashes = false,
            };
            try
            {
                SRRCreationResult vobResult = await srrService.CreateFromRARAsync(
                    physicalVobSrrPath, [sample], null, nestedOptions, ct);
                if (vobResult.Success)
                {
                    log($"Created nested SRR for RAR-backed vob sample: {vobSrrLogicalName}");
                    result.Add(new StoredFileEntry(vobSrrLogicalName, physicalVobSrrPath));
                }
                else
                {
                    log($"Nested SRR failed for RAR-backed vob sample {Path.GetFileName(sample)}: {vobResult.ErrorMessage}");
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                log($"Nested SRR error for RAR-backed vob sample {Path.GetFileName(sample)}: {ex.Message}");
            }
        }

        return result;
    }

    /// <summary>
    /// Stages every subtitle SFV's generated artifacts in pyrescene's own two-pass order: pyrescene
    /// creates ALL nested subtitle SRRs first, in its vobsub pass 9 (<c>create_srr_for_subs</c>),
    /// and appends the excluded/subtitle SFVs' own bytes only in the final tail, pass 10 — so ALL
    /// nested SRRs precede ALL subtitle-SFV entries. An earlier version emitted each SFV's own entry
    /// BEFORE its nested SRRs (per-SFV interleaving), producing the reversed
    /// <c>[subs.sfv, eng.srr]</c>; the same-stem pass-10 reorder cannot repair that when the SFV
    /// name differs from the chain (a <c>subs.sfv</c> listing <c>eng.rar</c>), which is exactly
    /// every multi-language subtitle SFV.
    ///
    /// The subtitle SFV's own bytes are stored EXACTLY ONCE, however it got here — for a
    /// SCANNER-origin subtitle, the scanner's pass-10 (<c>foreach sfv in sfvs</c>,
    /// ReleaseScanner.cs) already stores EVERY sfv, including excluded/subtitle ones
    /// (<c>InputSfvs_Appended_AfterAllOtherCategories</c> proves it), so <paramref name="currentStored"/>
    /// already contains it and re-adding it here would be a duplicate. But the subtitle list is ALSO
    /// populated by the manual "Add Subtitle" command, whose source never reaches the scanner's
    /// traversal at all — pass-10 doesn't cover a manually-added one — so those need storing here.
    /// Both cases are reconciled with one OS-final-path dedup check (matching the scanner's own
    /// <see cref="ReleaseScanner.ResolveDedupKey"/> dedup discipline) instead of two separate code
    /// paths for "scanner-origin" vs. "manual".
    /// </summary>
    private async Task<List<StoredFileEntry>> GenerateSubtitleArtifactsAsync(
        List<StoredFileEntry> currentStored, IReadOnlyList<string> subtitleSfvs, string workDir,
        SRRCreationOptions options, bool generateNestedSrrs, CancellationToken ct)
    {
        if (subtitleSfvs.Count == 0)
        {
            return [];
        }

        var result = new List<StoredFileEntry>();

        // Pass 9 (create_srr_for_subs): create every subtitle SFV's nested SRRs first, so they all
        // precede the subtitle-SFV entries emitted in pass 10 below. Gated on the vobsub toggle
        // (threaded in as generateNestedSrrs) for pyrescene --vobsub-srr parity: pyrescene without
        // --vobsub-srr skips create_srr_for_subs entirely but STILL stores the extra_sfvs — so pass
        // 10 below runs regardless, keeping scanner-origin and manually-added subtitle SFVs stored.
        for (int i = 0; generateNestedSrrs && i < subtitleSfvs.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            string sfv = subtitleSfvs[i];
            string sfvBasename = Path.GetFileName(sfv);

            // FolderRelativeStem falls back to "Subs/<basename>" for a source OUTSIDE the release
            // root (the subtitle list is shared with the file-mode Advanced tab's "Add Subtitle"
            // command) — the raw root-relative path would keep an invalid "../" the writer's
            // CanonicalizeRelative rejects.
            string stem = CreatorArtifactNaming.FolderRelativeStem(releaseRoot(), sfv, "Subs");

            // Per the excerpt, "not for Proof RARs that are already stored inside the SRR" — skip
            // nested-SRR creation for this subtitle SFV when some entry ALREADY in the stored list
            // ends with its basename-stem swapped to ".rar" (`basename(esfv)[:-3] + "rar"`, the
            // excerpt's own slice: for a 4-char ".sfv" extension this simply swaps it to ".rar").
            // Skipping here does not affect the SFV's OWN storage (pass 10 below, independent of
            // this check) — only whether we ALSO wrap its RAR in a redundant nested SRR when the
            // RAR is already embedded directly (e.g. a proof RAR sharing this excluded SFV's stem).
            if (sfvBasename.Length > 3)
            {
                string candidateRarSuffix = sfvBasename[..^3] + "rar";
                if (currentStored.Any(e => e.StoredName.EndsWith(candidateRarSuffix, StringComparison.OrdinalIgnoreCase)))
                {
                    log($"Subtitle SFV skipped (its RAR is already stored): {sfvBasename}");
                    continue;
                }
            }

            // The "directory" half of `stem` (everything up to the SFV's own last path segment) is
            // reused for EVERY nested SRR this subtitle SFV produces — each chain keeps ITS OWN
            // first-RAR basename as the file-name half (create_srr_for_subs), not the SFV's.
            int lastSlash = stem.LastIndexOf('/');
            string dirPrefix = lastSlash < 0 ? string.Empty : stem[..(lastSlash + 1)];
            result.AddRange(await generator.GenerateNestedSubtitleSrrsAsync(sfv, dirPrefix, workDir, i, options, ct));
        }

        // Pass 10: append each subtitle SFV's own bytes AFTER all nested SRRs. Store this subtitle
        // SFV's own bytes unless SOME entry already in the stored list (pass-10's scanner-origin
        // storage, or an earlier iteration's own addition) resolves to the same OS-final-path —
        // dedup against re-storing a scanner-origin subtitle, while still covering a manually-added
        // one exactly once. (Nested SRRs already in `result` are `.srr` sources whose dedup keys
        // never collide with an SFV's, so the pass-9 additions above do not interfere with this
        // check.)
        //
        // [DIVERGENCE: determinism] A manually-added subtitle SFV (an app feature with no pyrescene
        // equivalent) is staged here into `result`, which the caller splices with the nested-SRR
        // artifact block; a scanner-origin subtitle SFV instead rides the `currentStored` baseline
        // (the scanner's own pass-10). So in a folder that has BOTH, the manually-added SFV precedes
        // the scanner-discovered one in the merged list. pyrescene has no manual-add feature, so
        // there is NO parity target for this relative order; the design already declares
        // excluded-SFV ordering a [DIVERGENCE: determinism]. The invariant that DOES matter — each
        // subtitle SFV stored EXACTLY ONCE (the dedup below) — holds regardless of that order.
        foreach (string sfv in subtitleSfvs)
        {
            ct.ThrowIfCancellationRequested();
            string sfvDedupKey = ReleaseScanner.ResolveDedupKey(sfv);
            bool sfvAlreadyStored = currentStored.Concat(result)
                .Any(e => string.Equals(ReleaseScanner.ResolveDedupKey(e.FullPath), sfvDedupKey, StringComparison.OrdinalIgnoreCase));
            if (!sfvAlreadyStored)
            {
                // Stem + ".sfv", NOT FolderRelativeName: this normalizes the stored extension, so an
                // "X.SFV" source is stored as "X.sfv" and an extensionless source still gains one.
                string stem = CreatorArtifactNaming.FolderRelativeStem(releaseRoot(), sfv, "Subs");
                result.Add(new StoredFileEntry(stem + ".sfv", sfv));
            }
        }

        return result;
    }
}
