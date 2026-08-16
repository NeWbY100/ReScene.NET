namespace ReScene.App.Core.Services;

/// <summary>
/// Deterministic release-tree traversal that the release scanner walks to classify a
/// release folder. [DIVERGENCE: determinism] — pyrescene's
/// byte order is raw <c>os.walk</c> enumeration (filesystem-dependent, not reproducible in
/// general; pyrescene-rules-excerpt.txt, <c>get_files</c>) — this emulation instead
/// sorts each directory level's subdirectory and file names with <see cref="StringComparer.Ordinal"/>
/// (case-sensitive) and emits a directory's files before descending into its subdirectories,
/// top-down, so identical trees produce identical output regardless of filesystem enumeration
/// order. All scanner category passes consume this order.
/// </summary>
public static class ReleaseTraversal
{
    /// <summary>
    /// Enumerates every file under <paramref name="root"/> in the deterministic order documented
    /// on this class. Results are always full paths — a relative <paramref name="root"/> is
    /// resolved against the current directory once, up front, so callers never get
    /// CWD-dependent output. A directory (or a child's metadata) that fails to read (permission
    /// denied, I/O error, disappears mid-walk) is recorded as a <see cref="TraversalIssue"/> and
    /// skipped; the traversal continues with the remaining directories. If <paramref name="root"/>
    /// itself fails to enumerate, the result carries <see cref="TraversalResult.RootFailed"/> =
    /// <see langword="true"/> with no files. <paramref name="ct"/> is checked before any I/O and
    /// again per directory.
    /// </summary>
    public static TraversalResult EnumerateFiles(string root, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        // Full-path contract (Interfaces block): Directory.GetFiles/GetDirectories append results
        // to whatever path they're given, so a relative root would otherwise leak into every
        // result path and become dependent on the process's current directory.
        root = Path.GetFullPath(root);

        if (!TryReadDirectory(root, out string[] dirFiles, out string[] subdirs, out string? message))
        {
            return new TraversalResult([], [new TraversalIssue(root, message!)], RootFailed: true);
        }

        var files = new List<string>();
        var issues = new List<TraversalIssue>();
        EmitFilesAndDescend(dirFiles, subdirs, files, issues, ct);
        return new TraversalResult(files, issues, RootFailed: false);
    }

    /// <summary>
    /// Filters <paramref name="files"/> to those matching <paramref name="extension"/>
    /// (case-insensitive), preserving the input order.
    /// </summary>
    public static IReadOnlyList<string> FilterByExtension(IReadOnlyList<string> files, string extension) =>
        [.. files.Where(f => string.Equals(Path.GetExtension(f), extension, StringComparison.OrdinalIgnoreCase))];

    private static void Walk(string dir, List<string> files, List<TraversalIssue> issues, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        if (!TryReadDirectory(dir, out string[] dirFiles, out string[] subdirs, out string? message))
        {
            issues.Add(new TraversalIssue(dir, message!)); // scanner maps this to a Warning
            return;
        }

        EmitFilesAndDescend(dirFiles, subdirs, files, issues, ct);
    }

    /// <summary>
    /// Shared tail of a successfully-read directory: sort this level's names ordinally, emit its
    /// files, then descend into its subdirectories (skipping reparse points). Called once for the
    /// root (from <see cref="EnumerateFiles"/>) and once per descendant (from <see cref="Walk"/>)
    /// so the ordering logic isn't duplicated between the two call sites.
    /// </summary>
    private static void EmitFilesAndDescend(
        string[] dirFiles, string[] subdirs, List<string> files, List<TraversalIssue> issues, CancellationToken ct)
    {
        Array.Sort(dirFiles, StringComparer.Ordinal);
        Array.Sort(subdirs, StringComparer.Ordinal);

        // Files were emitted unconditionally while the loop below skipped linked DIRECTORIES, so
        // the two halves of the same rule disagreed. A linked FILE is the same hazard and a
        // sharper one: its target's bytes are read and stored into the SRR as though they were
        // part of the release, from wherever the link points — outside the selected root
        // included. Skipped silently, exactly as a linked directory is: the issue channel is
        // rendered to the user as "Unreadable: <path> (<message>)", which a deliberate skip is not.
        foreach (string file in dirFiles)
        {
            // One metadata call per file, so honour cancellation here rather than only once per
            // directory: a folder with tens of thousands of entries on a network share would
            // otherwise keep issuing requests for a scan the user already abandoned.
            ct.ThrowIfCancellationRequested();

            if (IsLink(file, isDirectory: false, issues, out bool fileFailed) || fileFailed)
            {
                continue;
            }

            files.Add(file);
        }

        foreach (string sub in subdirs)
        {
            // pyrescene's os.walk does not follow directory links by default. A directory that
            // could not be INSPECTED is skipped too — the previous code returned from its catch,
            // and dropping that (by discarding the failure flag) would descend into a junction
            // whose target was never checked, which is the exact escape this guard exists to stop.
            if (IsLink(sub, isDirectory: true, issues, out bool subFailed) || subFailed)
            {
                continue;
            }

            Walk(sub, files, issues, ct);
        }
    }

    /// <summary>
    /// Whether <paramref name="path"/> is a symbolic link or junction. Sets
    /// <paramref name="failed"/> when the entry could not be inspected at all, in which case it
    /// has already been recorded as an issue.
    /// </summary>
    /// <remarks>
    /// Tests <see cref="FileSystemInfo.LinkTarget"/> rather than
    /// <see cref="FileAttributes.ReparsePoint"/>. That attribute is set on far more than links:
    /// OneDrive Files-On-Demand placeholders and Windows Server data-deduplicated files both carry
    /// it while reading back as perfectly ordinary content. Testing the attribute per FILE would
    /// therefore yield ZERO files for any release inside a OneDrive-synced folder — and the
    /// directory half had the same flaw, costing a whole subtree. <c>LinkTarget</c> is non-null
    /// only for an actual link, which is also what pyrescene's <c>os.walk</c> discriminates on.
    /// </remarks>
    private static bool IsLink(string path, bool isDirectory, List<TraversalIssue> issues, out bool failed)
    {
        failed = false;
        try
        {
            FileSystemInfo info = isDirectory ? new DirectoryInfo(path) : new FileInfo(path);
            return info.LinkTarget is not null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // A child that disappears or becomes unreadable between the directory listing and this
            // call must degrade to an Issue, not crash the whole traversal (class contract).
            issues.Add(new TraversalIssue(path, e.Message));
            failed = true;
            return false;
        }
    }

    /// <summary>
    /// Reads one directory's immediate files and subdirectories. Returns <see langword="false"/>
    /// with <paramref name="message"/> set on any read failure, leaving the classification of that
    /// failure (root vs. descendant) to the caller.
    /// </summary>
    private static bool TryReadDirectory(
        string dir, out string[] dirFiles, out string[] subdirs, out string? message)
    {
        try
        {
            dirFiles = Directory.GetFiles(dir);
            subdirs = Directory.GetDirectories(dir);
            message = null;
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            dirFiles = [];
            subdirs = [];
            message = e.Message;
            return false;
        }
    }
}
