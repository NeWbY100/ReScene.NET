using ReScene.App.Core.ViewModels.Reconstruction;
using ReScene.Core;
using ReScene.Core.Diagnostics;

namespace ReScene.App.Core.Tests;

/// <summary>
/// Verifies the RAR version-range and command-line argument matrix produced by
/// <see cref="RARCommandLineBuilder"/>. This logic was previously inlined in the reconstructor
/// view-model and therefore untestable; the tests assert the concrete argument strings, version
/// constraints, and matrix sizes for representative switch combinations.
/// </summary>
public sealed class RARCommandLineBuilderTests
{
    // ── BuildVersionRanges ───────────────────────────────────────────────

    [Fact]
    public void BuildVersionRanges_NothingSelected_ReturnsEmpty()
    {
        var settings = new RARSwitchSettings();

        List<VersionRange> ranges = RARCommandLineBuilder.BuildVersionRanges(settings);

        Assert.Empty(ranges);
    }

    [Fact]
    public void BuildVersionRanges_AllVersions_ReturnsExpectedRangesInOrder()
    {
        var settings = new RARSwitchSettings
        {
            Version2 = true,
            Version3 = true,
            Version4 = true,
            Version5 = true,
            Version6 = true,
            Version7 = true,
        };

        List<VersionRange> ranges = RARCommandLineBuilder.BuildVersionRanges(settings);

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
        var settings = new RARSwitchSettings { Version5 = true };

        List<VersionRange> ranges = RARCommandLineBuilder.BuildVersionRanges(settings);

        VersionRange range = Assert.Single(ranges);
        Assert.Equal((500, 600), (range.Start, range.End));
    }

    // ── BuildCommandLineArguments ────────────────────────────────────────

    [Fact]
    public void BuildCommandLineArguments_NoSwitches_ReturnsSingleAddOnlyCombination()
    {
        var settings = new RARSwitchSettings();

        IReadOnlyList<RARCommandLineArgument[]> matrix = RARCommandLineBuilder.BuildCommandLineArguments(settings, CancellationToken.None);

        RARCommandLineArgument[] only = Assert.Single(matrix);
        RARCommandLineArgument add = Assert.Single(only);
        Assert.Equal("a", add.Argument);
        Assert.Equal(200, add.MinimumVersion);
    }

    [Fact]
    public void BuildCommandLineArguments_SingleCompressionLevel_AppendsAfterAddCommand()
    {
        var settings = new RARSwitchSettings { SwitchM5 = true };

        IReadOnlyList<RARCommandLineArgument[]> matrix = RARCommandLineBuilder.BuildCommandLineArguments(settings, CancellationToken.None);

        RARCommandLineArgument[] combo = Assert.Single(matrix);
        Assert.Equal(["a", "-m5"], combo.Select(c => c.Argument));
    }

    [Fact]
    public void BuildCommandLineArguments_MultipleCompressionLevels_ProducesOneCombinationEach()
    {
        var settings = new RARSwitchSettings { SwitchM0 = true, SwitchM3 = true, SwitchM5 = true };

        IReadOnlyList<RARCommandLineArgument[]> matrix = RARCommandLineBuilder.BuildCommandLineArguments(settings, CancellationToken.None);

        Assert.Equal(3, matrix.Count);
        // Each combination is "a" followed by exactly one compression level.
        Assert.Equal(["-m0", "-m3", "-m5"], matrix.Select(c => c[^1].Argument));
        Assert.All(matrix, c => Assert.Equal("a", c[0].Argument));
    }

    [Fact]
    public void BuildCommandLineArguments_CartesianProduct_MultipliesIndependentDimensions()
    {
        // 2 compression levels × 2 archive formats × 3 dict sizes = 12 combinations.
        var settings = new RARSwitchSettings
        {
            SwitchM0 = true,
            SwitchM5 = true,
            SwitchMA4 = true,
            SwitchMA5 = true,
            SwitchMD64K = true,
            SwitchMD128K = true,
            SwitchMD256K = true,
        };

        IReadOnlyList<RARCommandLineArgument[]> matrix = RARCommandLineBuilder.BuildCommandLineArguments(settings, CancellationToken.None);

        Assert.Equal(2 * 2 * 3, matrix.Count);
    }

