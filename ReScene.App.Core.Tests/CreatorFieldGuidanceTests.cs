using ReScene.App.Core.Models;
using ReScene.App.Core.ViewModels.Creation;

namespace ReScene.App.Core.Tests;

/// <summary>
/// Direct tests for the Creator's per-field guidance. Previously these strings and severities were
/// only reachable by driving a whole view-model; extracted, each branch can be asserted on its own.
/// </summary>
public class CreatorFieldGuidanceTests : TempDirTestBase
{
    // ── Input status ─────────────────────────────────────────

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void BuildInputStatus_Blank_IsNone(string value)
        => Assert.Equal(FieldStatus.None, CreatorFieldGuidance.BuildInputStatus(value));

    [Fact]
    public void BuildInputStatus_NonexistentFile_IsAnError()
    {
        FieldStatus status = CreatorFieldGuidance.BuildInputStatus(Path.Combine(TempDir, "absent.sfv"));

        Assert.Equal(FieldState.Error, status.State);
        Assert.Equal("This file does not exist.", status.Message);
    }

    [Fact]
    public void BuildInputStatus_FileInAFolderWithNoArchives_WarnsAndNamesTheRelease()
    {
        string releaseDir = Path.Combine(TempDir, "Some.Release-GRP");
        Directory.CreateDirectory(releaseDir);
        string sfv = Path.Combine(releaseDir, "rls.sfv");
        File.WriteAllText(sfv, "");

        FieldStatus status = CreatorFieldGuidance.BuildInputStatus(sfv);

        // The COMPLETE message, not a substring: the quoting and the em dash are part of the
        // user-visible text, and a substring assertion would not notice them changing.
        Assert.Equal(
            FieldStatus.Warning("No .rar volumes found in \"Some.Release-GRP\". An SRR is built from the release's .rar files — they need to be in this folder next to the .sfv."),
            status);
    }

    [Fact]
    public void BuildInputStatus_FileBesideArchives_IsOkAndCountsThem()
    {
        string releaseDir = Path.Combine(TempDir, "Other.Release-GRP");
        Directory.CreateDirectory(releaseDir);
        string sfv = Path.Combine(releaseDir, "rls.sfv");
        File.WriteAllText(sfv, "");
        File.WriteAllBytes(Path.Combine(releaseDir, "rls.rar"), [1]);
        File.WriteAllBytes(Path.Combine(releaseDir, "rls.r00"), [1]);

        FieldStatus status = CreatorFieldGuidance.BuildInputStatus(sfv);

        // Both .rar and .r00 are RAR volume extensions, so the count is exactly 2 — asserted, since
        // a substring check would pass whatever number appeared.
        Assert.Equal(
            FieldStatus.Ok("Release \"Other.Release-GRP\" — 2 archive file(s) in this folder."),
            status);
    }

    // ── Action hint ──────────────────────────────────────────

    [Fact]
    public void BuildActionHint_WhileCreating_IsEmpty_EvenWithNothingSelected()
        => Assert.Equal(string.Empty, CreatorFieldGuidance.BuildActionHint(isCreating: true, inputPath: "", outputPath: ""));

    [Fact]
    public void BuildActionHint_NoInput_AsksForTheInputFirst()
        => Assert.Equal("Select an input file to continue.",
            CreatorFieldGuidance.BuildActionHint(isCreating: false, inputPath: "", outputPath: @"out.srr"));

    [Fact]
    public void BuildActionHint_InputButNoOutput_AsksForTheDestination()
        => Assert.Equal("Choose where to save the SRR to continue.",
            CreatorFieldGuidance.BuildActionHint(isCreating: false, inputPath: @"in.sfv", outputPath: ""));

    [Fact]
    public void BuildActionHint_BothSet_IsEmpty()
        => Assert.Equal(string.Empty,
            CreatorFieldGuidance.BuildActionHint(isCreating: false, inputPath: @"in.sfv", outputPath: @"out.srr"));
}
