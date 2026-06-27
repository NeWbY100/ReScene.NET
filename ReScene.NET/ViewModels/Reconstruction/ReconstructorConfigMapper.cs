using ReScene.NET.Models;

namespace ReScene.NET.ViewModels.Reconstruction;

/// <summary>
/// Copies the Reconstructor view-model's scalar bound fields (paths and every RAR option toggle)
/// to and from the serializable <see cref="ReconstructorConfig"/>. The imported-SRR snapshot is
/// handled separately by the view-model so this mapper stays free of the private import state.
/// All bound properties remain on the view-model; this only relocates the 1:1 copy boilerplate.
/// </summary>
internal static class ReconstructorConfigMapper
{
    /// <summary>Copies all scalar option fields from the view-model into a fresh config.</summary>
    public static ReconstructorConfig Capture(ReconstructorViewModel vm) => new()
    {
        WinRarPath = vm.WinRarPath,
        ReleasePath = vm.ReleasePath,
        VerificationPath = vm.VerificationPath,
        OutputPath = vm.OutputPath,

        Version2 = vm.Version2,
        Version3 = vm.Version3,
        Version4 = vm.Version4,
        Version5 = vm.Version5,
        Version6 = vm.Version6,
        Version7 = vm.Version7,

        SwitchM0 = vm.SwitchM0,
        SwitchM1 = vm.SwitchM1,
        SwitchM2 = vm.SwitchM2,
        SwitchM3 = vm.SwitchM3,
        SwitchM4 = vm.SwitchM4,
        SwitchM5 = vm.SwitchM5,

        SwitchMA4 = vm.SwitchMA4,
        SwitchMA5 = vm.SwitchMA5,

        SwitchMD64K = vm.SwitchMD64K,
        SwitchMD128K = vm.SwitchMD128K,
        SwitchMD256K = vm.SwitchMD256K,
        SwitchMD512K = vm.SwitchMD512K,
        SwitchMD1024K = vm.SwitchMD1024K,
        SwitchMD2048K = vm.SwitchMD2048K,
        SwitchMD4096K = vm.SwitchMD4096K,
        SwitchMD8M = vm.SwitchMD8M,
        SwitchMD16M = vm.SwitchMD16M,
        SwitchMD32M = vm.SwitchMD32M,
        SwitchMD64M = vm.SwitchMD64M,
        SwitchMD128M = vm.SwitchMD128M,
        SwitchMD256M = vm.SwitchMD256M,
        SwitchMD512M = vm.SwitchMD512M,
        SwitchMD1G = vm.SwitchMD1G,

        SwitchTSM0 = vm.SwitchTSM0,
        SwitchTSM1 = vm.SwitchTSM1,
        SwitchTSM2 = vm.SwitchTSM2,
        SwitchTSM3 = vm.SwitchTSM3,
        SwitchTSM4 = vm.SwitchTSM4,
        SwitchTSC0 = vm.SwitchTSC0,
        SwitchTSC1 = vm.SwitchTSC1,
        SwitchTSC2 = vm.SwitchTSC2,
        SwitchTSC3 = vm.SwitchTSC3,
        SwitchTSC4 = vm.SwitchTSC4,
        SwitchTSA0 = vm.SwitchTSA0,
        SwitchTSA1 = vm.SwitchTSA1,
        SwitchTSA2 = vm.SwitchTSA2,
        SwitchTSA3 = vm.SwitchTSA3,
        SwitchTSA4 = vm.SwitchTSA4,

        SwitchAI = vm.SwitchAI,
        SwitchR = vm.SwitchR,
        SwitchDS = vm.SwitchDS,
        SwitchSDash = vm.SwitchSDash,
        SwitchMT = vm.SwitchMT,
        SwitchMTStart = vm.SwitchMTStart,
        SwitchMTEnd = vm.SwitchMTEnd,

        SwitchV = vm.SwitchV,
        VolumeSize = vm.VolumeSize,
        VolumeSizeUnitIndex = vm.VolumeSizeUnitIndex,
        UseOldVolumeNaming = vm.UseOldVolumeNaming,

        FileA = vm.FileA,
        FileI = vm.FileI,

        DeleteRARFiles = vm.DeleteRARFiles,
        DeleteDuplicateCRCFiles = vm.DeleteDuplicateCRCFiles,
        StopOnFirstMatch = vm.StopOnFirstMatch,
        CompleteAllVolumes = vm.CompleteAllVolumes,
        RenameToReleaseNames = vm.RenameToReleaseNames,

        EnableHostOSPatching = vm.EnableHostOSPatching,
    };

