using ReScene.NET.Models;

namespace ReScene.NET.ViewModels.Reconstruction;

/// <summary>
/// Pure field-validation helpers for the RAR Reconstructor's path inputs. Each method takes a
/// raw path string and returns the <see cref="FieldStatus"/> the view should show, preserving the
/// exact severities and message text the view-model previously computed inline.
/// </summary>
internal static class ReconstructorFieldGuidance
{
    /// <summary>
    /// Status for the WinRAR installations directory: the folder containing per-version WinRAR
    /// subfolders (a directory, not a path to rar.exe).
    /// </summary>
    public static FieldStatus EvaluateWinRarPath(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return FieldStatus.Warning("Required — choose the WinRAR installations folder.");
        }

        if (!Directory.Exists(value))
        {
            return FieldStatus.Error("This WinRAR directory does not exist.");
        }

        return FieldStatus.Ok("WinRAR installations directory selected.");
    }

    /// <summary>Status for the release/source path (a directory or single file of unpacked contents).</summary>
    public static FieldStatus EvaluateReleasePath(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return FieldStatus.Warning("Required — choose the release folder.");
        }

        if (!Directory.Exists(value) && !File.Exists(value))
        {
            return FieldStatus.Error("This path does not exist.");
        }

        return FieldStatus.Ok("Source files selected.");
    }

    /// <summary>Status for the verification (.sfv/.sha1) file path.</summary>
    public static FieldStatus EvaluateVerificationPath(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return FieldStatus.Warning("Required — choose the .sfv or .sha1 to verify against.");
        }

        if (!File.Exists(value))
        {
            return FieldStatus.Error("This .sfv/.sha1 file does not exist.");
        }

        return FieldStatus.Info("Reconstructed archives will be verified against this file.");
    }

    /// <summary>
    /// Status for the output directory (where rebuilt archives are written). It is created at
    /// Start if missing, so only emptiness is flagged here.
    /// </summary>
    public static FieldStatus EvaluateOutputPath(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return FieldStatus.Warning("Required — choose the output folder.");
        }

        return FieldStatus.Ok("Output folder set.");
    }

    /// <summary>
    /// Whether the Paths tab still needs attention: any of the four required paths (WinRAR,
    /// Release, Verify, Output) is empty or invalid, so the run could not start. Drives the
    /// warning glyph on the Paths sub-tab header.
    /// </summary>
    public static bool PathsNeedAttention(
        string winRarPath, string releasePath, string verificationPath, string outputPath)
    {
        return NeedsAttention(EvaluateWinRarPath(winRarPath))
            || NeedsAttention(EvaluateReleasePath(releasePath))
            || NeedsAttention(EvaluateVerificationPath(verificationPath))
            || NeedsAttention(EvaluateOutputPath(outputPath));
    }

    /// <summary>A field needs attention unless its value is accepted (Ok) or merely informational (Info).</summary>
    private static bool NeedsAttention(FieldStatus status) =>
        status.State is not (FieldState.Ok or FieldState.Info);

    /// <summary>
    /// Whether Start would show the subdirectory modified-date warning: the release directory has
    /// subdirectories but the imported SRR carried no directory timestamps to restore.
    /// </summary>
    public static bool NeedsSubdirTimestampWarning(string releasePath, int importedDirTimestampCount)
    {
        try
        {
            return Directory.Exists(releasePath)
                && Directory.EnumerateDirectories(releasePath).Any()
                && importedDirTimestampCount == 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Unreadable directory — let Start surface the real error.
            return false;
        }
    }

    /// <summary>
    /// True when two folder paths are the same folder, or one is nested inside the other —
    /// configurations where deleting the output folder's contents would also remove the release.
    /// Returns false for empty or unparseable paths (the per-path validation handles those).
    /// </summary>
    public static bool PathsOverlap(string pathA, string pathB)
    {
        if (string.IsNullOrWhiteSpace(pathA) || string.IsNullOrWhiteSpace(pathB))
        {
            return false;
        }

        string a, b;
        try
        {
            a = AppendSeparator(Path.GetFullPath(pathA));
            b = AppendSeparator(Path.GetFullPath(pathB));
        }
        catch (Exception ex) when (ex is ArgumentException or PathTooLongException or NotSupportedException)
        {
            return false;
        }

        return a.Equals(b, StringComparison.OrdinalIgnoreCase)
            || a.StartsWith(b, StringComparison.OrdinalIgnoreCase)
            || b.StartsWith(a, StringComparison.OrdinalIgnoreCase);
    }

    private static string AppendSeparator(string path) =>
        path.EndsWith(Path.DirectorySeparatorChar) ? path : path + Path.DirectorySeparatorChar;
}