    [Fact]
    public void BuildCommandLineArguments_ThreadRange_ProducesOneComboPerThreadCount()
    {
        var settings = new RARSwitchSettings { SwitchMT = true, SwitchMTStart = 2, SwitchMTEnd = 4 };

        IReadOnlyList<RARCommandLineArgument[]> matrix = RARCommandLineBuilder.BuildCommandLineArguments(settings, CancellationToken.None);

        Assert.Equal(3, matrix.Count);   // -mt2, -mt3, -mt4
        Assert.All(matrix, combo => Assert.Contains(combo, c => c.Argument is "-mt2" or "-mt3" or "-mt4"));
    }

    [Fact]
    public void BuildCommandLineArguments_ReversedThreadRange_NormalisesInsteadOfProducingZero()
    {
        // Start > End (a typo or swapped imported config) must not collapse the whole matrix to
        // zero combinations and a silent "No match found".
        var settings = new RARSwitchSettings { SwitchMT = true, SwitchMTStart = 4, SwitchMTEnd = 2 };

        IReadOnlyList<RARCommandLineArgument[]> matrix = RARCommandLineBuilder.BuildCommandLineArguments(settings, CancellationToken.None);

        Assert.Equal(3, matrix.Count);   // normalised to 2..4
        Assert.Contains(matrix, combo => combo.Any(c => c.Argument == "-mt2"));
        Assert.Contains(matrix, combo => combo.Any(c => c.Argument == "-mt4"));
    }

    [Fact]
    public void BuildCommandLineArguments_ArchiveFormatSwitch_CarriesVersionRange()
    {
        var settings = new RARSwitchSettings { SwitchMA5 = true };

        RARCommandLineArgument[] combo = Assert.Single(RARCommandLineBuilder.BuildCommandLineArguments(settings, CancellationToken.None));

        RARCommandLineArgument ma5 = Assert.Single(combo, c => c.Argument == "-ma5");
        Assert.Equal(500, ma5.MinimumVersion);
        Assert.Equal(699, ma5.MaximumVersion);
    }

    [Fact]
    public void BuildCommandLineArguments_DictSize_CarriesArchiveVersionConstraint()
    {
        var settings = new RARSwitchSettings { SwitchMD8M = true };

        RARCommandLineArgument[] combo = Assert.Single(RARCommandLineBuilder.BuildCommandLineArguments(settings, CancellationToken.None));

        RARCommandLineArgument md8m = Assert.Single(combo, c => c.Argument == "-md8m");
        Assert.Equal(500, md8m.MinimumVersion);
        Assert.Equal(RARArchiveVersion.RAR5 | RARArchiveVersion.RAR7, md8m.ArchiveVersion);
    }

    [Fact]
    public void BuildCommandLineArguments_SwitchAi_DoublesMatrixAndAddsAiToFirstHalf()
    {
        var settings = new RARSwitchSettings { SwitchAI = true };

        IReadOnlyList<RARCommandLineArgument[]> matrix = RARCommandLineBuilder.BuildCommandLineArguments(settings, CancellationToken.None);

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
        var settings = new RARSwitchSettings { SwitchMT = true, SwitchMTStart = 1, SwitchMTEnd = 4 };

        IReadOnlyList<RARCommandLineArgument[]> matrix = RARCommandLineBuilder.BuildCommandLineArguments(settings, CancellationToken.None);

        // z runs from Start (1) through End (4) inclusive → 4 combinations.
        Assert.Equal(4, matrix.Count);
        Assert.Equal(
            ["-mt1", "-mt2", "-mt3", "-mt4"],
            matrix.Select(c => c.Single(a => a.Argument.StartsWith("-mt", StringComparison.Ordinal)).Argument));
    }

