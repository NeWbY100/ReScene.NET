using ReScene.App.Core.ViewModels.Reconstruction;
using ReScene.Core;
using ReScene.Core.Cryptography;
using ReScene.Core.Diagnostics;
using ReScene.SRR;

namespace ReScene.App.Core.Tests;

/// <summary>Small fixtures for the pure planner tests.</summary>
internal static class ArchiveSetPlannerTestData
{
    public static SharedReconstructionSettings SharedSettings() => new()
    {
        WinRARPath = "C:\\winrar",
        ReleasePath = "C:\\release",
        OutputPath = "C:\\out",
        RARVersions = [new VersionRange(300, 400)],
        CommandLineArguments = [[new RARCommandLineArgument("a", 200)]],
        HashType = HashType.CRC32,
        Verification = VerificationSnapshot.Empty,
        SetFileArchiveAttribute = TriState.Unchecked,
        SetFileNotContentIndexedAttribute = TriState.Unchecked,
        DeleteRARFiles = false,
        DeleteDuplicateCRCFiles = true,
        StopOnFirstMatch = true,
        CompleteAllVolumes = false,
        RenameToReleaseNames = true,
        EnableHostOSPatching = true,
        UseOldVolumeNaming = false,
    };

    public static BruteForceOptions SampleOptions()
    {
        SharedReconstructionSettings shared = SharedSettings() with
        {
            RARVersions = [new VersionRange(300, 400), new VersionRange(400, 500)],
            CommandLineArguments =
            [
                [new RARCommandLineArgument("a", 200), new RARCommandLineArgument("-m0", 300)],
                [new RARCommandLineArgument("a", 200), new RARCommandLineArgument("-m3", 300)],
            ],
        };

        var set = new SRRArchiveSet { Key = "DVD1/x", Directory = "DVD1" };
        set.VolumeNames.Add("DVD1\\x.rar");
        set.ArchivedFiles.Add("x.iso");
        set.ArchivedFileCrcs["x.iso"] = "00000000";

        return ArchiveSetPlanner.BuildOptionsForSet(set, shared,
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));
    }
}
