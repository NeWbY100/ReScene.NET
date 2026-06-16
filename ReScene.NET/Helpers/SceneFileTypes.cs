namespace ReScene.NET.Helpers;

/// <summary>
/// Single source of truth for the scene-related file-type sets the app reasons about:
/// RAR volume detection and SRS-supported media containers. Centralizes sets that were
/// previously copy-pasted (and drifted) across <see cref="ReleaseFileScanner"/>,
/// <see cref="FieldGuidance"/>, and the SRR creator view model.
/// </summary>
/// <remarks>
/// These sets are deliberately authored, not derived from the pipe-delimited
/// <c>FileDialogFilters</c> UI strings.
/// </remarks>
internal static class SceneFileTypes
{
    /// <summary>
    /// Media container extensions the SRS layer supports (mirrors the library's
    /// <c>ISOMediaExtractor</c> / <c>SRSWriter.DetectContainerType</c> coverage).
    /// </summary>
    public static readonly IReadOnlySet<string> MediaExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        ".avi", ".mkv", ".mp4", ".m4v", ".mov", ".wmv",
        ".flac", ".mp3",
        ".vob", ".m2ts", ".ts", ".mpg", ".mpeg", ".evo", ".m2v"
    };

    /// <summary>
    /// Returns <see langword="true"/> when <paramref name="extension"/> (with leading dot)
    /// is an SRS-supported media container.
    /// </summary>
    public static bool IsMediaExtension(string extension) => MediaExtensions.Contains(extension);

    /// <summary>
    /// Returns <see langword="true"/> when <paramref name="extension"/> (with leading dot)
    /// names a RAR volume: <c>.rar</c> or an old-style continuation volume — a letter
    /// followed by two digits (e.g. <c>.r00</c>, <c>.s01</c>). Mirrors the library's
    /// internal <c>RARVolumeIdentifier</c> old-style rule.
    /// </summary>
    public static bool IsRarVolumeExtension(string extension)
        => extension.Equals(".rar", StringComparison.OrdinalIgnoreCase)
            || IsOldStyleRarVolumeExtension(extension);

    /// <summary>
    /// Returns <see langword="true"/> for an old-style RAR continuation-volume extension only:
    /// a dot, a letter, then two digits (e.g. <c>.r00</c>, <c>.s01</c>). Excludes the first
    /// volume's <c>.rar</c>. Mirrors the library's internal <c>RARVolumeIdentifier</c> old-style rule.
    /// </summary>
    public static bool IsOldStyleRarVolumeExtension(string extension)
        => extension.Length == 4
            && extension[0] == '.'
            && char.IsLetter(extension[1])
            && char.IsDigit(extension[2])
            && char.IsDigit(extension[3]);
}