    /// <summary>Writes all scalar option fields from a config onto the view-model.</summary>
    public static void Apply(ReconstructorViewModel vm, ReconstructorConfig c)
    {
        vm.WinRarPath = c.WinRarPath;
        vm.ReleasePath = c.ReleasePath;
        vm.VerificationPath = c.VerificationPath;
        vm.OutputPath = c.OutputPath;

        vm.Version2 = c.Version2;
        vm.Version3 = c.Version3;
        vm.Version4 = c.Version4;
        vm.Version5 = c.Version5;
        vm.Version6 = c.Version6;
        vm.Version7 = c.Version7;

        vm.SwitchM0 = c.SwitchM0;
        vm.SwitchM1 = c.SwitchM1;
        vm.SwitchM2 = c.SwitchM2;
        vm.SwitchM3 = c.SwitchM3;
        vm.SwitchM4 = c.SwitchM4;
        vm.SwitchM5 = c.SwitchM5;

        vm.SwitchMA4 = c.SwitchMA4;
        vm.SwitchMA5 = c.SwitchMA5;

        vm.SwitchMD64K = c.SwitchMD64K;
        vm.SwitchMD128K = c.SwitchMD128K;
        vm.SwitchMD256K = c.SwitchMD256K;
        vm.SwitchMD512K = c.SwitchMD512K;
        vm.SwitchMD1024K = c.SwitchMD1024K;
        vm.SwitchMD2048K = c.SwitchMD2048K;
        vm.SwitchMD4096K = c.SwitchMD4096K;
        vm.SwitchMD8M = c.SwitchMD8M;
        vm.SwitchMD16M = c.SwitchMD16M;
        vm.SwitchMD32M = c.SwitchMD32M;
        vm.SwitchMD64M = c.SwitchMD64M;
        vm.SwitchMD128M = c.SwitchMD128M;
        vm.SwitchMD256M = c.SwitchMD256M;
        vm.SwitchMD512M = c.SwitchMD512M;
        vm.SwitchMD1G = c.SwitchMD1G;

        vm.SwitchTSM0 = c.SwitchTSM0;
        vm.SwitchTSM1 = c.SwitchTSM1;
        vm.SwitchTSM2 = c.SwitchTSM2;
        vm.SwitchTSM3 = c.SwitchTSM3;
        vm.SwitchTSM4 = c.SwitchTSM4;
        vm.SwitchTSC0 = c.SwitchTSC0;
        vm.SwitchTSC1 = c.SwitchTSC1;
        vm.SwitchTSC2 = c.SwitchTSC2;
        vm.SwitchTSC3 = c.SwitchTSC3;
        vm.SwitchTSC4 = c.SwitchTSC4;
        vm.SwitchTSA0 = c.SwitchTSA0;
        vm.SwitchTSA1 = c.SwitchTSA1;
        vm.SwitchTSA2 = c.SwitchTSA2;
        vm.SwitchTSA3 = c.SwitchTSA3;
        vm.SwitchTSA4 = c.SwitchTSA4;

        vm.SwitchAI = c.SwitchAI;
        vm.SwitchR = c.SwitchR;
        vm.SwitchDS = c.SwitchDS;
        vm.SwitchSDash = c.SwitchSDash;
        vm.SwitchMT = c.SwitchMT;
        vm.SwitchMTStart = c.SwitchMTStart;
        vm.SwitchMTEnd = c.SwitchMTEnd;

        vm.SwitchV = c.SwitchV;
        vm.VolumeSize = c.VolumeSize;
        vm.VolumeSizeUnitIndex = c.VolumeSizeUnitIndex;
        vm.UseOldVolumeNaming = c.UseOldVolumeNaming;

        vm.FileA = c.FileA;
        vm.FileI = c.FileI;

        vm.DeleteRARFiles = c.DeleteRARFiles;
        vm.DeleteDuplicateCRCFiles = c.DeleteDuplicateCRCFiles;
        vm.CompleteAllVolumes = c.CompleteAllVolumes;
        vm.RenameToReleaseNames = c.RenameToReleaseNames;
        // Apply StopOnFirstMatch last: its OnStopOnFirstMatchChanged hook clears the rename flag
        // when false, so an inconsistent (rename-on while stop-off) config normalises on import.
        vm.StopOnFirstMatch = c.StopOnFirstMatch;

        vm.EnableHostOSPatching = c.EnableHostOSPatching;
    }
}