    [Fact]
    public void BuildCommandLineArguments_ThreadRangeAboveMax_ClampsToMaxNoOverflow()
    {
        // #13: an unbounded End (e.g. int.MaxValue from a typo/imported config) must clamp to the
        // highest thread count any WinRAR accepts, not overflow or hang the expansion loop.
        var settings = new RARSwitchSettings { SwitchMT = true, SwitchMTStart = 1, SwitchMTEnd = int.MaxValue };

        IReadOnlyList<RARCommandLineArgument[]> matrix = RARCommandLineBuilder.BuildCommandLineArguments(settings, CancellationToken.None);

        Assert.Equal(RARCommandLineBuilder.MaxThreadCount, matrix.Count);   // -mt1 .. -mt64
        Assert.Contains(matrix, combo => combo.Any(c => c.Argument == "-mt64"));
        Assert.DoesNotContain(matrix, combo => combo.Any(c => c.Argument == "-mt65"));
    }

    [Fact]
    public void BuildCommandLineArguments_ThreadRangeStartAndEndZero_PreservesMt0()
    {
        // -mt0 is byte-significant and must not be floored away to -mt1.
        var settings = new RARSwitchSettings { SwitchMT = true, SwitchMTStart = 0, SwitchMTEnd = 0 };

        RARCommandLineArgument[] combo = Assert.Single(RARCommandLineBuilder.BuildCommandLineArguments(settings, CancellationToken.None));

        Assert.Contains(combo, c => c.Argument == "-mt0");
    }

    [Fact]
    public void BuildCommandLineArguments_ThreadRangeBothEndpointsOutOfRange_ClampsBeforeOrdering_SingleRow()
    {
        // Both endpoints are clamped to 0..64 BEFORE min/max ordering, so 100..200 (both > 64)
        // becomes a single -mt64 row — never an empty loop.
        var settings = new RARSwitchSettings { SwitchMT = true, SwitchMTStart = 100, SwitchMTEnd = 200 };

        RARCommandLineArgument[] combo = Assert.Single(RARCommandLineBuilder.BuildCommandLineArguments(settings, CancellationToken.None));

        Assert.Contains(combo, c => c.Argument == "-mt64");
    }

    [Fact]
    public void BuildCommandLineArguments_CardinalityExceedsCap_ThrowsBeforeAllocating()
    {
        // Every compression/archive-format/dict-size/mtime switch ticked, plus -ai and the full
        // 0..64 -mt range: 6 * 2 * 15 * 5 * 2 * 65 = 117,000 combinations, over the 100,000 cap.
        var settings = new RARSwitchSettings
        {
            SwitchM0 = true,
            SwitchM1 = true,
            SwitchM2 = true,
            SwitchM3 = true,
            SwitchM4 = true,
            SwitchM5 = true,
            SwitchMA4 = true,
            SwitchMA5 = true,
            SwitchMD64K = true,
            SwitchMD128K = true,
            SwitchMD256K = true,
            SwitchMD512K = true,
            SwitchMD1024K = true,
            SwitchMD2048K = true,
            SwitchMD4096K = true,
            SwitchMD8M = true,
            SwitchMD16M = true,
            SwitchMD32M = true,
            SwitchMD64M = true,
            SwitchMD128M = true,
            SwitchMD256M = true,
            SwitchMD512M = true,
            SwitchMD1G = true,
            SwitchTSM0 = true,
            SwitchTSM1 = true,
            SwitchTSM2 = true,
            SwitchTSM3 = true,
            SwitchTSM4 = true,
            SwitchAI = true,
            SwitchMT = true,
            SwitchMTStart = 0,
            SwitchMTEnd = 64,
        };

        RARCommandLineMatrixTooLargeException ex = Assert.Throws<RARCommandLineMatrixTooLargeException>(
            () => RARCommandLineBuilder.BuildCommandLineArguments(settings, CancellationToken.None));

        Assert.Equal(RARCommandLineBuilder.MaxMatrixCardinality, ex.MaxCardinality);
        Assert.True(ex.Cardinality > ex.MaxCardinality);
    }

