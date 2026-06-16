using ReScene.Core;
using ReScene.Core.Diagnostics;
using ReScene.NET.ViewModels.Reconstruction;

namespace ReScene.NET.Tests;

/// <summary>
/// Verifies the RAR version-range and command-line argument matrix produced by
/// <see cref="RarCommandLineBuilder"/>. This logic was previously inlined in the reconstructor
/// view-model and therefore untestable; the tests assert the concrete argument strings, version
/// constraints, and matrix sizes for representative switch combinations.
/// </summary>
public sealed class RarCommandLineBuilderTests
{
    // ── BuildVersionRanges ───────────────────────────────────────────────

    [Fact]
    public void BuildVersionRanges_NothingSelected_ReturnsEmpty()
    {
        var settings = new RarSwitchSettings();

        List<VersionRange> ranges = RarCommandLineBuilder.BuildVersionRanges(settings);

        Assert.Empty(ranges);
    }

    [Fact]
    public void BuildVersionRanges_AllVersions_ReturnsExpectedRangesInOrder()
    {
        var settings = new RarSwitchSettings
        {
            Version2 = true,
            Version3 = true,
            Version4 = true,
            Version5 = true,
            Version6 = true,
            Version7 = true,
        };

        List<VersionRange> ranges = RarCommandLineBuilder.BuildVersionRanges(settings);

        Assert.Equal(6, ranges.Count);
        Assert.Equal((200, 300), (ranges[0].Start, ranges[0].End));
        Assert.Equal((300, 400), (ranges[1].Start, ranges[1].End));
        Assert.Equal((400, 500), (ranges[2].Start, ranges[2].End));
        Assert.Equal((500, 600), (ranges[3].Start, ranges[3].End));
        Assert.Equal((600, 700), (ranges[4].Start, ranges[4].End));
        Assert.Equal((700, 800), (ranges[5].Start, ranges[5].End));
    }

    [Fact]
    public void BuildVersionRanges_SingleVersion_ReturnsOnlyThatRange()
    {
        var settings = new RarSwitchSettings { Version5 = true };

        List<VersionRange> ranges = RarCommandLineBuilder.BuildVersionRanges(settings);

        VersionRange range = Assert.Single(ranges);
        Assert.Equal((500, 600), (range.Start, range.End));
    }

    // ── BuildCommandLineArguments ────────────────────────────────────────

    [Fact]
    public void BuildCommandLineArguments_NoSwitches_ReturnsSingleAddOnlyCombination()
    {
        var settings = new RarSwitchSettings();

        List<RARCommandLineArgument[]> matrix = RarCommandLineBuilder.BuildCommandLineArguments(settings);

        RARCommandLineArgument[] only = Assert.Single(matrix);
        RARCommandLineArgument add = Assert.Single(only);
        Assert.Equal("a", add.Argument);
        Assert.Equal(200, add.MinimumVersion);
    }

    [Fact]
    public void BuildCommandLineArguments_SingleCompressionLevel_AppendsAfterAddCommand()
    {
        var settings = new RarSwitchSettings { SwitchM5 = true };

        List<RARCommandLineArgument[]> matrix = RarCommandLineBuilder.BuildCommandLineArguments(settings);

        RARCommandLineArgument[] combo = Assert.Single(matrix);
        Assert.Equal(["a", "-m5"], combo.Select(c => c.Argument));
    }

    [Fact]
    public void BuildCommandLineArguments_MultipleCompressionLevels_ProducesOneCombinationEach()
    {
        var settings = new RarSwitchSettings { SwitchM0 = true, SwitchM3 = true, SwitchM5 = true };

        List<RARCommandLineArgument[]> matrix = RarCommandLineBuilder.BuildCommandLineArguments(settings);

        Assert.Equal(3, matrix.Count);
        // Each combination is "a" followed by exactly one compression level.
        Assert.Equal(["-m0", "-m3", "-m5"], matrix.Select(c => c[^1].Argument));
        Assert.All(matrix, c => Assert.Equal("a", c[0].Argument));
    }

    [Fact]
    public void BuildCommandLineArguments_CartesianProduct_MultipliesIndependentDimensions()
    {
        // 2 compression levels × 2 archive formats × 3 dict sizes = 12 combinations.
        var settings = new RarSwitchSettings
        {
            SwitchM0 = true,
            SwitchM5 = true,
            SwitchMA4 = true,
            SwitchMA5 = true,
            SwitchMD64K = true,
            SwitchMD128K = true,
            SwitchMD256K = true,
        };

        List<RARCommandLineArgument[]> matrix = RarCommandLineBuilder.BuildCommandLineArguments(settings);

        Assert.Equal(2 * 2 * 3, matrix.Count);
    }

    [Fact]
    public void BuildCommandLineArguments_ArchiveFormatSwitch_CarriesVersionRange()
    {
        var settings = new RarSwitchSettings { SwitchMA5 = true };

        RARCommandLineArgument[] combo = Assert.Single(RarCommandLineBuilder.BuildCommandLineArguments(settings));

        RARCommandLineArgument ma5 = Assert.Single(combo, c => c.Argument == "-ma5");
        Assert.Equal(500, ma5.MinimumVersion);
        Assert.Equal(699, ma5.MaximumVersion);
    }

