using System.Reflection;
using ReScene.NET.ViewModels.Reconstruction;
using ReScene.SRR;

namespace ReScene.NET.Tests;

/// <summary>
/// Pins <see cref="SrrSwitchMapper.Map"/> and its private group mappers. The mapper emits a
/// <em>partial</em> diff: each group is null when the SRR carries no information for it (so the
/// view-model leaves that toggle untouched). These tests assert null-vs-present per group and the
/// concrete switch/value chosen for each detected metadata input.
///
/// The four <see cref="SRRFile"/> inputs (CompressionMethod, DictionarySize, IsSolidArchive,
/// RARVersion) have <c>internal set</c> accessors that are only visible to the lib test assembly,
/// and several mapper branches guard against values that real RAR headers can never produce
/// (e.g. CompressionMethod 7, DictionarySize 8192). They are therefore set here via reflection on
/// the real public property names, which is the only way to exercise those defensive branches from
/// the app test project.
/// </summary>
public sealed class SrrSwitchMapperTests
{
    // ── Compression ──────────────────────────────────────────────────────

    [Fact]
    public void Map_NoCompressionMethod_LeavesCompressionGroupNull()
    {
        // Null CompressionMethod means the SRR said nothing about -m; the group must stay null
        // so the view-model leaves the existing compression toggle untouched.
        SRRFile srr = MakeSrr(compressionMethod: null);

        SrrSwitchMapper.SwitchDiff diff = SrrSwitchMapper.Map(srr);

        Assert.Null(diff.Compression);
    }

    [Fact]
    public void Map_CompressionMethod3_MapsToNormalMethod3()
    {
        // Method 3 is the "-m3" / Normal level: index 3 in ["Store","Fastest","Fast","Normal","Good","Best"].
        SRRFile srr = MakeSrr(compressionMethod: 3);

        SrrSwitchMapper.SwitchDiff diff = SrrSwitchMapper.Map(srr);

        SrrSwitchMapper.CompressionMap comp = Assert.NotNull(diff.Compression);
        Assert.Equal(3, comp.Method);
        Assert.Equal("Normal", comp.LogName);
    }

    [Fact]
    public void Map_CompressionMethod0_MapsToStore()
    {
        // Lower boundary of the valid 0..5 range: method 0 is Store.
        SRRFile srr = MakeSrr(compressionMethod: 0);

        SrrSwitchMapper.SwitchDiff diff = SrrSwitchMapper.Map(srr);

        SrrSwitchMapper.CompressionMap comp = Assert.NotNull(diff.Compression);
        Assert.Equal(0, comp.Method);
        Assert.Equal("Store", comp.LogName);
    }

    [Fact]
    public void Map_CompressionMethod5_MapsToBest()
    {
        // Upper boundary of the valid range: method 5 is Best (last name in the table).
        SRRFile srr = MakeSrr(compressionMethod: 5);

        SrrSwitchMapper.SwitchDiff diff = SrrSwitchMapper.Map(srr);

        SrrSwitchMapper.CompressionMap comp = Assert.NotNull(diff.Compression);
        Assert.Equal(5, comp.Method);
        Assert.Equal("Best", comp.LogName);
    }

    [Fact]
    public void Map_CompressionMethodOutOfRange_LeavesCompressionGroupNull()
    {
        // 7 is outside the 0..5 method table; the mapper guards against it and returns null
        // rather than indexing past the names array.
        SRRFile srr = MakeSrr(compressionMethod: 7);

        SrrSwitchMapper.SwitchDiff diff = SrrSwitchMapper.Map(srr);

        Assert.Null(diff.Compression);
    }

    [Fact]
    public void Map_CompressionMethodNegative_LeavesCompressionGroupNull()
    {
        // Negative methods are also out of range and must produce a null group.
        SRRFile srr = MakeSrr(compressionMethod: -1);

        SrrSwitchMapper.SwitchDiff diff = SrrSwitchMapper.Map(srr);

        Assert.Null(diff.Compression);
    }

    // ── Dictionary ───────────────────────────────────────────────────────