    [Fact]
    public void BuildCommandLineArguments_CancelledToken_ThrowsOperationCanceledPromptly()
    {
        var settings = new RARSwitchSettings();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.Throws<OperationCanceledException>(
            () => RARCommandLineBuilder.BuildCommandLineArguments(settings, cts.Token));
    }

    [Fact]
    public void BuildCommandLineArguments_SimpleSwitches_AppearInExpectedOrder()
    {
        var settings = new RARSwitchSettings { SwitchR = true, SwitchDS = true, SwitchSDash = true };

        RARCommandLineArgument[] combo = Assert.Single(RARCommandLineBuilder.BuildCommandLineArguments(settings, CancellationToken.None));

        Assert.Equal(["a", "-r", "-ds", "-s-"], combo.Select(c => c.Argument));
    }

    [Fact]
    public void BuildCommandLineArguments_SwitchS_EmitsSolidNotDisable()
    {
        var settings = new RARSwitchSettings { Version2 = true, SwitchR = true, SwitchDS = true, SwitchS = true };

        IReadOnlyList<RARCommandLineArgument[]> result = RARCommandLineBuilder.BuildCommandLineArguments(settings, CancellationToken.None);

        string[] args = [.. result[0].Select(a => a.Argument)];
        Assert.Contains("-s", args);
        Assert.DoesNotContain("-s-", args);
        Assert.Equal(["a", "-r", "-ds", "-s"], args);
    }

    [Fact]
    public void BuildCommandLineArguments_SwitchS_TakesPrecedenceOverSwitchSDash()
    {
        // Defense in depth: even if both reach the builder, only -s is emitted.
        var settings = new RARSwitchSettings { Version2 = true, SwitchS = true, SwitchSDash = true };

        string[] args = [.. RARCommandLineBuilder.BuildCommandLineArguments(settings, CancellationToken.None)[0].Select(a => a.Argument)];
        Assert.Contains("-s", args);
        Assert.DoesNotContain("-s-", args);
    }

    [Fact]
    public void BuildCommandLineArguments_VolumeWithOldNaming_AddsVolumeAndVnSwitch()
    {
        var settings = new RARSwitchSettings
        {
            SwitchV = true,
            VolumeSize = "100",
            VolumeSizeUnitIndex = 1, // KB
            UseOldVolumeNaming = true,
        };

        RARCommandLineArgument[] combo = Assert.Single(RARCommandLineBuilder.BuildCommandLineArguments(settings, CancellationToken.None));

        Assert.Contains(combo, c => c.Argument == "-v100");
        RARCommandLineArgument vn = Assert.Single(combo, c => c.Argument == "-vn");
        Assert.Equal(300, vn.MinimumVersion);
        Assert.Equal(699, vn.MaximumVersion);
    }

    [Fact]
    public void BuildCommandLineArguments_VolumeWithoutOldNaming_OmitsVnSwitch()
    {
        var settings = new RARSwitchSettings
        {
            SwitchV = true,
            VolumeSize = "100",
            VolumeSizeUnitIndex = 1,
            UseOldVolumeNaming = false,
        };

        RARCommandLineArgument[] combo = Assert.Single(RARCommandLineBuilder.BuildCommandLineArguments(settings, CancellationToken.None));

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
        var settings = new RARSwitchSettings { VolumeSize = size, VolumeSizeUnitIndex = unitIndex };

        string arg = RARCommandLineBuilder.BuildVolumeArgument(settings);

        Assert.Equal(expected, arg);
    }

