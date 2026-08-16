using System.Collections.ObjectModel;
using ReScene.App.Core.Helpers;
using ReScene.App.Core.Services;
using ReScene.SRR;
using ReScene.SRS;

namespace ReScene.App.Core.ViewModels.Creation;

/// <summary>
/// Runs the Advanced tab's FILE-mode creation: the phases that turn a single SFV or first-volume RAR
/// input into an SRR, plus the auto-scan that seeds the stored-file list when the input changes.
/// Folder mode's counterpart is <see cref="CreatorArtifactStager"/>, driven from
/// <see cref="FolderScanController"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>The stored-file collection is appended to INCREMENTALLY, during the run.</b> It is held by
/// reference and each phase adds to it as it generates, exactly where it did before. Generation
/// order is storage order, and the bound DataGrid shows the list filling in live while
/// <c>IsCreating</c> is true. Batching the appends into one add at the end would produce a
/// byte-identical SRR and would still satisfy the collection-order test, while changing what the
/// user sees.
/// </para>
/// <para>
/// The appends also stay on the awaiting continuation and must NOT be moved behind
/// <c>IUiDispatcher.Post</c>, which would reorder them relative to the posted progress updates.
/// </para>
/// </remarks>
internal sealed class FileModeCreationPipeline(
    ISRRCreationService srrService,
    ArtifactFileGenerator artifacts,
    ObservableCollection<CreatorViewModel.StoredFileItem> storedFileItems,
    ObservableCollection<string> extraSampleFiles,
    ObservableCollection<string> extraSubtitleSfvFiles,
    Action<string> log)
{
    /// <summary>
    /// The per-run view-model values the pipeline reads. Snapshotted into a record at the start of a
    /// run rather than read live: unlike folder mode's release root, none of these is meant to
    /// change mid-run, and passing them as a value makes that explicit.
    /// </summary>
    internal sealed record Inputs(
        string InputPath,
        string OutputPath,
        bool IsSFVInput,
        bool AutoCreateSRS,
        bool CreateVobsubSRR,
        bool StoreFixRAR,
        string AppName,
        SRRCreationOptions Options);

    /// <summary>
    /// Replaces the stored-file list with a scan of the input's own directory. Called when the input
    /// path changes, not at creation time.
    /// </summary>
    public void AutoScanReleaseFiles(bool autoIncludeFiles, string inputPath)
    {
        if (!autoIncludeFiles || string.IsNullOrWhiteSpace(inputPath))
        {
            return;
        }

        string releaseDir = Path.GetDirectoryName(inputPath) ?? ".";
        if (!Directory.Exists(releaseDir))
        {
            return;
        }

        storedFileItems.Clear();

        try
        {
            List<(string FullPath, string StoredName)> scanned = ReleaseFileScanner.ScanReleaseDirectory(releaseDir);
            foreach ((string? fullPath, string? storedName) in scanned)
            {
                storedFileItems.Add(new CreatorViewModel.StoredFileItem
                {
                    FullPath = fullPath,
                    StoredName = storedName
                });
            }
        }
        catch
        {
            // Directory scan failures are non-fatal
        }

    }

    /// <summary>
    /// Runs every file-mode creation phase in order and returns the writer's result.
    /// </summary>
    /// <param name="inputs">The run's snapshotted view-model values.</param>
    /// <param name="ensureTempDir">
    /// Returns the run's temp directory, creating it on first call. A callback rather than a path
    /// because the directory is created lazily — only if a phase actually needs one — while its
    /// cleanup belongs to the caller's <c>finally</c>, which must therefore own the variable.
    /// </param>
    /// <param name="materializePlaceholders">
    /// Generates the wizard's placeholder artifacts. Stays on the view-model: the placeholders are
    /// its own list's items, and the wizard's editing commands work on the same objects.
    /// </param>
    /// <param name="ct">Cancellation for the whole run.</param>
    public async Task<SRRCreationResult> RunAsync(
        Inputs inputs,
        Func<string> ensureTempDir,
        Func<string, SRRCreationOptions, CancellationToken, Task<Dictionary<CreatorViewModel.StoredFileItem, string>>> materializePlaceholders,
        CancellationToken ct)
    {
        // GetDirectoryName returns "" (not null) for a bare file name — same guard as
        // ComputeStoredName and BuildSampleAndSubtitlePlaceholders.
        string releaseDir = Path.GetDirectoryName(inputs.InputPath) is { Length: > 0 } dir ? dir : ".";

        // Phase 0: Materialize the wizard's sample/subtitle placeholders — generate their
        // actual .srs/.srr now, in the order the user arranged. (Advanced has no placeholders.)
        // Non-destructive: returns a map; placeholders stay placeholders so a retry regenerates.
        var materialized = new Dictionary<CreatorViewModel.StoredFileItem, string>();
        if (storedFileItems.Any(f => f.Kind != CreatorViewModel.StoredFileKind.Regular))
        {
            string tempDir = ensureTempDir();
            materialized = await materializePlaceholders(tempDir, inputs.Options, ct);
        }

        // Phase 1: Auto-create SRS files for samples (Advanced tab; the wizard uses placeholders
        // above instead, with AutoCreateSRS off).
        if (inputs.AutoCreateSRS)
        {
            await CreateSRSForSamplesAsync(releaseDir, ensureTempDir(), inputs.AppName, ct);
        }

        // Phase 2: Create nested SRRs for subtitle archives
        if (inputs.CreateVobsubSRR)
        {
            await CreateVobsubSRRsAsync(releaseDir, inputs.Options, ensureTempDir(), ct);
        }

        // Phase 3: Store fix RAR if applicable
        if (inputs.StoreFixRAR)
        {
            StoreFixRARFile(releaseDir);
        }

        // Phase 4: Create the main SRR

        // Stored files are written in this list's order. A stored name can only appear once in
        // an SRR, so two files sharing a name can't both be written: keep the entry in its
        // original position but take the last source for it (so a freshly generated SRS wins
        // over an earlier auto-scanned copy), and warn rather than silently dropping a file.
        var storedFiles = new List<StoredFileEntry>();
        var positions = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (CreatorViewModel.StoredFileItem item in storedFileItems)
        {
            // A placeholder's real path comes from this run's materialization map (it's not
            // written back onto the item, so a retry regenerates). Skip a placeholder whose
            // generation failed.
            string fullPath = item.Kind == CreatorViewModel.StoredFileKind.Regular
                ? item.FullPath
                : materialized.GetValueOrDefault(item, string.Empty);
            if (string.IsNullOrEmpty(fullPath))
            {
                continue;
            }

            // Normalize to the writer's key space (forward slashes) so a backslash typed into
            // the editable "Stored As" column can't slip past this collision check and then be
            // silently dropped by the writer.
            string storedName = item.StoredName.Replace('\\', '/');
            if (positions.TryGetValue(storedName, out int pos))
            {
                if (!storedFiles[pos].FullPath.Equals(fullPath, StringComparison.OrdinalIgnoreCase))
                {
                    log($"WARNING: Two stored files use the name \"{storedName}\" — only one is included. Rename one to keep both.");
                }

                storedFiles[pos] = new StoredFileEntry(storedName, fullPath);
            }
            else
            {
                positions[storedName] = storedFiles.Count;
                storedFiles.Add(new StoredFileEntry(storedName, fullPath));
            }
        }

        if (inputs.IsSFVInput)
        {
            return await srrService.CreateFromSFVAsync(
                inputs.OutputPath, inputs.InputPath,
                storedFiles.Count > 0 ? storedFiles : null,
                inputs.Options, ct);
        }
        else
        {
            List<string> volumes = CreatorArtifactNaming.DiscoverRARVolumes(inputs.InputPath);
            log($"Found {volumes.Count} volume(s).");

            return await srrService.CreateFromRARAsync(
                inputs.OutputPath, volumes,
                storedFiles.Count > 0 ? storedFiles : null,
                inputs.Options, ct);
        }

    }

    // ── SRS auto-creation (Advanced tab: scan + generate at create time) ──

    private async Task CreateSRSForSamplesAsync(string releaseDir, string tempDir, string appName, CancellationToken ct)
    {
        // Auto-detected samples plus any added manually on the wizard's Samples step.
        List<string> samples = [.. ReleaseFileScanner.FindSampleFiles(releaseDir)
            .Concat(extraSampleFiles)
            .Distinct(StringComparer.OrdinalIgnoreCase)];

        var srsOptions = new SRSCreationOptions
        {
            AppName = string.IsNullOrWhiteSpace(appName) ? "ReScene Manager" : appName
        };

        await ArtifactFileGenerator.GenerateAndRecordAsync(
            samples,
            (sample, i, token) => artifacts.GenerateSRSFileAsync(sample, tempDir, i, srsOptions, token),
            (sample, srsPath) => storedFileItems.Add(new CreatorViewModel.StoredFileItem
            {
                FullPath = srsPath,
                StoredName = CreatorArtifactNaming.GeneratedStoredName(releaseDir, sample, ".srs", "Sample"),
            }),
            ct);

    }

    // ── Vobsub nested SRR (Advanced tab: scan + generate at create time) ──

    private async Task CreateVobsubSRRsAsync(string releaseDir, SRRCreationOptions options, string tempDir, CancellationToken ct)
    {
        List<string> subtitleSfvs = [.. ReleaseFileScanner.FindSubtitleSFVFiles(releaseDir)
            .Concat(extraSubtitleSfvFiles)
            .Distinct(StringComparer.OrdinalIgnoreCase)];

        // The Advanced-tab create-time scan produces one nested SRR PER RAR CHAIN by REUSING the
        // folder-mode GenerateNestedSubtitleSrrsAsync — which already does the chain-split AND
        // builds its own nestedOptions (ComputeOSOHashes=false, AllowCompressed=true), so a
        // multi-language subtitle SFV yields per-chain SRRs (never one merged SRR) and a user
        // enabling ComputeOSOHashes for the outer run cannot leak OSO blocks into them.
        // GenerateAndRecordAsync's 1-source->1-path shape no longer fits (a single SFV can now
        // yield multiple SRRs), so record the returned list directly; the per-source index still
        // keeps temp filenames unique.
        for (int i = 0; i < subtitleSfvs.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            string sfv = subtitleSfvs[i];

            // dirPrefix = the directory half of the Advanced-tab CreatorArtifactNaming.GeneratedStoredName("Subs")
            // convention (strip the file-name), so each chain becomes `dirPrefix + chainStem +
            // ".srr"` — consistent with folder mode, where each chain keeps its OWN first-RAR
            // basename as the file-name half, not the subtitle SFV's.
            string generatedName = CreatorArtifactNaming.GeneratedStoredName(releaseDir, sfv, ".srr", "Subs");
            int lastSlash = generatedName.LastIndexOf('/');
            string dirPrefix = lastSlash < 0 ? string.Empty : generatedName[..(lastSlash + 1)];

            foreach (StoredFileEntry entry in await artifacts.GenerateNestedSubtitleSrrsAsync(sfv, dirPrefix, tempDir, i, options, ct))
            {
                storedFileItems.Add(new CreatorViewModel.StoredFileItem
                {
                    FullPath = entry.FullPath,
                    StoredName = entry.StoredName,
                });
            }
        }

    }

    // ── Fix release detection ───────────────────────────────

    private void StoreFixRARFile(string releaseDir)
    {
        string releaseName = Path.GetFileName(releaseDir) ?? string.Empty;
        if (!ReleaseFileScanner.IsFixRelease(releaseName))
        {
            return;
        }

        // Find SFV files in the release root
        string[] sfvFiles = Directory.GetFiles(releaseDir, "*.sfv");
        if (sfvFiles.Length != 1)
        {
            return;
        }

        // Find RAR files referenced by the SFV
        List<string> rarFiles = ReleaseFileScanner.FindRARFilesFromSFV(sfvFiles[0]);
        if (rarFiles.Count != 1)
        {
            return;
        }

        string rarPath = rarFiles[0];
        string storedName = Path.GetFileName(rarPath);

        // Don't add if already in stored files
        if (storedFileItems.Any(f => f.StoredName.Equals(storedName, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        storedFileItems.Add(new CreatorViewModel.StoredFileItem
        {
            FullPath = rarPath,
            StoredName = storedName
        });

        log($"Fix release detected. Storing RAR: {storedName}");

    }
}