    [Fact]
    public void BuildCommandLineArguments_DictSize_CarriesArchiveVersionConstraint()
    {
        var settings = new RarSwitchSettings { SwitchMD8M = true };

        RARCommandLineArgument[] combo = Assert.Single(RarCommandLineBuilder.BuildCommandLineArguments(settings));

        RARCommandLineArgument md8m = Assert.Single(combo, c => c.Argument == "-md8m");
        Assert.Equal(500, md8m.MinimumVersion);
        Assert.Equal(RARArchiveVersion.RAR5 | RARArchiveVersion.RAR7, md8m.ArchiveVersion);
    }

    [Fact]
    public void BuildCommandLineArguments_SwitchAi_DoublesMatrixAndAddsAiToFirstHalf()
    {
        var settings = new RarSwitchSettings { SwitchAI = true };

        List<RARCommandLineArgument[]> matrix = RarCommandLineBuilder.BuildCommandLineArguments(settings);

        // The -ai dimension iterates twice: once with -ai present, once without.
        Assert.Equal(2, matrix.Count);
        Assert.Contains(matrix, combo => combo.Any(c => c.Argument == "-ai"));
        Assert.Contains(matrix, combo => combo.All(c => c.Argument != "-ai"));

        RARCommandLineArgument ai = matrix.SelectMany(c => c).Single(c => c.Argument == "-ai");
        Assert.Equal(390, ai.MinimumVersion);
    }

    [Fact]
    public void BuildCommandLineArguments_MultiThreadRange_AddsOneCombinationPerThreadCount()
    {
        var settings = new RarSwitchSettings { SwitchMT = true, SwitchMTStart = 1, SwitchMTEnd = 4 };

        List<RARCommandLineArgument[]> matrix = RarCommandLineBuilder.BuildCommandLineArguments(settings);

        // z runs from Start (1) through End (4) inclusive → 4 combinations.
        Assert.Equal(4, matrix.Count);
        Assert.Equal(
            ["-mt1", "-mt2", "-mt3", "-mt4"],
            matrix.Select(c => c.Single(a => a.Argument.StartsWith("-mt", StringComparison.Ordinal)).Argument));
    }

    [Fact]
    public void BuildCommandLineArguments_SimpleSwitches_AppearInExpectedOrder()
    {
        var settings = new RarSwitchSettings { SwitchR = true, SwitchDS = true, SwitchSDash = true };

        RARCommandLineArgument[] combo = Assert.Single(RarCommandLineBuilder.BuildCommandLineArguments(settings));

        Assert.Equal(["a", "-r", "-ds", "-s-"], combo.Select(c => c.Argument));
    }

    [Fact]
    public void BuildCommandLineArguments_VolumeWithOldNaming_AddsVolumeAndVnSwitch()
    {
        var settings = new RarSwitchSettings
        {
            SwitchV = true,
            VolumeSize = "100",
            VolumeSizeUnitIndex = 1, // KB
            UseOldVolumeNaming = true,
        };

        RARCommandLineArgument[] combo = Assert.Single(RarCommandLineBuilder.BuildCommandLineArguments(settings));

        Assert.Contains(combo, c => c.Argument == "-v100");
        RARCommandLineArgument vn = Assert.Single(combo, c => c.Argument == "-vn");
        Assert.Equal(300, vn.MinimumVersion);
        Assert.Equal(699, vn.MaximumVersion);
    }

    [Fact]
    public void BuildCommandLineArguments_VolumeWithoutOldNaming_OmitsVnSwitch()
    {
        var settings = new RarSwitchSettings
        {
            SwitchV = true,
            VolumeSize = "100",
            VolumeSizeUnitIndex = 1,
            UseOldVolumeNaming = false,
        };

        RARCommandLineArgument[] combo = Assert.Single(RarCommandLineBuilder.BuildCommandLineArguments(settings));

        Assert.DoesNotContain(combo, c => c.Argument == "-vn");
    }

    // ── BuildVolumeArgument ──────────────────────────────────────────────

    [Theory]
    [InlineData(0, "100", "-v100b")]            // Bytes
    [InlineData(1, "100", "-v100")]             // KB (×1000, no suffix)
    [InlineData(2, "100", "-v100000")]          // MB → KB (×1000)
    [InlineData(3, "2", "-v2000000")]           // GB → KB (×1000×1000)
    [InlineData(4, "100", "-v100k")]            // KiB (k suffix, ×1024)
    [InlineData(5, "2", "-v2048k")]             // MiB → KiB (×1024)
    [InlineData(6, "1", "-v1048576k")]          // GiB → KiB (×1024×1024)
    public void BuildVolumeArgument_FormatsBySizeUnit(int unitIndex, string size, string expected)
    {
        var settings = new RarSwitchSettings { VolumeSize = size, VolumeSizeUnitIndex = unitIndex };

        string arg = RarCommandLineBuilder.BuildVolumeArgument(settings);

        Assert.Equal(expected, arg);
    }

    [Fact]
    public void BuildVolumeArgument_InvalidSize_FallsBackToDefaultKilobytes()
    {
        var settings = new RarSwitchSettings { VolumeSize = "not-a-number", VolumeSizeUnitIndex = 1 };

        string arg = RarCommandLineBuilder.BuildVolumeArgument(settings);

        // Default of 15000 KB is used when the size string cannot be parsed.
        Assert.Equal("-v15000", arg);
    }

    [Fact]
    public void BuildVolumeArgument_UnknownUnitIndex_FallsBackToKilobyteFormat()
    {
        var settings = new RarSwitchSettings { VolumeSize = "100", VolumeSizeUnitIndex = 99 };

        string arg = RarCommandLineBuilder.BuildVolumeArgument(settings);

        Assert.Equal("-v100", arg);
    }
}
