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
/// <item><term><paramref name="cleanupWorkFiles"/></term><description>Reads the view-model's
/// RUN-SCOPED capture, taken once after the version-scan await so a mid-run settings save cannot flip
/// cleanup behaviour between sets. The accessor preserves the original's read-at-cleanup-time
/// timing; the capture point itself is unchanged.</description></item>
/// </list>
/// </remarks>
internal sealed class ReconstructionRunner(
    IFileMover fileMover,
    Func<ReconstructionImportState> import,
    Func<string> outputPath,
    Func<bool> completeAllVolumes,
    Func<bool> cleanupWorkFiles,
    Action<string> log)
{
    /// <summary>
    /// Reads the embedded SFV bytes for a set from the imported SRR's stored files. For a single
    /// flat set (empty key) any stored .sfv matches. Otherwise a stored .sfv matches this set when
    /// either its archive-set key equals the set key (handles directory-prefixed stored names such
    /// as "DVD1\aln-re4a.sfv" → key "DVD1/aln-re4a"), OR its base name equals the set's base name
    /// (handles a flat "aln-re4a.sfv" matched to key "DVD1/aln-re4a"). Returns null when no SRR
    /// was imported or no stored .sfv matches.
    /// </summary>
    public byte[]? LoadEmbeddedSfvBytes(SRRArchiveSet set)
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
    public (bool Relocated, bool ScratchPreserved) RelocateVerifiedOutput(
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
    public void CleanupWorkRoot(string workRoot, SRRArchiveSet set)
    {
        if (string.IsNullOrEmpty(set.Key))
        {
            return;
        }

        if (!cleanupWorkFiles())
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
}
