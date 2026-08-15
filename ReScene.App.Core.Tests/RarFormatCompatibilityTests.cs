using ReScene.App.Core.ViewModels.Reconstruction;
using ReScene.Core;

namespace ReScene.App.Core.Tests;

/// <summary>
/// Pins <see cref="RarFormatCompatibility"/>'s engine-correct policy: which WinRAR executable
/// versions can produce which archive format, and how <c>-ma4</c>/<c>-ma5</c> aggregate over a
/// selection. The version/format boundaries below were verified against
/// <c>RARCommandLineBuilder.cs</c> (the <c>-ma4</c>/<c>-ma5</c> arguments are bounded
/// <c>Min=500,Max=699</c>) and <c>RARVersionSelector</c> (<c>ShouldSkipRAR6TimestampCombination</c>,
/// <c>ParseRARArchiveVersion</c>) — not from general RAR-version knowledge.
/// </summary>
public sealed class RarFormatCompatibilityTests
{
    // ── FormatForUnpackVersion ───────────────────────────────────────────
    // RarFormat is nested in an internal static class, so it can't appear as a [Theory] parameter
    // on these public test methods (CS0051) — split per expected format instead, matching how
    // SRRSwitchMapperTests only ever uses SRRSwitchMapper's nested types inside method bodies.

    [Theory]
    [InlineData(0)]
    [InlineData(29)]
    [InlineData(49)]
    public void FormatForUnpackVersion_Below50_ReturnsRar4(int unpackVersion)
        => Assert.Equal(RarFormatCompatibility.RarFormat.Rar4, RarFormatCompatibility.FormatForUnpackVersion(unpackVersion));

    [Theory]
    [InlineData(50)]
    [InlineData(69)]
    public void FormatForUnpackVersion_50To69_ReturnsRar5(int unpackVersion)
        => Assert.Equal(RarFormatCompatibility.RarFormat.Rar5, RarFormatCompatibility.FormatForUnpackVersion(unpackVersion));

    [Theory]
    [InlineData(70)]
    [InlineData(100)]
    public void FormatForUnpackVersion_70OrAbove_ReturnsRar7(int unpackVersion)
        => Assert.Equal(RarFormatCompatibility.RarFormat.Rar7, RarFormatCompatibility.FormatForUnpackVersion(unpackVersion));

    // ── ExecutableSupports ───────────────────────────────────────────────
    // Same CS0051 constraint as above — one theory per format, RarFormat passed as a fixed literal
    // inside each method body rather than as InlineData.

    [Theory]
    // Native below 500, needs -ma4 in 500..699, cannot be made by 700+.
    [InlineData(390, true, false)]
    [InlineData(499, true, false)]
    [InlineData(500, true, true)]
    [InlineData(699, true, true)]
    [InlineData(700, false, false)]
    public void ExecutableSupports_Rar4_MatchesEnginePolicy(int exeVersion, bool expectedSupported, bool expectedMa4)
    {
        bool supported = RarFormatCompatibility.ExecutableSupports(exeVersion, RarFormatCompatibility.RarFormat.Rar4, out bool needsMa4, out bool needsMa5);

        Assert.Equal(expectedSupported, supported);
        Assert.Equal(expectedMa4, needsMa4);
        Assert.False(needsMa5);
    }

    [Theory]
    // Only 500..699, and only with -ma5 required (unflagged 550-699 is RAR4 — a different concern
    // from ExecutableSupports, which reports capability/policy, not what an unflagged run defaults to).
    [InlineData(499, false, false)]
    [InlineData(500, true, true)]
    [InlineData(699, true, true)]
    [InlineData(700, false, false)]
    public void ExecutableSupports_Rar5_MatchesEnginePolicy(int exeVersion, bool expectedSupported, bool expectedMa5)
    {
        bool supported = RarFormatCompatibility.ExecutableSupports(exeVersion, RarFormatCompatibility.RarFormat.Rar5, out bool needsMa4, out bool needsMa5);

        Assert.Equal(expectedSupported, supported);
        Assert.False(needsMa4);
        Assert.Equal(expectedMa5, needsMa5);
    }

