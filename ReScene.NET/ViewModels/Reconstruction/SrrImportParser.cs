using ReScene.NET.Helpers;
using ReScene.SRR;

namespace ReScene.NET.ViewModels.Reconstruction;

/// <summary>
/// Reads an SRR file and derives the imported/detected reconstruction state plus the display
/// strings shown on the wizard's import step. Pure: it neither logs, shows dialogs, nor mutates
/// any bound option — the view-model performs those steps when it applies the result, preserving
/// the exact log/dialog ordering.
/// </summary>
internal static class SrrImportParser
{
    public static ImportedSrrInfo Parse(SRRFile srr, string path)
    {
        bool hasRarReconstructionInfo = srr.RARFiles.Count > 0
            || srr.ArchivedFiles.Count > 0
            || srr.CompressionMethod.HasValue;

        (CustomPackerType packerType, string? packerWarning) = DescribeCustomPacker(srr);

        return new ImportedSrrInfo
        {
            Srr = srr,
            SrrFilePath = path,
            HasRarReconstructionInfo = hasRarReconstructionInfo,

            CustomPackerType = packerType,
            CustomPackerWarning = packerWarning,

            ArchiveFiles = new HashSet<string>(srr.ArchivedFiles, StringComparer.OrdinalIgnoreCase),
            ArchiveDirectories = new HashSet<string>(srr.ArchivedDirectories, StringComparer.OrdinalIgnoreCase),
            DirTimestamps = new Dictionary<string, DateTime>(srr.ArchivedDirectoryTimestamps, StringComparer.OrdinalIgnoreCase),
            DirCreationTimes = new Dictionary<string, DateTime>(srr.ArchivedDirectoryCreationTimes, StringComparer.OrdinalIgnoreCase),
            DirAccessTimes = new Dictionary<string, DateTime>(srr.ArchivedDirectoryAccessTimes, StringComparer.OrdinalIgnoreCase),
            FileTimestamps = new Dictionary<string, DateTime>(srr.ArchivedFileTimestamps, StringComparer.OrdinalIgnoreCase),
            FileCreationTimes = new Dictionary<string, DateTime>(srr.ArchivedFileCreationTimes, StringComparer.OrdinalIgnoreCase),
            FileAccessTimes = new Dictionary<string, DateTime>(srr.ArchivedFileAccessTimes, StringComparer.OrdinalIgnoreCase),
            ArchiveFileCrcs = new Dictionary<string, string>(srr.ArchivedFileCrcs, StringComparer.OrdinalIgnoreCase),
            OriginalRarFileNames = srr.RARFiles.Select(r => r.FileName).ToList(),
            ArchiveComment = srr.ArchiveComment,
            ArchiveCommentBytes = srr.ArchiveCommentBytes?.ToArray(),
            CmtCompressedData = srr.CmtCompressedData?.ToArray(),
            CmtCompressionMethod = srr.CmtCompressionMethod,

            DetectedFileHostOS = srr.DetectedHostOS,
            DetectedFileAttributes = srr.DetectedFileAttributes,
            DetectedCmtHostOS = srr.CmtHostOS,
            DetectedCmtFileTime = srr.CmtFileTimeDOS,
            DetectedCmtFileAttributes = srr.CmtFileAttributes,
            DetectedLargeFlag = srr.HasLargeFiles,
            DetectedHighPackSize = srr.DetectedHighPackSize,
            DetectedHighUnpSize = srr.DetectedHighUnpSize,

            DisplayName = Path.GetFileName(path),
            DisplayAppName = DescribeAppName(srr),
            DisplayRarVolumeText = srr.RARFiles.Count == 1 ? "1 volume" : $"{srr.RARFiles.Count} volumes",
            DisplayArchivedFilesText = srr.ArchivedFiles.Count == 1 ? "1 file" : $"{srr.ArchivedFiles.Count} files",
            DisplayCompressionText = DescribeCompression(srr.CompressionMethod),
            DisplayStoredFilesText = DescribeStoredFiles(srr),
        };
    }

    private static (CustomPackerType Type, string? Warning) DescribeCustomPacker(SRRFile srr)
    {
        if (!srr.HasCustomPackerHeaders)
        {
            return (CustomPackerType.None, null);
        }

        string groups = srr.CustomPackerDetected switch
        {
            CustomPackerType.AllOnesWithLargeFlag => "RELOADED, HI2U, 0x0007, 0x0815",
            CustomPackerType.MaxUint32WithoutLargeFlag => "QCF",
            _ => "Unknown"
        };

        string warning = $"Custom RAR packer detected ({srr.CustomPackerDetected}) — brute-forcing is not possible. " +
            $"Direct SRR reconstruction will be used instead. Known groups: {groups}.";

        return (srr.CustomPackerDetected, warning);
    }

    private static string DescribeAppName(SRRFile srr)
    {
        string? app = srr.HeaderBlock?.HasAppName == true ? srr.HeaderBlock?.AppName : null;
        return string.IsNullOrWhiteSpace(app) ? "Unknown" : app;
    }

    private static string DescribeStoredFiles(SRRFile srr) => srr.StoredFiles.Count == 0
        ? "None"
        : string.Join(Environment.NewLine, srr.StoredFiles.Select(
            s => $"{Path.GetFileName(s.FileName)} ({FormatUtilities.FormatSize(s.FileLength)})"));

    /// <summary>Friendly label for a RAR compression method (0–5), mirroring the import log's names.</summary>
    public static string DescribeCompression(int? method) => method switch
    {
        null => "Unknown",
        0 => "Store / no compression (-m0)",
        1 => "Fastest (-m1)",
        2 => "Fast (-m2)",
        3 => "Normal (-m3)",
        4 => "Good (-m4)",
        5 => "Best (-m5)",
        _ => $"Method {method}",
    };
}
