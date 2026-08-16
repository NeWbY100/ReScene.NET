namespace ReScene.App.Core.ViewModels.Reconstruction;

/// <summary>
/// The two reserved subtrees under a reconstruction output directory (<c>output</c> and
/// <c>.rescene-work</c>): what to say before clearing them, whether there is anything to clear, and
/// the clearing itself. Unrelated files at the output root are never touched (#4).
/// </summary>
internal static class ReservedOutputTreeManager
{
    /// <summary>
    /// The confirmation shown before the pre-run cleanup: it clears only the two reserved subtrees
    /// (<c>output</c> and <c>.rescene-work</c>) under <paramref name="outputPath"/>, preserving unrelated
    /// root files. Shared verbatim by the Start command and the Beginner wizard so the two never drift.
    /// </summary>
    public static string ConfirmText(string outputPath) =>
        $"The output directory already contains reconstruction output:\n\n{outputPath}\n\n" +
        $"Its '{ReconstructionPathGuard.OutputDirName}' and '{ReconstructionPathGuard.ScratchDirName}' subfolders " +
        "— including any kept work files — will be cleared before starting (other files are left untouched). Continue?";

    /// <summary>
    /// Whether either reserved subtree under <paramref name="outputPath"/> currently holds content the
    /// pre-run cleanup would clear. Shared by Start and the Beginner wizard so both prompt on the same
    /// condition. Fails closed (returns true → prompt) if the roots cannot be resolved.
    /// </summary>
    public static bool HasReconstructionArtifacts(string outputPath)
    {
        try
        {
            (string outputRoot, string scratchRoot) = ReconstructionPathGuard.ResolveReservedRoots(outputPath);
            return HasContent(outputRoot) || HasContent(scratchRoot);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return true;
        }

        static bool HasContent(string dir) => Directory.Exists(dir) && Directory.EnumerateFileSystemEntries(dir).Any();
    }

    /// <summary>
    /// Clears the two reserved subtrees (<c>output</c> + <c>.rescene-work</c>) under
    /// <paramref name="outputPath"/>, resolved through the path guard so a junction cannot redirect the
    /// delete. Unrelated files at the output root are untouched (#4). Returns false (after surfacing the
    /// error) if resolution or deletion fails - the catch does not distinguish the two.
    /// </summary>
    /// <param name="outputPath">The reconstruction output directory.</param>
    /// <param name="log">Appends a line to the run log.</param>
    /// <param name="showError">
    /// Surfaces the failure to the user, as (title, message). A separate callback from
    /// <paramref name="log"/> because the failure path does BOTH, in that order — collapsing them
    /// would drop the dialog.
    /// </param>
    public static bool ClearReservedSubtrees(string outputPath, Action<string> log, Action<string, string> showError)
    {
        try
        {
            (string outputRoot, string scratchRoot) = ReconstructionPathGuard.ResolveReservedRoots(outputPath);
            DeleteIfExists(outputRoot);
            DeleteIfExists(scratchRoot);
            log("Output directory cleaned.");
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            log($"Failed to clean output directory: {ex.Message}");
            showError("Error", $"Failed to clean output directory:\n{ex.Message}");
            return false;
        }

        static void DeleteIfExists(string dir)
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
    }
}