    [Theory]
    // Native at 700+; nothing below can be coerced up to RAR7.
    [InlineData(699, false)]
    [InlineData(700, true)]
    [InlineData(390, false)]
    public void ExecutableSupports_Rar7_MatchesEnginePolicy(int exeVersion, bool expectedSupported)
    {
        bool supported = RarFormatCompatibility.ExecutableSupports(exeVersion, RarFormatCompatibility.RarFormat.Rar7, out bool needsMa4, out bool needsMa5);

        Assert.Equal(expectedSupported, supported);
        Assert.False(needsMa4);
        Assert.False(needsMa5);
    }

    // ── SelectFor ────────────────────────────────────────────────────────

    private static readonly VersionRange[] _broadRange = [new VersionRange(200, 800)];

    [Fact]
    public void SelectFor_Rar4NativeExecutable_NativeNoMa4()
    {
        InstalledRARVersion[] installed = [new(390, "winrar-390", "p390")];

        var selection = RarFormatCompatibility.SelectFor(RarFormatCompatibility.RarFormat.Rar4, _broadRange, [], installed);

        VersionRange range = Assert.Single(selection.Ranges);
        Assert.Equal((390, 391), (range.Start, range.End));
        Assert.Equal(["winrar-390"], selection.Folders);
        Assert.False(selection.NeedsMa4);
        Assert.False(selection.NeedsMa5);
        Assert.False(selection.Empty);
    }

    [Fact]
    public void SelectFor_Rar4In500To699Band_NeedsMa4()
    {
        InstalledRARVersion[] installed = [new(560, "winrar-560", "p560")];

        var selection = RarFormatCompatibility.SelectFor(RarFormatCompatibility.RarFormat.Rar4, _broadRange, [], installed);

        VersionRange range = Assert.Single(selection.Ranges);
        Assert.Equal((560, 561), (range.Start, range.End));
        Assert.True(selection.NeedsMa4);
        Assert.False(selection.NeedsMa5);
        Assert.False(selection.Empty);
    }

    [Fact]
    public void SelectFor_Rar4MixedNativeAnd500Band_BothRangesSurvive_NeedsMa4True()
    {
        // 390 stays native (below 500); 560 needs -ma4 — the version-bounded argument (Min=500,
        // Max=699) is filtered out for 390 downstream, so only 560 actually gets -ma4.
        InstalledRARVersion[] installed = [new(390, "winrar-390", "p390"), new(560, "winrar-560", "p560")];

        var selection = RarFormatCompatibility.SelectFor(RarFormatCompatibility.RarFormat.Rar4, _broadRange, [], installed);

        Assert.Equal(2, selection.Ranges.Count);
        Assert.Equal((390, 391), (selection.Ranges[0].Start, selection.Ranges[0].End));
        Assert.Equal((560, 561), (selection.Ranges[1].Start, selection.Ranges[1].End));
        Assert.Equal(["winrar-390", "winrar-560"], selection.Folders);
        Assert.True(selection.NeedsMa4);
        Assert.False(selection.Empty);
    }

    [Fact]
    public void SelectFor_Rar4With700Executable_ExcludedNotCapable()
    {
        InstalledRARVersion[] installed = [new(700, "winrar-700", "p700")];

        var selection = RarFormatCompatibility.SelectFor(RarFormatCompatibility.RarFormat.Rar4, _broadRange, [], installed);

        Assert.True(selection.Empty);
        Assert.Empty(selection.Ranges);
        Assert.Empty(selection.Folders);
    }

    [Fact]
    public void SelectFor_Rar5In500To699Band_NeedsMa5Required()
    {
        InstalledRARVersion[] installed = [new(560, "winrar-560", "p560")];

        var selection = RarFormatCompatibility.SelectFor(RarFormatCompatibility.RarFormat.Rar5, _broadRange, [], installed);

        VersionRange range = Assert.Single(selection.Ranges);
        Assert.Equal((560, 561), (range.Start, range.End));
        Assert.False(selection.NeedsMa4);
        Assert.True(selection.NeedsMa5);
        Assert.False(selection.Empty);
    }

