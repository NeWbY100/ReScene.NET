using ReScene.NET.Models;

namespace ReScene.NET.Helpers;

/// <summary>
/// Pure, side-effect-light helpers that turn user input into <see cref="FieldStatus"/>
/// guidance and suggested values. No WPF dependencies — unit-testable.
/// </summary>
public static class FieldGuidance
{
    private static readonly HashSet<string> _knownSampleExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mkv", ".avi", ".mp4", ".m4v", ".mov", ".wmv", ".vob", ".mpg", ".mpeg",
        ".ts", ".m2ts", ".flac", ".mp3", ".ogg", ".wav"
    };

    /// <summary>
    /// Returns <paramref name="inputPath"/> with its extension replaced by
    /// <paramref name="newExtension"/>, in the same directory. Empty input yields empty output.
    /// </summary>
    public static string SuggestSiblingPath(string inputPath, string newExtension)
    {
        if (string.IsNullOrWhiteSpace(inputPath))
        {
            return string.Empty;
        }

        string dir = Path.GetDirectoryName(inputPath) ?? ".";
        string name = Path.GetFileNameWithoutExtension(inputPath);
        return Path.Combine(dir, name + newExtension);
    }

    /// <summary>
    /// Sanity-checks a chosen full media file against the sample it should contain.
    /// The full media must be at least as large as the sample.
    /// </summary>
    public static FieldStatus EvaluateMediaAgainstSample(long mediaSize, long sampleSize)
    {
        if (sampleSize <= 0)
        {
            return FieldStatus.None;
        }

        return mediaSize >= sampleSize
            ? FieldStatus.Ok("Media is larger than the sample — looks right.")
            : FieldStatus.Warning("Media is smaller than the sample; this is likely the wrong file.");
    }

    /// <summary>
    /// Describes a sample file by extension and size, warning on unrecognized media types.
    /// </summary>
    public static FieldStatus DescribeSample(string extension, long sizeBytes)
    {
        string label = extension.TrimStart('.').ToUpperInvariant();
        return _knownSampleExtensions.Contains(extension)
            ? FieldStatus.Ok($"{label} sample, {FormatUtilities.FormatSize(sizeBytes)}")
            : FieldStatus.Warning($"Unrecognized media type ({label}); SRS may not support it.");
    }

    /// <summary>
    /// Counts RAR archive files (.rar and old-style .r00/.r01… and .partN.rar) in a directory.
    /// Returns 0 when the directory is missing.
    /// </summary>
    public static int CountReleaseArchives(string directory)
    {
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
        {
            return 0;
        }

        int count = 0;
        foreach (string file in Directory.EnumerateFiles(directory))
        {
            string ext = Path.GetExtension(file);
            if (ext.Equals(".rar", StringComparison.OrdinalIgnoreCase) || IsOldStyleVolume(ext))
            {
                count++;
            }
        }

        return count;
    }

    // Matches ".r00".."r999": 'r' followed by digits.
    private static bool IsOldStyleVolume(string ext)
    {
        if (ext.Length < 3 || (ext[1] != 'r' && ext[1] != 'R'))
        {
            return false;
        }

        for (int i = 2; i < ext.Length; i++)
        {
            if (!char.IsDigit(ext[i]))
            {
                return false;
            }
        }

        return true;
    }
}