    [Fact]
    public void Map_NoDictionarySize_LeavesDictionaryGroupNull()
    {
        // No dictionary info in the SRR → null group, toggle untouched.
        SRRFile srr = MakeSrr(dictionarySize: null);

        SrrSwitchMapper.SwitchDiff diff = SrrSwitchMapper.Map(srr);

        Assert.Null(diff.Dictionary);
    }

    [Fact]
    public void Map_DictionarySize4096_MapsToMD4096KWithSize()
    {
        // 4096 KB maps to the MD4096K toggle; SizeKb carries the original value for the log line.
        SRRFile srr = MakeSrr(dictionarySize: 4096);

        SrrSwitchMapper.SwitchDiff diff = SrrSwitchMapper.Map(srr);

        SrrSwitchMapper.DictionaryMap dict = Assert.NotNull(diff.Dictionary);
        Assert.Equal(SrrSwitchMapper.DictionarySwitch.MD4096K, dict.Switch);
        Assert.Equal(4096, dict.SizeKb);
    }

    [Fact]
    public void Map_DictionarySize64_MapsToMD64K()
    {
        // Smallest mapped size: 64 KB → MD64K (lower end of the explicit switch table).
        SRRFile srr = MakeSrr(dictionarySize: 64);

        SrrSwitchMapper.SwitchDiff diff = SrrSwitchMapper.Map(srr);

        SrrSwitchMapper.DictionaryMap dict = Assert.NotNull(diff.Dictionary);
        Assert.Equal(SrrSwitchMapper.DictionarySwitch.MD64K, dict.Switch);
        Assert.Equal(64, dict.SizeKb);
    }

    [Fact]
    public void Map_DictionarySizeUnmapped_GroupPresentButSwitchNone()
    {
        // 8192 KB (8M) is in the deliberately-unmapped 8M..1G range: the group is still emitted
        // (so the clear-then-set runs and clears old toggles) but no switch is re-enabled.
        SRRFile srr = MakeSrr(dictionarySize: 8192);

        SrrSwitchMapper.SwitchDiff diff = SrrSwitchMapper.Map(srr);

        SrrSwitchMapper.DictionaryMap dict = Assert.NotNull(diff.Dictionary);
        Assert.Equal(SrrSwitchMapper.DictionarySwitch.None, dict.Switch);
        Assert.Equal(8192, dict.SizeKb);
    }

    // ── Solid flag ───────────────────────────────────────────────────────

    [Fact]
    public void Map_SolidArchiveTrue_MapsToSDashFalse()
    {
        // SwitchSDash is "-s-" (disable solid). A solid archive means -s- must be OFF.
        SRRFile srr = MakeSrr(isSolid: true);

        SrrSwitchMapper.SwitchDiff diff = SrrSwitchMapper.Map(srr);

        Assert.False(Assert.NotNull(diff.SwitchSDash));
    }

    [Fact]
    public void Map_SolidArchiveFalse_MapsToSDashTrue()
    {
        // A non-solid archive means -s- must be ON.
        SRRFile srr = MakeSrr(isSolid: false);

        SrrSwitchMapper.SwitchDiff diff = SrrSwitchMapper.Map(srr);

        Assert.True(Assert.NotNull(diff.SwitchSDash));
    }

    [Fact]
    public void Map_SolidArchiveUnknown_LeavesSDashNull()
    {
        // No solid info → null, leaving the -s- toggle untouched.
        SRRFile srr = MakeSrr(isSolid: null);

        SrrSwitchMapper.SwitchDiff diff = SrrSwitchMapper.Map(srr);

        Assert.Null(diff.SwitchSDash);
    }

    // ── Archive format ───────────────────────────────────────────────────

    [Fact]
    public void Map_NoRarVersion_LeavesFormatGroupNull()
    {
        // No RAR version detected → null format group, toggles untouched.
        SRRFile srr = MakeSrr(rarVersion: null);

        SrrSwitchMapper.SwitchDiff diff = SrrSwitchMapper.Map(srr);

        Assert.Null(diff.Format);
    }

