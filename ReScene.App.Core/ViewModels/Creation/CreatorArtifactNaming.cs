using ReScene.App.Core.Helpers;
using ReScene.App.Core.Services;
using ReScene.SRR;

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
    internal static bool IsFilesystemRoot(string path)
    {
        string trimmed = Path.TrimEndingDirectorySeparator(path);
        return string.IsNullOrEmpty(Path.GetDirectoryName(trimmed));
    }

    /// <summary>
    /// Detects a scanner root-enumeration failure (<see cref="ReleaseScanResult.RootError"/>: every
    /// collection empty except a single warning in that method's exact "Cannot scan '&lt;root&gt;':
    /// ..." format). <see cref="ReleaseScanResult"/> carries no explicit RootFailed flag, so this is
    /// distinguished from an ordinary empty scan
    /// (e.g. "might be missing an SFV file", also a single warning with all-empty collections) by
    /// that literal prefix — not by shape alone.
    /// </summary>
    internal static bool IsRootError(ReleaseScanResult result) =>
        result.MainSets.Count == 0 && result.SampleFiles.Count == 0 && result.SubtitleSfvs.Count == 0
        && result.StoredFiles.Count == 0 && result.MusicSfvs.Count == 0
        && result.Warnings.Count == 1
        && result.Warnings[0].StartsWith("Cannot scan '", StringComparison.Ordinal);

    internal static string RootRelativeName(string releaseRoot, string fullPath) =>
        Path.GetRelativePath(releaseRoot, fullPath).Replace('\\', '/');

    /// <summary>
    /// The position where generated SAMPLE artifacts belong (excerpt's own SRS pass): immediately
    /// before whichever comes first — a pre-existing <c>.srs</c> (any path); ANY <c>.sfv</c> (main,
    /// non-main, or proof-linked — every sfv collectively marks the START of pass-10's tail, so
    /// samples must precede all of them, not just non-proof ones); or a
    /// <c>.rar</c> that is EITHER not under a proof directory (a conditional fix RAR) OR one that
    /// IS but has already been relocated next to its matching sfv by
    /// <see cref="ReleaseScanner.ApplyProofBeforeSfvReorder{T}"/> (<see cref="HasMatchingSfv"/>) —
    /// reproducing "right where the SRS pass injects" even when it produced nothing to anchor on.
    /// </summary>
    internal static int FindSampleArtifactSpliceIndex(List<StoredFileEntry> entries)
    {
        for (int i = 0; i < entries.Count; i++)
        {
            string name = entries[i].StoredName;
            string ext = Path.GetExtension(name);
            if (ext.Equals(".srs", StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }

            if (ext.Equals(".sfv", StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }

            if (ext.Equals(".rar", StringComparison.OrdinalIgnoreCase)
                && (!IsUnderProofDirectory(name) || HasMatchingSfv(name, entries)))
            {
                return i;
            }
        }

        return entries.Count;
    }

    /// <summary>
    /// The position where subtitle nested-SRRs belong (excerpt pass 9, AFTER a conditional fix RAR
    /// but BEFORE the final input-SFV pass): the first <c>.sfv</c> entry of ANY kind — main,
    /// non-main, or proof-linked — always marks the START of pass-10's tail (every sfv collectively
    /// lives there), naturally skipping past a fix RAR (<c>.rar</c>, not <c>.sfv</c>) that might
    /// precede it.
    /// </summary>
    internal static int FindSubtitleArtifactSpliceIndex(List<StoredFileEntry> entries)
    {
        for (int i = 0; i < entries.Count; i++)
        {
            if (Path.GetExtension(entries[i].StoredName).Equals(".sfv", StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }

        return entries.Count;
    }

    /// <summary>
    /// Whether a stored logical name (forward-slash-separated) sits under a
    /// <c>proof</c>/<c>proofs</c> DIRECTORY — the IMMEDIATE parent only, matching the scanner's
    /// rule-3/4 pardir classification exactly (ReleaseScanner.cs). An earlier version accepted ANY
    /// ancestor segment, broader than the scanner's own immediate-parent test.
    /// </summary>
    internal static bool IsUnderProofDirectory(string storedName)
    {
        int lastSlash = storedName.LastIndexOf('/');
        if (lastSlash < 0)
        {
            return false; // a root-level entry has no parent directory at all
        }

        string parent = storedName[..lastSlash];
        int parentSlash = parent.LastIndexOf('/');
        string immediateParent = parentSlash < 0 ? parent : parent[(parentSlash + 1)..];
        return immediateParent.Equals("proof", StringComparison.OrdinalIgnoreCase)
            || immediateParent.Equals("proofs", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Reconciles the splice anchor with the post-reorder final-SFV region: mirrors
    /// <see cref="ReleaseScanner.ApplyProofBeforeSfvReorder{T}"/>'s own mover predicate (stem, with
    /// its last 4 characters swapped to <c>.sfv</c>, matching some OTHER entry). A proof RAR
    /// satisfying this has either already been relocated next to its sfv
    /// by that reorder, or already sits adjacent to it (nothing to move) — either way it is
    /// logically part of the final-SFV tail, not its own early "proof-RAR category" position, and
    /// must count as a valid splice anchor even though <see cref="IsUnderProofDirectory"/> is true
    /// for it. An independently-discovered proof RAR with NO matching sfv (nothing to relocate
    /// against) has no such match and correctly stays un-anchored, in its true early position.
    /// </summary>
    internal static bool HasMatchingSfv(string storedName, List<StoredFileEntry> entries)
    {
        if (storedName.Length < 4)
        {
            return false;
        }

        string candidateSfv = storedName[..^4] + ".sfv";
        return entries.Any(e => string.Equals(e.StoredName, candidateSfv, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Per the excerpt, a RAR-backed sample is a lowercase-<c>.vob</c> file (case-SENSITIVE: a
    /// <c>.VOB</c> sample never matches) whose OWN leading bytes are the RAR marker <c>Rar!</c>.
    /// pyrescene reads this from the SRS's cached track signature bytes; reading the sample file's
    /// own leading bytes directly is equivalent (that is exactly where those signature bytes come
    /// from) and needs no SRS-parsing dependency here.
    /// </summary>
    internal static bool IsRarBackedVobSample(string samplePath)
    {
        if (!samplePath.EndsWith(".vob", StringComparison.Ordinal))
        {
            return false;
        }

        try
        {
            using var fs = new FileStream(samplePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            Span<byte> head = stackalloc byte[4];
            return fs.Read(head) == 4 && head[0] == (byte)'R' && head[1] == (byte)'a' && head[2] == (byte)'r' && head[3] == (byte)'!';
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// Root-relative logical name for a folder-mode generated artifact's SOURCE, falling back to
    /// <paramref name="conventionalDir"/>/&lt;basename&gt; when the source lives OUTSIDE the release
    /// root — ExtraSampleFiles/ExtraSubtitleSfvFiles are shared with the
    /// file-mode Advanced tab's "Add Sample"/"Add Subtitle" commands, so a folder-mode run can
    /// still see a manually-added, out-of-root source (the supported "artifact from an unextracted
    /// release" case). Mirrors the pre-existing <see cref="GeneratedStoredName"/>'s fallback (used
    /// by the wizard/Advanced-tab paths) but keeps the source's own extension — callers needing a
    /// different one use <see cref="FolderRelativeStem"/> instead.
    /// </summary>
    internal static string FolderRelativeName(string releaseRoot, string sourcePath, string conventionalDir)
    {
        string relative = Path.GetRelativePath(releaseRoot, sourcePath);
        return Path.IsPathRooted(relative) || relative.StartsWith("..", StringComparison.Ordinal)
            ? $"{conventionalDir}/{Path.GetFileName(sourcePath)}"
            : relative.Replace('\\', '/');
    }

    /// <summary>Like <see cref="FolderRelativeName"/>, with the extension stripped.</summary>
    internal static string FolderRelativeStem(string releaseRoot, string sourcePath, string conventionalDir)
    {
        string name = FolderRelativeName(releaseRoot, sourcePath, conventionalDir);
        int lastDot = name.LastIndexOf('.');
        return lastDot < 0 ? name : name[..lastDot];
    }

    /// <summary>
    /// Stored name for a generated .srs/.srr: the release-relative path (with the new extension)
    /// when the source lives under the release, otherwise the conventional
    /// <paramref name="conventionalDir"/>/&lt;name&gt;<paramref name="newExtension"/> — manually-added
    /// samples/subtitles from an unextracted release sit outside the release folder.
    /// </summary>
    internal static string GeneratedStoredName(string releaseDir, string sourcePath, string newExtension, string conventionalDir)
    {
        string relative = Path.GetRelativePath(releaseDir, sourcePath);
        string name = Path.IsPathRooted(relative) || relative.StartsWith("..", StringComparison.Ordinal)
            ? $"{conventionalDir}/{Path.GetFileName(sourcePath)}"
            : relative.Replace('\\', '/');
        return Path.ChangeExtension(name, newExtension);
    }

    internal static List<string> DiscoverRARVolumes(string firstRARPath)
    {
        string dir = Path.GetDirectoryName(firstRARPath) ?? ".";
        string baseName = Path.GetFileNameWithoutExtension(firstRARPath);

        var volumes = new List<string>();

        if (baseName.Contains(".part", StringComparison.OrdinalIgnoreCase))
        {
            string pattern = baseName[..baseName.LastIndexOf(".part", StringComparison.OrdinalIgnoreCase)];
            foreach (string file in Directory.GetFiles(dir, $"{pattern}.part*.rar"))
            {
                volumes.Add(file);
            }
        }
        else
        {
            volumes.Add(firstRARPath);

            // Old-style continuation volumes: .r00, .r01, … .r99, .s00, … .z99. Enumerate the
            // directory rather than walking the sequence, so a gap in the numbering doesn't
            // silently truncate the set (the loop here previously stopped at the first missing one).
            foreach (string file in Directory.GetFiles(dir, baseName + ".*"))
            {
                if (SceneFileTypes.IsOldStyleRARVolumeExtension(Path.GetExtension(file)))
                {
                    volumes.Add(file);
                }
            }
        }

        volumes.Sort(RARVolumeNameComparer.Instance);
        return volumes;
    }
}
