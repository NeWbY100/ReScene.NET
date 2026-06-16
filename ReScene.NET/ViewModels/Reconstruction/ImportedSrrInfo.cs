using ReScene.SRR;

namespace ReScene.NET.ViewModels.Reconstruction;

/// <summary>
/// Result of parsing an imported SRR file: the raw imported/detected state plus the derived RAR
/// option values and human-readable display strings. The view-model maps this onto its private
/// fields, bound option properties, and bound display properties — this type carries no WPF
/// binding concerns of its own.
/// </summary>
internal sealed class ImportedSrrInfo
{
    public required SRRFile Srr { get; init; }
    public required string SrrFilePath { get; init; }

    /// <summary>
    /// True when the SRR carries no RAR reconstruction information (no volume entries, archived
    /// files, or compression metadata) — the user must configure options manually.
    /// </summary>
    public bool HasRarReconstructionInfo { get; init; }

    // ── Custom packer ──
    public CustomPackerType CustomPackerType { get; init; } = CustomPackerType.None;

    /// <summary>The custom-packer warning text (also shown in a dialog), or null when none.</summary>
    public string? CustomPackerWarning { get; init; }

    // ── Imported archive state ──
    public HashSet<string> ArchiveFiles { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<string> ArchiveDirectories { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, DateTime> DirTimestamps { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, DateTime> DirCreationTimes { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, DateTime> DirAccessTimes { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, DateTime> FileTimestamps { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, DateTime> FileCreationTimes { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, DateTime> FileAccessTimes { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, string> ArchiveFileCrcs { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    public List<string> OriginalRarFileNames { get; init; } = [];
    public string? ArchiveComment { get; init; }
    public byte[]? ArchiveCommentBytes { get; init; }
    public byte[]? CmtCompressedData { get; init; }
    public byte? CmtCompressionMethod { get; init; }

    // ── Detected header fields ──
    public byte? DetectedFileHostOS { get; init; }
    public uint? DetectedFileAttributes { get; init; }
    public byte? DetectedCmtHostOS { get; init; }
    public uint? DetectedCmtFileTime { get; init; }
    public uint? DetectedCmtFileAttributes { get; init; }
    public bool? DetectedLargeFlag { get; init; }
    public uint? DetectedHighPackSize { get; init; }
    public uint? DetectedHighUnpSize { get; init; }

    // ── Display strings (wizard import step) ──
    public string DisplayName { get; init; } = string.Empty;
    public string DisplayAppName { get; init; } = string.Empty;
    public string DisplayRarVolumeText { get; init; } = string.Empty;
    public string DisplayArchivedFilesText { get; init; } = string.Empty;
    public string DisplayCompressionText { get; init; } = string.Empty;
    public string DisplayStoredFilesText { get; init; } = string.Empty;
}
