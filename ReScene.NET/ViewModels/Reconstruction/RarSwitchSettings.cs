namespace ReScene.NET.ViewModels.Reconstruction;

/// <summary>
/// Immutable snapshot of the user-selected RAR switch toggles on the Reconstructor tab. The
/// view-model populates this from its bound properties and hands it to
/// <see cref="RarCommandLineBuilder"/>; keeping the values here (rather than reading the
/// view-model directly) keeps the builder free of any WPF binding concerns.
/// </summary>
internal sealed record RarSwitchSettings
{
    // RAR versions
    public bool Version2 { get; init; }
    public bool Version3 { get; init; }
    public bool Version4 { get; init; }
    public bool Version5 { get; init; }
    public bool Version6 { get; init; }
    public bool Version7 { get; init; }

    // Compression method
    public bool SwitchM0 { get; init; }
    public bool SwitchM1 { get; init; }
    public bool SwitchM2 { get; init; }
    public bool SwitchM3 { get; init; }
    public bool SwitchM4 { get; init; }
    public bool SwitchM5 { get; init; }

    // Archive format
    public bool SwitchMA4 { get; init; }
    public bool SwitchMA5 { get; init; }

    // Dictionary size
    public bool SwitchMD64K { get; init; }
    public bool SwitchMD128K { get; init; }
    public bool SwitchMD256K { get; init; }
    public bool SwitchMD512K { get; init; }
    public bool SwitchMD1024K { get; init; }
    public bool SwitchMD2048K { get; init; }
    public bool SwitchMD4096K { get; init; }
    public bool SwitchMD8M { get; init; }
    public bool SwitchMD16M { get; init; }
    public bool SwitchMD32M { get; init; }
    public bool SwitchMD64M { get; init; }
    public bool SwitchMD128M { get; init; }
    public bool SwitchMD256M { get; init; }
    public bool SwitchMD512M { get; init; }
    public bool SwitchMD1G { get; init; }

    // Timestamps — modification
    public bool SwitchTSM0 { get; init; }
    public bool SwitchTSM1 { get; init; }
    public bool SwitchTSM2 { get; init; }
    public bool SwitchTSM3 { get; init; }
    public bool SwitchTSM4 { get; init; }

    // Timestamps — creation
    public bool SwitchTSC0 { get; init; }
    public bool SwitchTSC1 { get; init; }
    public bool SwitchTSC2 { get; init; }
    public bool SwitchTSC3 { get; init; }
    public bool SwitchTSC4 { get; init; }

    // Timestamps — access
    public bool SwitchTSA0 { get; init; }
    public bool SwitchTSA1 { get; init; }
    public bool SwitchTSA2 { get; init; }
    public bool SwitchTSA3 { get; init; }
    public bool SwitchTSA4 { get; init; }

    // Other options
    public bool SwitchAI { get; init; }
    public bool SwitchR { get; init; }
    public bool SwitchDS { get; init; }
    public bool SwitchSDash { get; init; }
    public bool SwitchMT { get; init; }
    public int SwitchMTStart { get; init; }
    public int SwitchMTEnd { get; init; }

    // Volume
    public bool SwitchV { get; init; }
    public string VolumeSize { get; init; } = string.Empty;
    public int VolumeSizeUnitIndex { get; init; }
    public bool UseOldVolumeNaming { get; init; }
}
