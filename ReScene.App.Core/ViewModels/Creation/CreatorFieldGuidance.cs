using ReScene.App.Core.Helpers;
using ReScene.App.Core.Models;

namespace ReScene.App.Core.ViewModels.Creation;

/// <summary>
/// Computes the Creator's per-field guidance: the input field's status line and the action hint
/// under the primary button. Both return the value the view-model assigns rather than assigning it,
/// so the bound properties stay on the view-model where the generators and the view expect them.
/// </summary>
/// <remarks>
/// <see cref="BuildActionHint"/> is a pure calculation over its arguments.
/// <see cref="BuildInputStatus"/> is not: it probes the filesystem
/// (<see cref="File.Exists(string)"/>, plus an archive count over the release directory). It takes
/// its inputs as parameters and keeps reading the filesystem exactly where it did before, rather
/// than pretending to a purity it does not have.
/// </remarks>
internal static class CreatorFieldGuidance
{
    /// <summary>
    /// The input field's status for <paramref name="value"/>: none when blank, an error when the
    /// file does not exist, and otherwise an Ok/Warning describing how many archive files sit in the
    /// release directory alongside it.
    /// </summary>
    public static FieldStatus BuildInputStatus(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return FieldStatus.None;
        }

        if (!File.Exists(value))
        {
            return FieldStatus.Error("This file does not exist.");
        }

        string releaseDir = Path.GetDirectoryName(value) ?? ".";
        string releaseName = Path.GetFileName(releaseDir);
        int archiveCount = FieldGuidance.CountReleaseArchives(releaseDir);

        return archiveCount > 0
            ? FieldStatus.Ok($"Release \"{releaseName}\" — {archiveCount} archive file(s) in this folder.")
            : FieldStatus.Warning($"No .rar volumes found in \"{releaseName}\". An SRR is built from the release's .rar files — they need to be in this folder next to the .sfv.");
    }

    /// <summary>
    /// The hint under the primary action: the next thing the user must supply, or empty when a run
    /// is in progress or nothing is outstanding. Returns a plain string, not a
    /// <see cref="FieldStatus"/> — this is prose under a button, not a field's validity.
    /// </summary>
    public static string BuildActionHint(bool isCreating, string inputPath, string outputPath)
    {
        if (isCreating)
        {
            return string.Empty;
        }

        if (string.IsNullOrWhiteSpace(inputPath))
        {
            return "Select an input file to continue.";
        }

        if (string.IsNullOrWhiteSpace(outputPath))
        {
            return "Choose where to save the SRR to continue.";
        }

        return string.Empty;
    }
}
