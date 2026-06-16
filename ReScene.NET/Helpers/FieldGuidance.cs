using ReScene.NET.Models;

namespace ReScene.NET.Helpers;

/// <summary>
/// Pure, side-effect-light helpers that turn user input into <see cref="FieldStatus"/>
/// guidance and suggested values. No WPF dependencies — unit-testable.
/// </summary>
public static class FieldGuidance
{
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
    /// Suggests what to pre-fill in a "save output" dialog. Returns <paramref name="outputPath"/>
    /// itself when it already names a file; when it is empty or only holds a directory (e.g. the
    /// settings' default output directory), derives the file name from <paramref name="inputPath"/>
    /// instead. Returns <see langword="null"/> when there is nothing to suggest.
    /// </summary>
    public static string? SuggestSaveFileName(string? outputPath, string? inputPath, string newExtension)
    {
        bool hasOutput = !string.IsNullOrWhiteSpace(outputPath);
        bool outputIsDirectory = hasOutput && Directory.Exists(outputPath);

        if (hasOutput && !outputIsDirectory)
        {
            return outputPath;
        }

        if (string.IsNullOrWhiteSpace(inputPath))
        {
            return null;
        }

        return outputIsDirectory
            ? Path.Combine(outputPath!, Path.GetFileNameWithoutExtension(inputPath) + newExtension)
            : SuggestSiblingPath(inputPath, newExtension);
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
        return SceneFileTypes.IsMediaExtension(extension)
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
            if (SceneFileTypes.IsRarVolumeExtension(ext))
            {
                count++;
            }
        }

        return count;
    }
}