    [Fact]
    public void SelectFor_Rar5With700Executable_ExcludedCannotCoerce()
    {
        InstalledRARVersion[] installed = [new(700, "winrar-700", "p700")];

        var selection = RarFormatCompatibility.SelectFor(RarFormatCompatibility.RarFormat.Rar5, _broadRange, [], installed);

        Assert.True(selection.Empty);
        Assert.Empty(selection.Ranges);
        Assert.Empty(selection.Folders);
    }

    [Fact]
    public void SelectFor_Rar7With700Executable_NativeNoFlags()
    {
        InstalledRARVersion[] installed = [new(700, "winrar-700", "p700")];

        var selection = RarFormatCompatibility.SelectFor(RarFormatCompatibility.RarFormat.Rar7, _broadRange, [], installed);

        VersionRange range = Assert.Single(selection.Ranges);
        Assert.Equal((700, 701), (range.Start, range.End));
        Assert.False(selection.NeedsMa4);
        Assert.False(selection.NeedsMa5);
        Assert.False(selection.Empty);
    }

    [Fact]
    public void SelectFor_SameVersionFolderVariants_BothFoldersPreservedSingleRange()
    {
        InstalledRARVersion[] installed =
        [
            new(390, "winrar-390", "pA"),
            new(390, "winrar-390-beta1", "pB", "beta1"),
        ];

        var selection = RarFormatCompatibility.SelectFor(RarFormatCompatibility.RarFormat.Rar4, _broadRange, [], installed);

        VersionRange range = Assert.Single(selection.Ranges);
        Assert.Equal((390, 391), (range.Start, range.End));
        Assert.Equal(["winrar-390", "winrar-390-beta1"], selection.Folders);
        Assert.False(selection.Empty);
    }

    [Fact]
    public void SelectFor_Rar5WithOnly390Installed_EmptyIntersection()
    {
        InstalledRARVersion[] installed = [new(390, "winrar-390", "p390")];

        var selection = RarFormatCompatibility.SelectFor(RarFormatCompatibility.RarFormat.Rar5, _broadRange, [], installed);

        Assert.True(selection.Empty);
        Assert.Empty(selection.Ranges);
        Assert.Empty(selection.Folders);
    }

    [Fact]
    public void SelectFor_NoScan_Rar5UserRange500To699_ClipsRangeReturnsEmptyFolders()
    {
        VersionRange[] userRanges = [new VersionRange(500, 700)];

        var selection = RarFormatCompatibility.SelectFor(RarFormatCompatibility.RarFormat.Rar5, userRanges, [], []);

        VersionRange range = Assert.Single(selection.Ranges);
        Assert.Equal((500, 700), (range.Start, range.End));
        Assert.Empty(selection.Folders);
        Assert.False(selection.Empty);
        Assert.True(selection.NeedsMa5);
        Assert.False(selection.NeedsMa4);
    }

    [Fact]
    public void SelectFor_UserFoldersRestrictsToNamedFolder()
    {
        InstalledRARVersion[] installed =
        [
            new(390, "winrar-390", "pA"),
            new(390, "winrar-390-beta1", "pB", "beta1"),
        ];

        var selection = RarFormatCompatibility.SelectFor(
            RarFormatCompatibility.RarFormat.Rar4, _broadRange, ["winrar-390"], installed);

        Assert.Equal(["winrar-390"], selection.Folders);
        VersionRange range = Assert.Single(selection.Ranges);
        Assert.Equal((390, 391), (range.Start, range.End));
    }

    [Fact]
    public void SelectFor_UserRangeExcludesInstalledExecutable_EmptyIntersection()
    {
        InstalledRARVersion[] installed = [new(560, "winrar-560", "p560")];
        VersionRange[] userRanges = [new VersionRange(200, 500)];

        var selection = RarFormatCompatibility.SelectFor(RarFormatCompatibility.RarFormat.Rar4, userRanges, [], installed);

        Assert.True(selection.Empty);
        Assert.Empty(selection.Ranges);
        Assert.Empty(selection.Folders);
    }
}
