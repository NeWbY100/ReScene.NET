using ReScene.SRR;

namespace ReScene.NET.ViewModels.Reconstruction;

/// <summary>
/// Mutable holder for the reconstruction state captured from an imported SRR: archived entries,
/// timestamps, CRCs, comment/CMT data, and detected header fields. The view-model owns one of
/// these and feeds it to the options builder and config capture/restore. It carries no WPF binding
/// concerns (the bound display strings and option toggles live on the view-model).
/// </summary>
internal sealed class ReconstructionImportState
{
    public HashSet<string> ArchiveFiles { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<string> ArchiveDirectories { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, DateTime> DirTimestamps { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, DateTime> DirCreationTimes { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, DateTime> DirAccessTimes { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, DateTime> FileTimestamps { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, DateTime> FileCreationTimes { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, DateTime> FileAccessTimes { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, string> ArchiveFileCrcs { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public string? ArchiveComment { get; set; }
    public byte[]? ArchiveCommentBytes { get; set; }
    public byte[]? CmtCompressedData { get; set; }
    public byte? CmtCompressionMethod { get; set; }
    public byte? DetectedFileHostOS { get; set; }
    public uint? DetectedFileAttributes { get; set; }
    public byte? DetectedCmtHostOS { get; set; }
    public uint? DetectedCmtFileTime { get; set; }
    public uint? DetectedCmtFileAttributes { get; set; }
    public bool? DetectedLargeFlag { get; set; }
    public uint? DetectedHighPackSize { get; set; }
    public uint? DetectedHighUnpSize { get; set; }
    public List<string> OriginalRarFileNames { get; set; } = [];
    public CustomPackerType CustomPackerType { get; set; } = CustomPackerType.None;
    public string? SRRFilePath { get; set; }

    /// <summary>Resets every field back to the freshly-imported-nothing defaults.</summary>
    public void Clear()
    {
        ArchiveFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        ArchiveDirectories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        DirTimestamps = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
        DirCreationTimes = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
        DirAccessTimes = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
        FileTimestamps = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
        FileCreationTimes = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
        FileAccessTimes = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
        ArchiveFileCrcs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        ArchiveComment = null;
        ArchiveCommentBytes = null;
        CmtCompressedData = null;
        CmtCompressionMethod = null;
        DetectedFileHostOS = null;
        DetectedFileAttributes = null;
        DetectedCmtHostOS = null;
        DetectedCmtFileTime = null;
        DetectedCmtFileAttributes = null;
        DetectedLargeFlag = null;
        DetectedHighPackSize = null;
        DetectedHighUnpSize = null;
        OriginalRarFileNames = [];
        CustomPackerType = CustomPackerType.None;
        SRRFilePath = null;
    }
}
