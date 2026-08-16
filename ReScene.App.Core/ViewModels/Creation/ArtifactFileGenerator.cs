using ReScene.App.Core.Services;
using ReScene.SRR;
using ReScene.SRS;

namespace ReScene.App.Core.ViewModels.Creation;

/// <summary>
/// Creates the individual artifact files a Creator run stores alongside a release: a sample's
/// <c>.srs</c>, and the nested <c>.srr</c> for a subtitle <c>.sfv</c>'s RAR chains. Extracted from
/// CreatorViewModel so the folder-mode staging path and the file-mode/wizard paths call one
/// implementation — before this existed, the file-mode vobsub path reached across into the
/// folder-mode section to reuse the per-chain nested-SRR generator.
/// </summary>
/// <param name="srrService">
/// Creates SRR files. Per-instance by design: the service re-exposes one writer's progress events,
/// so sharing an instance across view-models would cross their progress streams.
/// </param>
/// <param name="srsService">Creates SRS files; per-instance for the same reason.</param>
/// <param name="log">Receives the user-facing progress and failure lines these generators emit.</param>
internal sealed class ArtifactFileGenerator(
    ISRRCreationService srrService, ISRSCreationService srsService, Action<string> log)
{
    /// <summary>
    /// The options a NESTED subtitle SRR is always created with, regardless of the outer run's.
    /// pyrescene's <c>create_srr_for_subs</c> hardcodes its own nested-creation options
    /// (<c>save_paths=False, compressed=True, oso_hash=False</c>) rather than forwarding the outer
    /// SRR's, so a user enabling OSO hashes for the outer SRR must not leak OSO blocks into a
    /// nested subtitle SRR pyrescene never adds them to. <c>save_paths=False</c> needs no explicit
    /// handling: neither creation path used here has a relative-path mode, so both are already
    /// flat-name-only by construction.
    /// </summary>
    private static SRRCreationOptions NestedOptions(SRRCreationOptions outer) => new()
    {
        AppName = outer.AppName,
        AllowCompressed = true,
        ComputeOSOHashes = false,
    };

    /// <summary>
    /// One nested SRR PER RAR CHAIN the subtitle SFV lists — matching pyrescene's
    /// <c>create_srr_for_subs</c>, which walks every "first RAR" it finds and makes a dedicated SRR
    /// named after THAT chain's own basename (e.g. a two-language subtitle SFV listing
    /// "eng.rar"+"eng.r00" and a separate "jpn.rar" yields "eng.srr" AND "jpn.srr", not one merged
    /// SRR).
    ///
    /// Chain grouping goes through the shared <see cref="SfvVolumeResolver.ResolveOrderedChains"/>
    /// — the SAME code <c>SRRWriter.ResolveVolumesAsync</c>'s SFV branch runs, so the two can never
    /// disagree. An earlier hand-rolled copy DIVERGED: <c>SFVFile.ReadFile</c> split every space (so
    /// it THREW <see cref="InvalidDataException"/> on a spaced RAR name like <c>my movie.rar</c>,
    /// dropping the whole chain), and a raw <see cref="Path.Combine(string, string)"/> left a
    /// <c>.\eng.r00</c> continuation keyed apart from its <c>eng.rar</c> head (splitting one chain
    /// into two same-named <c>eng.srr</c> SRRs — a duplicate logical name the writer then rejects).
    /// The resolver's space-tolerant parse + <see cref="SrrNameCanonicalizer.ResolveSfvEntry"/>
    /// fixes both, trusting the SFV's OWN listed entries as each chain's full membership (no fresh
    /// on-disk chain-walk, same as the writer). Each chain is written via
    /// <see cref="ISRRCreationService.CreateFromRARAsync"/> directly (not <c>CreateFromSFVAsync</c>)
    /// since the chain's volumes are already resolved here.
    /// </summary>
    public async Task<List<StoredFileEntry>> GenerateNestedSubtitleSrrsAsync(
        string sfvPath, string dirPrefix, string workDir, int index, SRRCreationOptions options, CancellationToken ct)
    {
        string sfvName = Path.GetFileName(sfvPath);
        string sfvDir = Path.GetDirectoryName(sfvPath) ?? ".";

        IReadOnlyList<IReadOnlyList<string>> chains;
        try
        {
            string[] lines = await File.ReadAllLinesAsync(sfvPath, ct).ConfigureAwait(false);
            chains = SfvVolumeResolver.ResolveOrderedChains(sfvDir, lines);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            log($"  Nested SRR error for {sfvName}: {ex.Message}");
            return [];
        }

        if (chains.Count == 0)
        {
            log($"  Nested SRR skipped for {sfvName}: no RAR volumes found in SFV.");
            return [];
        }

        SRRCreationOptions nestedOptions = NestedOptions(options);

        var result = new List<StoredFileEntry>();
        int chainIndex = 0;
        foreach (IReadOnlyList<string> chain in chains)
        {
            ct.ThrowIfCancellationRequested();
            // SfvVolumeResolver now returns each chain in first-seen LISTING order and no longer
            // sorts (so the writer's single sort stays byte-identical to base — see
            // SfvVolumeResolver.ResolveOrderedChains). This path, unlike the writer, needs the TRUE
            // first volume (volume[0]) for chainStem/naming and passes the volumes to
            // CreateFromRARAsync, so it applies its OWN single per-chain sort here.
            var volumes = new List<string>(chain);
            volumes.Sort(RARVolumeNameComparer.Instance);
            string firstVolumeName = Path.GetFileName(volumes[0]);
            string chainStem = firstVolumeName.Length >= 4 ? firstVolumeName[..^4] : firstVolumeName;
            string storedName = dirPrefix + chainStem + ".srr";
            string srrPath = Path.Combine(workDir, $"{index}_{chainIndex}_{chainStem}.srr");
            chainIndex++;
            log($"Creating nested SRR for subtitle chain: {chainStem} (from {sfvName})");

            try
            {
                SRRCreationResult creation = await srrService.CreateFromRARAsync(srrPath, volumes, storedFiles: null, nestedOptions, ct);
                if (creation.Success)
                {
                    log($"  Nested SRR created: {Path.GetFileName(srrPath)} ({creation.SRRFileSize:N0} bytes)");
                    result.Add(new StoredFileEntry(storedName, srrPath));
                }
                else
                {
                    log($"  Nested SRR failed for {chainStem}: {creation.ErrorMessage}");
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                log($"  Nested SRR error for {chainStem}: {ex.Message}");
            }
        }

        return result;
    }

    /// <summary>
    /// A nested subtitle SRR is RAR-blocks-ONLY — no embedded SFV, no sibling .nfo files. Confirmed
    /// against a real pyrescene <c>--vobsub-srr</c> golden: its nested SRR contains only the
    /// extracted RAR volume block(s); embedding the SFV (and sibling .nfo files) was this app's own
    /// PRE-EXISTING choice, shared by both the folder-mode staging path and the wizard/Advanced
    /// <see cref="GenerateNestedSRRFileAsync"/> — and is also redundant regardless of the golden:
    /// the subtitle SFV's own bytes are already stored in the OUTER SRR (scanner pass-10 stores
    /// every SFV). Fixed globally (both callers), matching the RECOVERY_BLOCKS_REMOVED precedent: a
    /// shipped-behavior change applied everywhere the shared code path runs, not just the
    /// folder-mode surface.
    /// </summary>
    private static List<StoredFileEntry>? BuildNestedSubtitleStoredFiles() => null;

    /// <summary>
    /// Creates one .srs from <paramref name="samplePath"/> into <paramref name="tempDir"/> and
    /// returns its path, or null on failure. The index keeps temp filenames unique so two samples
    /// sharing a basename don't overwrite each other (the prefix never reaches the SRR).
    /// </summary>
    public async Task<string?> GenerateSRSFileAsync(string samplePath, string tempDir, int index, SRSCreationOptions srsOptions, CancellationToken ct)
    {
        string sampleName = Path.GetFileName(samplePath);
        string srsPath = Path.Combine(tempDir, $"{index}_{Path.ChangeExtension(sampleName, ".srs")}");
        log($"Creating SRS for: {sampleName}");

        try
        {
            SRSCreationResult result = await srsService.CreateAsync(srsPath, samplePath, srsOptions, ct);
            if (result.Success)
            {
                log($"  SRS created: {Path.GetFileName(srsPath)} ({result.SRSFileSize:N0} bytes)");
                return srsPath;
            }

            log($"  SRS failed for {sampleName}: {result.ErrorMessage}");
        }
        catch (Exception ex)
        {
            log($"  SRS error for {sampleName}: {ex.Message}");
        }

        return null;
    }

    /// <summary>
    /// Creates one nested .srr from the subtitle <paramref name="sfvPath"/> (and any .nfo beside it)
    /// into <paramref name="tempDir"/> and returns its path, or null on failure. Used only by the
    /// wizard's placeholder materialization.
    ///
    /// Unlike the Advanced-tab create-time path (which emits one nested SRR PER RAR CHAIN via
    /// <see cref="GenerateNestedSubtitleSrrsAsync"/>), this wizard-placeholder path stays
    /// single-merged-SRR: the placeholder→stored-item model is 1:1, so a subtitle SFV listing
    /// multiple chains still produces ONE merged SRR here. Per-chain support here needs a wizard
    /// model that can materialize one placeholder into several stored items. The oso-off option IS
    /// applied below regardless.
    /// </summary>
    public async Task<string?> GenerateNestedSRRFileAsync(string sfvPath, string tempDir, int index, SRRCreationOptions options, CancellationToken ct)
    {
        string sfvName = Path.GetFileName(sfvPath);
        string srrPath = Path.Combine(tempDir, $"{index}_{Path.ChangeExtension(sfvName, ".srr")}");
        log($"Creating nested SRR for: {sfvName}");

        SRRCreationOptions nestedOptions = NestedOptions(options);

        try
        {
            SRRCreationResult result = await srrService.CreateFromSFVAsync(
                srrPath, sfvPath, BuildNestedSubtitleStoredFiles(), nestedOptions, ct);
            if (result.Success)
            {
                log($"  Nested SRR created: {Path.GetFileName(srrPath)} ({result.SRRFileSize:N0} bytes)");
                return srrPath;
            }

            log($"  Nested SRR failed for {sfvName}: {result.ErrorMessage}");
        }
        catch (Exception ex)
        {
            log($"  Nested SRR error for {sfvName}: {ex.Message}");
        }

        return null;
    }

    /// <summary>
    /// Shared "enumerate sources → generate file → name it → record it" loop behind the Advanced
    /// create-time scans and the wizard's placeholder materialization. <paramref name="generate"/>
    /// produces the file for each source (returning null on failure/skip); <paramref name="record"/>
    /// is the sink — the Advanced paths add a stored item to the view-model's bound collection,
    /// while the wizard path writes into its placeholder→path map. The per-source index keeps temp
    /// filenames unique.
    /// </summary>
    public static async Task GenerateAndRecordAsync<TSource>(
        IReadOnlyList<TSource> sources,
        Func<TSource, int, CancellationToken, Task<string?>> generate,
        Action<TSource, string> record,
        CancellationToken ct)
    {
        for (int i = 0; i < sources.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            string? generated = await generate(sources[i], i, ct);
            if (generated is not null)
            {
                record(sources[i], generated);
            }
        }
    }
}
