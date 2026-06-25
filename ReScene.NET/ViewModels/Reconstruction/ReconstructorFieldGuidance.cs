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
            return FieldStatus.None;
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
            return FieldStatus.None;
        }

        if (!Directory.Exists(value) && !File.Exists(value))
        {
            return FieldStatus.Error("This path does not exist.");
        }

        return FieldStatus.Ok("Source files selected.");
    }

    /// <summary>Status for the verification (.srr/.sfv/.sha1) file path.</summary>
    public static FieldStatus EvaluateVerificationPath(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return FieldStatus.None;
        }

        if (!File.Exists(value))
        {
            return FieldStatus.Error("This .srr file does not exist.");
        }

        return FieldStatus.Info("Reconstructed archives will be verified against this SRR.");
    }

    /// <summary>
    /// Whether the Paths tab still needs attention: any of the four paths required for a
    /// successful run (WinRAR, Release, Verify, Output) is empty or invalid, so the run could
    /// not start. Drives the warning glyph on the Paths sub-tab header.
    /// </summary>
    public static bool PathsNeedAttention(
        string winRarPath, string releasePath, string verificationPath, string outputPath)
    {
        // The Evaluate* helpers return None for an empty value and Error for an invalid one,
        // so "None or Error" covers both "empty" and "missing" without re-checking the strings.
        // A valid value returns Ok/Info and does not trigger the glyph. Output has no existence
        // check today (it is created at Start), so only its emptiness matters.
        return EvaluateWinRarPath(winRarPath).State is FieldState.None or FieldState.Error
            || EvaluateReleasePath(releasePath).State is FieldState.None or FieldState.Error
            || EvaluateVerificationPath(verificationPath).State is FieldState.None or FieldState.Error
            || string.IsNullOrWhiteSpace(outputPath);
    }

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
}
