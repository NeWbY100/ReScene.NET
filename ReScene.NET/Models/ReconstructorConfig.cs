namespace ReScene.NET.Models;

/// <summary>
/// Serializable snapshot of all user-editable fields and options on the RAR Reconstructor tab.
/// </summary>
public sealed class ReconstructorConfig
{
    public int Version { get; set; } = 1;

    // Paths
    public string WinRarPath { get; set; } = string.Empty;
    public string ReleasePath { get; set; } = string.Empty;
    public string VerificationPath { get; set; } = string.Empty;
    public string OutputPath { get; set; } = string.Empty;

    // RAR Versions
    public bool Version2 { get; set; }
    public bool Version3 { get; set; } = true;
    public bool Version4 { get; set; } = true;
    public bool Version5 { get; set; } = true;
    public bool Version6 { get; set; } = true;
    public bool Version7 { get; set; }

    // Compression Method
    public bool SwitchM0 { get; set; }
    public bool SwitchM1 { get; set; }
    public bool SwitchM2 { get; set; }
    public bool SwitchM3 { get; set; } = true;
    public bool SwitchM4 { get; set; }
    public bool SwitchM5 { get; set; }

    // Archive Format
    public bool SwitchMA4 { get; set; }
    public bool SwitchMA5 { get; set; }

    // Dictionary Size
    public bool SwitchMD64K { get; set; }
    public bool SwitchMD128K { get; set; }
    public bool SwitchMD256K { get; set; }
    public bool SwitchMD512K { get; set; }
    public bool SwitchMD1024K { get; set; }
    public bool SwitchMD2048K { get; set; }
    public bool SwitchMD4096K { get; set; } = true;
    public bool SwitchMD8M { get; set; }
    public bool SwitchMD16M { get; set; }
    public bool SwitchMD32M { get; set; }
    public bool SwitchMD64M { get; set; }
    public bool SwitchMD128M { get; set; }
    public bool SwitchMD256M { get; set; }
    public bool SwitchMD512M { get; set; }
    public bool SwitchMD1G { get; set; }

    // Timestamps — Modification
    public bool SwitchTSM0 { get; set; }
    public bool SwitchTSM1 { get; set; }
    public bool SwitchTSM2 { get; set; }
    public bool SwitchTSM3 { get; set; }
    public bool SwitchTSM4 { get; set; }

    // Timestamps — Creation
    public bool SwitchTSC0 { get; set; }
    public bool SwitchTSC1 { get; set; }
    public bool SwitchTSC2 { get; set; }
    public bool SwitchTSC3 { get; set; }
    public bool SwitchTSC4 { get; set; }

    // Timestamps — Access
    public bool SwitchTSA0 { get; set; }
    public bool SwitchTSA1 { get; set; }
    public bool SwitchTSA2 { get; set; }
    public bool SwitchTSA3 { get; set; }
    public bool SwitchTSA4 { get; set; }

    // Other Options
    public bool SwitchAI { get; set; }
    public bool SwitchR { get; set; } = true;
    public bool SwitchDS { get; set; }
    public bool SwitchSDash { get; set; }
    public bool SwitchMT { get; set; }
    public int SwitchMTStart { get; set; } = 1;
    public int SwitchMTEnd { get; set; } = Environment.ProcessorCount;

    // Volume
    public bool SwitchV { get; set; }
    public string VolumeSize { get; set; } = "15000";
    public int VolumeSizeUnitIndex { get; set; } = 1;
    public bool UseOldVolumeNaming { get; set; }

    // File attributes
    public bool? FileA { get; set; } = false;
    public bool? FileI { get; set; } = false;

    // Output options
    public bool DeleteRARFiles { get; set; }
    public bool DeleteDuplicateCRCFiles { get; set; } = true;
    public bool StopOnFirstMatch { get; set; } = true;
    public bool CompleteAllVolumes { get; set; }
    public bool RenameToReleaseNames { get; set; } = true;

    // Header patching
    public bool EnableHostOSPatching { get; set; } = true;

    /// <summary>
    /// Snapshot of the SRR-imported state (timestamps, CRCs, comment, detected flags, …).
    /// Null when no SRR has been imported yet.
    /// </summary>
    public ImportedSrrState? ImportedSrr { get; set; }
}

/// <summary>
/// Persisted snapshot of state captured by Import-from-SRR.
/// </summary>
public sealed class ImportedSrrState
{
    public string? SRRFilePath { get; set; }

    public List<string> ArchiveFiles { get; set; } = [];
    public List<string> ArchiveDirectories { get; set; } = [];

    public Dictionary<string, DateTime> DirTimestamps { get; set; } = [];
    public Dictionary<string, DateTime> DirCreationTimes { get; set; } = [];
    public Dictionary<string, DateTime> DirAccessTimes { get; set; } = [];
    public Dictionary<string, DateTime> FileTimestamps { get; set; } = [];
    public Dictionary<string, DateTime> FileCreationTimes { get; set; } = [];
    public Dictionary<string, DateTime> FileAccessTimes { get; set; } = [];

    public Dictionary<string, string> ArchiveFileCrcs { get; set; } = [];

    public List<string> OriginalRarFileNames { get; set; } = [];

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

    public string CustomPackerType { get; set; } = "None";
    public string? CustomPackerWarning { get; set; }
}
