using ReScene.SRR;

namespace ReScene.App.Core.Services;

/// <summary>
/// Resolves a file name taken from parsed SRR/SRS metadata into a write destination beneath a
/// user-chosen directory, refusing any name that would land outside it.
/// </summary>
/// <remarks>
/// <para>
/// This exists because <see cref="Path.Combine(string, string)"/> is not a containment primitive
/// and reads as though it were. Two of its behaviours are the whole problem:
/// </para>
/// <list type="bullet">
/// <item><description>
/// Given an ABSOLUTE second argument it returns that argument unchanged, silently discarding the
/// directory the caller chose — so a metadata name of <c>C:\Windows\System32\x.dll</c> or
/// <c>/etc/cron.d/x</c> becomes the destination outright.
/// </description></item>
/// <item><description>
/// It does not reject <c>..</c> segments, so <c>../../x.mkv</c> climbs out of the directory.
/// </description></item>
/// </list>
/// <para>
/// The names reaching here come from inside a file the user merely opened, so they are attacker-
/// controlled in exactly the way a downloaded release is. Callers must treat a
/// <see langword="false"/> result as a per-item failure and report it, never as a reason to fall
/// back to the raw name.
/// </para>
/// </remarks>
internal static class MetadataOutputPath
{
    /// <summary>DOS device names, which denote devices rather than files in every directory.</summary>
    private static readonly HashSet<string> _reservedDeviceNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    /// <summary>
    /// Resolves <paramref name="metadataName"/> beneath <paramref name="outputDirectory"/>.
    /// </summary>
    /// <returns>
    /// <see langword="true"/> with <paramref name="fullPath"/> set to a path inside
    /// <paramref name="outputDirectory"/>; otherwise <see langword="false"/> with
    /// <paramref name="error"/> explaining why the name was refused.
    /// </returns>
    public static bool TryResolve(
        string outputDirectory,
        string metadataName,
        out string fullPath,
        out string error)
    {
        fullPath = string.Empty;
        error = string.Empty;

        if (string.IsNullOrWhiteSpace(outputDirectory))
        {
            error = "No output directory was selected.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(metadataName))
        {
            error = "The file name recorded in the metadata is empty.";
            return false;
        }

        // Rooted names are the sharpest case: Path.Combine would return this verbatim and drop the
        // chosen directory entirely. Checked before normalization so a Windows-syntax name is
        // caught on every platform — Path.IsPathRooted recognises neither "C:\x" nor the
        // drive-relative "D:x" when running on Linux, and this app is cross-platform.
        //
        // Only the DRIVE-LETTER position counts here. Rejecting a colon anywhere would refuse
        // ordinary POSIX names like "Movie: Part 1.mkv", where ':' is a legal character.
        //
        // Known false positive: a POSIX name whose FIRST character is a single letter followed by
        // a colon — "Q: The Winged Serpent.mkv" — is indistinguishable from a Windows
        // drive-relative path and is refused. Accepted deliberately: a false refusal costs the
        // user one rename, a false accept writes outside the folder they chose.
        bool hasDriveQualifier = metadataName.Length >= 2
            && metadataName[1] == ':'
            && char.IsAsciiLetter(metadataName[0]);

        if (Path.IsPathRooted(metadataName)
            || hasDriveQualifier
            || metadataName.StartsWith('/')
            || metadataName.StartsWith('\\'))
        {
            error = $"The metadata names an absolute path, which would write outside the chosen folder: {metadataName}";
            return false;
        }

        string normalized = metadataName
            .Replace('\\', Path.DirectorySeparatorChar)
            .Replace('/', Path.DirectorySeparatorChar);

        if (OperatingSystem.IsWindows() && !IsWritableWindowsName(normalized, metadataName, ref error))
        {
            return false;
        }

        string root;
        string candidate;
        try
        {
            root = Path.GetFullPath(outputDirectory);
            candidate = Path.GetFullPath(Path.Combine(root, normalized));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            error = $"The metadata file name cannot be resolved to a valid path: {metadataName}";
            return false;
        }

        // Lexical containment. Comparing the RELATIVE result rather than a string prefix keeps
        // this correct on case-sensitive filesystems, where a prefix check that ignores case would
        // accept a sibling directory differing only in case.
        if (!IsUnderRoot(root, candidate))
        {
            error = $"The metadata file name escapes the chosen folder: {metadataName}";
            return false;
        }

        // Lexical containment is NOT sufficient. Path.GetFullPath does not resolve links, so a
        // junction or symlink already sitting inside the chosen directory — say "shared" pointing
        // elsewhere — makes "shared/victim.mkv" pass the check above while writing outside. Re-check
        // through the filesystem, resolving the root and the candidate's deepest EXISTING ancestor
        // (the candidate itself normally does not exist yet, and the resolver requires a target).
        //
        // Skipped when the root does not exist: nothing can be linked inside a directory that is
        // not there, and the resolver would simply throw. Callers pass a directory the user chose,
        // so this is the unusual case.
        if (!Directory.Exists(root))
        {
            fullPath = candidate;
            return true;
        }

        try
        {
            string resolvedRoot = SrrNameCanonicalizer.GetFinalPath(root);
            string resolvedAncestor = SrrNameCanonicalizer.GetFinalPath(DeepestExistingAncestor(candidate));

            if (!string.Equals(resolvedRoot, resolvedAncestor, StringComparison.Ordinal)
                && !IsUnderRoot(resolvedRoot, resolvedAncestor))
            {
                error = $"The metadata file name resolves outside the chosen folder through a link: {metadataName}";
                return false;
            }
        }
        catch (Exception ex) when (ex is SrrNameException or IOException or UnauthorizedAccessException or ArgumentException)
        {
            error = $"The destination for '{metadataName}' could not be verified against the chosen folder: {ex.Message}";
            return false;
        }

        fullPath = candidate;
        return true;
    }

    /// <summary>Whether <paramref name="candidate"/> sits strictly beneath <paramref name="root"/>.</summary>
    private static bool IsUnderRoot(string root, string candidate)
    {
        string relative = Path.GetRelativePath(root, candidate);

        // GetRelativePath returns a ROOTED path when no relative path exists (a different drive or
        // root), and a ".."-prefixed one when the target sits outside — either means escape.
        return !string.IsNullOrWhiteSpace(relative)
            && relative != "."
            && relative != ".."
            && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            && !Path.IsPathRooted(relative);
    }

    /// <summary>The nearest ancestor of <paramref name="path"/> that exists on disk.</summary>
    private static string DeepestExistingAncestor(string path)
    {
        string current = path;
        while (!Directory.Exists(current) && !File.Exists(current))
        {
            string? parent = Path.GetDirectoryName(current);
            if (string.IsNullOrEmpty(parent) || string.Equals(parent, current, StringComparison.Ordinal))
            {
                break;
            }

            current = parent;
        }

        return current;
    }

    /// <summary>
    /// Rejects names that Windows does not treat as ordinary files, which the containment checks
    /// above cannot see because they are lexically inside the chosen directory.
    /// </summary>
    private static bool IsWritableWindowsName(string normalized, string original, ref string error)
    {
        // Any colon past the drive-letter position opens an alternate data stream, so
        // "movie.mkv:restored" writes a hidden stream instead of the file the user expects.
        if (normalized.Contains(':', StringComparison.Ordinal))
        {
            error = $"The metadata file name contains ':', which names an alternate data stream on Windows: {original}";
            return false;
        }

        foreach (string segment in normalized.Split(Path.DirectorySeparatorChar))
        {
            if (segment.Length == 0)
            {
                continue;
            }

            // A device name is reserved with OR without an extension, and in every directory.
            string stem = Path.GetFileNameWithoutExtension(segment);
            if (_reservedDeviceNames.Contains(stem))
            {
                error = $"The metadata file name uses the reserved Windows device name '{stem}': {original}";
                return false;
            }
        }

        return true;
    }
}
