using ReScene.App.Core.ViewModels.Creation;
using ReScene.SRR;

namespace ReScene.App.Core.Tests;

/// <summary>
/// Direct tests for the naming, classification and splice-position helpers extracted from
/// CreatorViewModel. They were previously private and reachable only end-to-end through folder-mode
/// staging, which meant a change to the string arithmetic could only be caught by building a whole
/// release tree. Paths are composed with <see cref="Path.Combine(string, string)"/> throughout so
/// the expectations hold on every host the suite runs on.
/// </summary>
public class CreatorArtifactNamingTests
{
    private static string Root => Path.Combine(Path.GetTempPath(), "creator-naming-root");

    private static StoredFileEntry Entry(string storedName) => new(storedName, $"/abs/{storedName}");

    // ── Root-relative naming ─────────────────────────────────

    [Fact]
    public void RootRelativeName_NestedSource_IsRootRelativeWithForwardSlashes()
    {
        string full = Path.Combine(Root, "Sample", "clip.mkv");
        Assert.Equal("Sample/clip.mkv", CreatorArtifactNaming.RootRelativeName(Root, full));
    }

    [Fact]
    public void RootRelativeName_RootLevelSource_IsJustTheName()
        => Assert.Equal("release.nfo", CreatorArtifactNaming.RootRelativeName(Root, Path.Combine(Root, "release.nfo")));

    [Fact]
    public void FolderRelativeName_SourceUnderRoot_KeepsItsRelativePath()
    {
        string inside = Path.Combine(Root, "Subs", "eng.sfv");
        Assert.Equal("Subs/eng.sfv", CreatorArtifactNaming.FolderRelativeName(Root, inside, "Subs"));
    }

    [Fact]
    public void FolderRelativeName_SourceOutsideRoot_FallsBackToConventionalDir()
    {
        // ExtraSampleFiles is shared with the Advanced tab's "Add Sample" command, so a folder-mode
        // run can legitimately see a source outside the release root. The raw relative path would
        // keep a "../" the writer's name contract rejects.
        string outside = Path.Combine(Path.GetTempPath(), "creator-naming-elsewhere", "clip.mkv");
        Assert.Equal("Sample/clip.mkv", CreatorArtifactNaming.FolderRelativeName(Root, outside, "Sample"));
    }

    [Fact]
    public void FolderRelativeStem_StripsOnlyTheExtension()
    {
        string inside = Path.Combine(Root, "Sample", "clip.part1.mkv");
        Assert.Equal("Sample/clip.part1", CreatorArtifactStemFor(inside));

        static string CreatorArtifactStemFor(string p) =>
            CreatorArtifactNaming.FolderRelativeStem(Root, p, "Sample");
    }

    [Fact]
    public void GeneratedStoredName_SwapsTheExtension_AndFallsBackOutsideTheRoot()
    {
        string inside = Path.Combine(Root, "Sample", "clip.mkv");
        Assert.Equal("Sample/clip.srs", CreatorArtifactNaming.GeneratedStoredName(Root, inside, ".srs", "Sample"));

        string outside = Path.Combine(Path.GetTempPath(), "creator-naming-elsewhere", "clip.mkv");
        Assert.Equal("Sample/clip.srs", CreatorArtifactNaming.GeneratedStoredName(Root, outside, ".srs", "Sample"));
    }

    // ── Proof-directory classification ───────────────────────

    [Theory]
    [InlineData("Proof/x.rar", true)]
    [InlineData("proof/x.rar", true)]
    [InlineData("Proofs/x.rar", true)]
    [InlineData("rls/Proof/x.rar", true)]
    [InlineData("Proof/sub/x.rar", false)]   // IMMEDIATE parent only, matching the scanner's rule
    [InlineData("Proofread/x.rar", false)]   // a longer name, not the proof directory
    [InlineData("x-proof.rar", false)]       // no parent directory at all
    public void IsUnderProofDirectory_MatchesTheImmediateParentOnly(string storedName, bool expected)
        => Assert.Equal(expected, CreatorArtifactNaming.IsUnderProofDirectory(storedName));

    [Fact]
    public void HasMatchingSfv_TrueOnlyWhenAnotherEntrySharesTheSameStem()
    {
        List<StoredFileEntry> entries = [Entry("Proof/rls.rar"), Entry("Proof/rls.sfv"), Entry("other.rar")];

        Assert.True(CreatorArtifactNaming.HasMatchingSfv("Proof/rls.rar", entries));
        Assert.False(CreatorArtifactNaming.HasMatchingSfv("other.rar", entries));
    }

    // ── Splice positions ─────────────────────────────────────

    [Fact]
    public void FindSampleArtifactSpliceIndex_StopsAtTheFirstSrsSfvOrNonProofRar()
    {
        List<StoredFileEntry> entries = [Entry("release.nfo"), Entry("rls.sfv"), Entry("rls.rar")];
        Assert.Equal(1, CreatorArtifactNaming.FindSampleArtifactSpliceIndex(entries));
    }

    [Fact]
    public void FindSampleArtifactSpliceIndex_SkipsAProofRarWithNoMatchingSfv()
    {
        // An independently-discovered proof RAR has nothing to relocate against, so it stays in its
        // own early position and must NOT anchor the sample splice.
        List<StoredFileEntry> entries = [Entry("Proof/p.rar"), Entry("rls.sfv")];
        Assert.Equal(1, CreatorArtifactNaming.FindSampleArtifactSpliceIndex(entries));
    }

    [Fact]
    public void FindSampleArtifactSpliceIndex_AnchorsOnAProofRarThatHasItsSfv()
    {
        // With a matching sfv it is logically part of the final-SFV tail, so it DOES anchor.
        List<StoredFileEntry> entries = [Entry("Proof/p.rar"), Entry("Proof/p.sfv")];
        Assert.Equal(0, CreatorArtifactNaming.FindSampleArtifactSpliceIndex(entries));
    }

    [Fact]
    public void FindSampleArtifactSpliceIndex_NoAnchor_ReturnsTheEnd()
    {
        List<StoredFileEntry> entries = [Entry("release.nfo"), Entry("info.txt")];
        Assert.Equal(2, CreatorArtifactNaming.FindSampleArtifactSpliceIndex(entries));
    }

    [Fact]
    public void FindSubtitleArtifactSpliceIndex_StopsAtTheFirstSfv_SkippingAFixRar()
    {
        List<StoredFileEntry> entries = [Entry("release.nfo"), Entry("fix.rar"), Entry("rls.sfv")];
        Assert.Equal(2, CreatorArtifactNaming.FindSubtitleArtifactSpliceIndex(entries));
    }

    // ── Filesystem-root detection ────────────────────────────

    [Fact]
    public void IsFilesystemRoot_TrueForARoot_FalseForARelease()
    {
        string root = Path.GetPathRoot(Path.GetTempPath())!;
        Assert.True(CreatorArtifactNaming.IsFilesystemRoot(root));
        Assert.False(CreatorArtifactNaming.IsFilesystemRoot(Root));
    }
}