    [Fact]
    public void Map_RarVersion29_SelectsMA4()
    {
        // Versions < 50 are RAR4: -ma4 on, -ma5 off.
        SRRFile srr = MakeSrr(rarVersion: 29);

        SrrSwitchMapper.SwitchDiff diff = SrrSwitchMapper.Map(srr);

        SrrSwitchMapper.FormatMap fmt = Assert.NotNull(diff.Format);
        Assert.True(fmt.MA4);
        Assert.False(fmt.MA5);
        Assert.Equal("Archive format: RAR4 (-ma4)", fmt.LogLine);
    }

    [Fact]
    public void Map_RarVersion50_SelectsMA5()
    {
        // 50 <= version < 70 is RAR5: -ma5 on, -ma4 off.
        SRRFile srr = MakeSrr(rarVersion: 50);

        SrrSwitchMapper.SwitchDiff diff = SrrSwitchMapper.Map(srr);

        SrrSwitchMapper.FormatMap fmt = Assert.NotNull(diff.Format);
        Assert.False(fmt.MA4);
        Assert.True(fmt.MA5);
        Assert.Equal("Archive format: RAR5 (-ma5)", fmt.LogLine);
    }

    [Fact]
    public void Map_RarVersion70_SelectsNeitherFormatSwitch()
    {
        // Version >= 70 is RAR7, which takes no -ma switch: both MA4 and MA5 false.
        SRRFile srr = MakeSrr(rarVersion: 70);

        SrrSwitchMapper.SwitchDiff diff = SrrSwitchMapper.Map(srr);

        SrrSwitchMapper.FormatMap fmt = Assert.NotNull(diff.Format);
        Assert.False(fmt.MA4);
        Assert.False(fmt.MA5);
        Assert.Equal("Archive format: RAR7", fmt.LogLine);
    }

    // ── Combined / independence ──────────────────────────────────────────

    [Fact]
    public void Map_EmptySrr_AllGroupsNull()
    {
        // An SRR with no detected switch metadata yields an all-null diff: applying it must
        // leave every bound toggle exactly as it was.
        SRRFile srr = MakeSrr();

        SrrSwitchMapper.SwitchDiff diff = SrrSwitchMapper.Map(srr);

        Assert.Null(diff.Compression);
        Assert.Null(diff.Dictionary);
        Assert.Null(diff.SwitchSDash);
        Assert.Null(diff.Format);
    }

    [Fact]
    public void Map_FullyPopulatedSrr_MapsEachGroupIndependently()
    {
        // All four inputs present: every group is populated and reflects its own input only.
        SRRFile srr = MakeSrr(compressionMethod: 3, dictionarySize: 4096, isSolid: true, rarVersion: 50);

        SrrSwitchMapper.SwitchDiff diff = SrrSwitchMapper.Map(srr);

        Assert.Equal(3, Assert.NotNull(diff.Compression).Method);
        Assert.Equal(SrrSwitchMapper.DictionarySwitch.MD4096K, Assert.NotNull(diff.Dictionary).Switch);
        Assert.False(Assert.NotNull(diff.SwitchSDash)); // solid → -s- off
        Assert.True(Assert.NotNull(diff.Format).MA5);   // RAR5
    }

    // ── Helpers ──────────────────────────────────────────────────────────

    /// <summary>
    /// Builds an <see cref="SRRFile"/> with the mapper-relevant detected-metadata properties set.
    /// These properties have <c>internal set</c> accessors not visible to this assembly, so they are
    /// assigned via reflection on their real public property names.
    /// </summary>
    private static SRRFile MakeSrr(
        int? compressionMethod = null,
        int? dictionarySize = null,
        bool? isSolid = null,
        int? rarVersion = null)
    {
        var srr = new SRRFile();
        SetProperty(srr, nameof(SRRFile.CompressionMethod), compressionMethod);
        SetProperty(srr, nameof(SRRFile.DictionarySize), dictionarySize);
        SetProperty(srr, nameof(SRRFile.IsSolidArchive), isSolid);
        SetProperty(srr, nameof(SRRFile.RARVersion), rarVersion);
        return srr;
    }

    private static void SetProperty(SRRFile srr, string name, object? value)
    {
        PropertyInfo property = typeof(SRRFile).GetProperty(name, BindingFlags.Public | BindingFlags.Instance)
            ?? throw new InvalidOperationException($"SRRFile.{name} not found; mapper test setup is out of date.");
        property.SetValue(srr, value);
    }
}
