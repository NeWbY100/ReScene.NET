using ReScene.NET.Models;
using ReScene.SRR;

namespace ReScene.NET.ViewModels.Reconstruction;

/// <summary>
/// Maps the in-memory <see cref="ReconstructionImportState"/> to and from the serializable
/// <see cref="ImportedSrrState"/> DTO used by import/export configuration. Pure: it copies data
/// only and never touches WPF binding (the bound <c>CustomPackerWarning</c> is handled by the
/// view-model).
/// </summary>
internal static class ImportedSrrStateMapper
{
    /// <summary>
    /// Captures the state as a DTO, or returns null when no meaningful SRR state has been imported.
    /// The bound custom-packer warning is supplied separately by the caller.
    /// </summary>
    public static ImportedSrrState? Capture(ReconstructionImportState state, string? customPackerWarning)
    {
        bool hasState = state.ArchiveFiles.Count > 0
            || state.ArchiveDirectories.Count > 0
            || state.FileTimestamps.Count > 0
            || state.ArchiveFileCrcs.Count > 0
            || state.SRRFilePath is not null
            || state.CmtCompressedData is { Length: > 0 };

        if (!hasState)
        {
            return null;
        }

        return new ImportedSrrState
        {
            SRRFilePath = state.SRRFilePath,
            ArchiveFiles = [.. state.ArchiveFiles],
            ArchiveDirectories = [.. state.ArchiveDirectories],
            DirTimestamps = new Dictionary<string, DateTime>(state.DirTimestamps),
            DirCreationTimes = new Dictionary<string, DateTime>(state.DirCreationTimes),
            DirAccessTimes = new Dictionary<string, DateTime>(state.DirAccessTimes),
            FileTimestamps = new Dictionary<string, DateTime>(state.FileTimestamps),
            FileCreationTimes = new Dictionary<string, DateTime>(state.FileCreationTimes),
            FileAccessTimes = new Dictionary<string, DateTime>(state.FileAccessTimes),
            ArchiveFileCrcs = new Dictionary<string, string>(state.ArchiveFileCrcs),
            OriginalRarFileNames = [.. state.OriginalRarFileNames],
            ArchiveComment = state.ArchiveComment,
            ArchiveCommentBytes = state.ArchiveCommentBytes,
            CmtCompressedData = state.CmtCompressedData,
            CmtCompressionMethod = state.CmtCompressionMethod,
            DetectedFileHostOS = state.DetectedFileHostOS,
            DetectedFileAttributes = state.DetectedFileAttributes,
            DetectedCmtHostOS = state.DetectedCmtHostOS,
            DetectedCmtFileTime = state.DetectedCmtFileTime,
            DetectedCmtFileAttributes = state.DetectedCmtFileAttributes,
            DetectedLargeFlag = state.DetectedLargeFlag,
            DetectedHighPackSize = state.DetectedHighPackSize,
            DetectedHighUnpSize = state.DetectedHighUnpSize,
            CustomPackerType = state.CustomPackerType.ToString(),
            CustomPackerWarning = customPackerWarning
        };
    }

    /// <summary>
    /// Builds an import state from a DTO (or a fully-empty state when the DTO is null, meaning
    /// "no SRR imported"). The caller applies the bound custom-packer warning and any logging.
    /// </summary>
    public static ReconstructionImportState Apply(ImportedSrrState? s)
    {
        if (s is null)
        {
            return new ReconstructionImportState();
        }

        return new ReconstructionImportState
        {
            SRRFilePath = s.SRRFilePath,
            ArchiveFiles = new HashSet<string>(s.ArchiveFiles, StringComparer.OrdinalIgnoreCase),
            ArchiveDirectories = new HashSet<string>(s.ArchiveDirectories, StringComparer.OrdinalIgnoreCase),
            DirTimestamps = ToCi(s.DirTimestamps),
            DirCreationTimes = ToCi(s.DirCreationTimes),
            DirAccessTimes = ToCi(s.DirAccessTimes),
            FileTimestamps = ToCi(s.FileTimestamps),
            FileCreationTimes = ToCi(s.FileCreationTimes),
            FileAccessTimes = ToCi(s.FileAccessTimes),
            ArchiveFileCrcs = new Dictionary<string, string>(s.ArchiveFileCrcs, StringComparer.OrdinalIgnoreCase),
            OriginalRarFileNames = s.OriginalRarFileNames is { } names ? [.. names] : [],
            ArchiveComment = s.ArchiveComment,
            ArchiveCommentBytes = s.ArchiveCommentBytes,
            CmtCompressedData = s.CmtCompressedData,
            CmtCompressionMethod = s.CmtCompressionMethod,
            DetectedFileHostOS = s.DetectedFileHostOS,
            DetectedFileAttributes = s.DetectedFileAttributes,
            DetectedCmtHostOS = s.DetectedCmtHostOS,
            DetectedCmtFileTime = s.DetectedCmtFileTime,
            DetectedCmtFileAttributes = s.DetectedCmtFileAttributes,
            DetectedLargeFlag = s.DetectedLargeFlag,
            DetectedHighPackSize = s.DetectedHighPackSize,
            DetectedHighUnpSize = s.DetectedHighUnpSize,
            CustomPackerType = Enum.TryParse(s.CustomPackerType, out CustomPackerType packer) ? packer : CustomPackerType.None,
        };

        static Dictionary<string, DateTime> ToCi(Dictionary<string, DateTime>? src) =>
            src is null
                ? new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, DateTime>(src, StringComparer.OrdinalIgnoreCase);
    }
}
