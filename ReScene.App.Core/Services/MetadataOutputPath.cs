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
        // Only the DRIVE-LETTER position counts. Rejecting a colon anywhere would refuse ordinary
        // POSIX names like "Movie: Part 1.mkv", where ':' is a perfectly legal character.
        //
        // Known false positive: a POSIX name whose FIRST character is a single letter followed by
        // a colon — "Q: The Winged Serpent.mkv", "M: Eine Stadt sucht einen Mörder.mkv" — is
        // indistinguishable from a Windows drive-relative path, and is refused. Accepted
        // deliberately: this app is cross-platform, an SRS authored on Windows can genuinely carry
        // "D:name", and a false refusal costs the user one rename while a false ACCEPT writes
        // outside the folder they chose.
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

        // Authoritative containment test. Comparing the RELATIVE result rather than a string
        // prefix keeps this correct on case-sensitive filesystems, where a prefix check that
        // ignores case would accept a sibling directory differing only in case.
        string relative = Path.GetRelativePath(root, candidate);
        if (string.IsNullOrWhiteSpace(relative)
            || relative == "."
            || relative == ".."
            || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            || Path.IsPathRooted(relative))
        {
            error = $"The metadata file name escapes the chosen folder: {metadataName}";
            return false;
        }

        fullPath = candidate;
        return true;
    }
}