    [Fact]
    public void BuildVolumeArgument_InvalidSize_FallsBackToDefaultKilobytes()
    {
        var settings = new RARSwitchSettings { VolumeSize = "not-a-number", VolumeSizeUnitIndex = 1 };

        string arg = RARCommandLineBuilder.BuildVolumeArgument(settings);

        // Default of 15000 KB (with the 'k' suffix) is used when the size string cannot be
        // parsed, regardless of the selected unit — see #21.
        Assert.Equal("-v15000k", arg);
    }

    [Fact]
    public void BuildVolumeArgument_BlankSize_FallsBackToDefaultKilobytes()
    {
        // #21: a blank size (unedited/cleared field) must fall back to the default rather than
        // parsing to 0 and producing a nonsense "-v0..." argument.
        var settings = new RARSwitchSettings { VolumeSize = "", VolumeSizeUnitIndex = 3 }; // GB

        string arg = RARCommandLineBuilder.BuildVolumeArgument(settings);

        Assert.Equal("-v15000k", arg);
    }

    [Fact]
    public void BuildVolumeArgument_SizeOverflowsUnitConversion_FallsBackToDefaultInsteadOfThrowing()
    {
        // #21: an extreme size (e.g. a pasted/typo'd value) must not overflow the per-unit
        // multiplication into a wrapped/garbage "-v" argument; it falls back to the default.
        var settings = new RARSwitchSettings { VolumeSize = long.MaxValue.ToString(), VolumeSizeUnitIndex = 3 }; // GB

        string arg = RARCommandLineBuilder.BuildVolumeArgument(settings);

        Assert.Equal("-v15000k", arg);
    }

    [Fact]
    public void BuildVolumeArgument_UnknownUnitIndex_FallsBackToKilobyteFormat()
    {
        var settings = new RARSwitchSettings { VolumeSize = "100", VolumeSizeUnitIndex = 99 };

        string arg = RARCommandLineBuilder.BuildVolumeArgument(settings);

        Assert.Equal("-v100", arg);
    }

    [Fact]
    public void BuildVersionRanges_Scanned_TightRangePerSelectedVersion()
    {
        var settings = new RARSwitchSettings
        {
            HasScannedVersions = true,
            SelectedRARVersions = [560, 624],
        };

        List<VersionRange> ranges = RARCommandLineBuilder.BuildVersionRanges(settings);

        Assert.Equal(2, ranges.Count);
        Assert.Equal((560, 561), (ranges[0].Start, ranges[0].End));
        Assert.Equal((624, 625), (ranges[1].Start, ranges[1].End));
    }

    [Fact]
    public void BuildVersionRanges_Scanned_DedupsAndSorts()
    {
        var settings = new RARSwitchSettings
        {
            HasScannedVersions = true,
            SelectedRARVersions = [560, 560, 500],
        };

        List<VersionRange> ranges = RARCommandLineBuilder.BuildVersionRanges(settings);

        int[] expectedStarts = [500, 560];
        Assert.Equal(expectedStarts, ranges.Select(r => r.Start).ToArray());
    }

    [Fact]
    public void BuildVersionRanges_Scanned_EmptySelection_ReturnsEmpty()
    {
        var settings = new RARSwitchSettings { HasScannedVersions = true, Version5 = true };

        List<VersionRange> ranges = RARCommandLineBuilder.BuildVersionRanges(settings);

        Assert.Empty(ranges);  // scanned + nothing ticked -> no versions (Start guard blocks the run)
    }

    [Fact]
    public void BuildVersionRanges_NotScanned_FallsBackToBroadMajorRanges()
    {
        var settings = new RARSwitchSettings { HasScannedVersions = false, Version5 = true, Version6 = true };

        List<VersionRange> ranges = RARCommandLineBuilder.BuildVersionRanges(settings);

        Assert.Equal(2, ranges.Count);
        Assert.Equal((500, 600), (ranges[0].Start, ranges[0].End));
        Assert.Equal((600, 700), (ranges[1].Start, ranges[1].End));
    }
}
