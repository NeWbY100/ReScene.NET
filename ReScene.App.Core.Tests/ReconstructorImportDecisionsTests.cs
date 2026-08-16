using System.Runtime.CompilerServices;
using ReScene.App.Core.Services;
using ReScene.App.Core.ViewModels;
using ReScene.Core;
using ReScene.SRR;

namespace ReScene.App.Core.Tests;

/// <summary>
/// Pins the two SRR-import decisions that had no direct coverage. Both are pure branch tables where a
/// one-step perturbation is invisible without a per-arm theory.
/// <para>
/// Its sibling decision, <c>SetRARVersionsFromSRR</c>, is covered too. Its inputs live on
/// <c>SRRFile</c> behind <c>internal</c> setters, and ReScene.Lib grants InternalsVisibleTo only to
/// its own test project - so the metadata is synthesised through <c>UnsafeAccessor</c> rather than by
/// widening that grant across the submodule boundary or coupling a pure branch table to the SRR
/// binary format. The accessors are typed, so they fail loudly if a setter's signature changes.
/// </para>
/// </summary>
public sealed class ReconstructorImportDecisionsTests
{
    private sealed class InlineUiDispatcher : IUiDispatcher
    {
        public void Invoke(Action action) => action();
        public void Post(Action action) => action();
        public void Post(Action action, UiDispatcherPriority priority) => action();
        public bool CheckAccess() => true;
    }

    private sealed class InertBruteForceService : IBruteForceService
    {
        public event EventHandler<BruteForceProgressEventArgs>? Progress { add { } remove { } }
        public event EventHandler<BruteForceStatusChangedEventArgs>? StatusChanged { add { } remove { } }
        public event EventHandler<LogEventArgs>? LogMessage { add { } remove { } }
        public event EventHandler<FileCopyProgressEventArgs>? FileCopyProgress { add { } remove { } }
        public event EventHandler<CRCValidationProgressEventArgs>? CRCValidationProgress { add { } remove { } }
        public event EventHandler<TimestampPreservationFailedEventArgs>? TimestampPreservationFailed { add { } remove { } }

        public Task<BruteForceRunResult> RunAsync(BruteForceOptions options, CancellationToken cancellationToken = default)
            => Task.FromResult(new BruteForceRunResult(true, null));
    }

    private static ReconstructorViewModel CreateVm()
        => new(new InertBruteForceService(), new NoOpFileDialogService(),
               new InlineUiDispatcher(), new TestUiTimerFactory(), settingsService: null);

    // ── RAR version selection ────────────────────────────────

    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "set_RARVersion")]
    private static extern void SetRARVersion(SRRFile instance, int? value);

    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "set_DictionarySize")]
    private static extern void SetDictionarySize(SRRFile instance, int? value);

    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "set_HasFirstVolumeFlag")]
    private static extern void SetHasFirstVolumeFlag(SRRFile instance, bool? value);

    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "set_HasUnicodeNames")]
    private static extern void SetHasUnicodeNames(SRRFile instance, bool? value);

    private static SRRFile Srr(int? rarVersion = null, int? dictionarySize = null,
                               bool? firstVolumeFlag = null, bool? unicodeNames = null)
    {
        var srr = new SRRFile();
        SetRARVersion(srr, rarVersion);
        SetDictionarySize(srr, dictionarySize);
        SetHasFirstVolumeFlag(srr, firstVolumeFlag);
        SetHasUnicodeNames(srr, unicodeNames);
        return srr;
    }

    /// <summary>All six major flags, so a branch that changes one it should not is caught.</summary>
    private static bool[] Majors(ReconstructorViewModel vm) =>
        [vm.Version2, vm.Version3, vm.Version4, vm.Version5, vm.Version6, vm.Version7];

    [Fact]
    public void SetRARVersionsFromSRR_NoVersion_ChangesNothingAndLogsNothing()
    {
        // The early return. Asserted by absence: with a version present the method's FIRST act is to
        // clear all six flags, so a seeded pattern surviving intact is the signal.
        ReconstructorViewModel vm = CreateVm();
        vm.Version2 = vm.Version7 = true;
        vm.Version3 = vm.Version4 = vm.Version5 = vm.Version6 = false;
        int logsBefore = vm.LogEntries.Count;

        vm.SetRARVersionsFromSRRForTest(Srr());

        Assert.Equal([true, false, false, false, false, true], Majors(vm));
        Assert.Equal(logsBefore, vm.LogEntries.Count);
    }

    [Theory]
    //                      v2     v3     v4     v5     v6     v7
    [InlineData(70, new[] { false, false, false, false, false, true })]
    [InlineData(99, new[] { false, false, false, false, false, true })]
    [InlineData(50, new[] { false, false, false, true, true, false })]
    [InlineData(69, new[] { false, false, false, true, true, false })]
    public void SetRARVersionsFromSRR_ModernVersions(int unpVer, bool[] expected)
    {
        ReconstructorViewModel vm = CreateVm();

        vm.SetRARVersionsFromSRRForTest(Srr(unpVer));

        Assert.Equal(expected, Majors(vm));
    }

    [Fact]
    public void SetRARVersionsFromSRR_VersionAtLeast50_WinsOverTheLargeDictionaryArm()
    {
        // The >= 50 threshold cannot be pinned by the six flags: at 50 the arm and the legacy
        // fall-through produce the identical flag pattern AND the identical log line. It is only
        // observable when the large-dictionary arm would otherwise fire, because the two arms log
        // differently - so that is what this asserts.
        ReconstructorViewModel vm = CreateVm();

        vm.SetRARVersionsFromSRRForTest(Srr(rarVersion: 50, dictionarySize: 8192));

        Assert.Equal([false, false, false, true, true, false], Majors(vm));
        Assert.Contains(vm.LogEntries, l => l.EndsWith("RAR versions: 5.x, 6.x", StringComparison.Ordinal));
        Assert.DoesNotContain(vm.LogEntries, l => l.Contains("Large dictionary", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(8192, true)]    // > 4096 takes the large-dictionary arm
    [InlineData(4097, true)]
    [InlineData(4096, false)]   // NOT "> 4096" - falls through to the legacy table
    public void SetRARVersionsFromSRR_LargeDictionaryArm_OnlyAboveTheThreshold(int dictionaryKb, bool takesTheArm)
    {
        ReconstructorViewModel vm = CreateVm();

        vm.SetRARVersionsFromSRRForTest(Srr(rarVersion: 29, dictionarySize: dictionaryKb));

        // The arm selects exactly 5 and 6; the legacy table always also selects 5 and 6 but adds the
        // legacy majors, so v2/v3 tell them apart.
        // unpVer 29 in the legacy table sets 2.x (<= 29), 3.x (20-36) and 4.x (26-36).
        Assert.Equal(takesTheArm ? [false, false, false, true, true, false]
                                 : [true, true, true, true, true, false], Majors(vm));
    }

    [Theory]
    //                      v2     v3     v4     v5     v6     v7
    [InlineData(19, new[] { true, false, false, true, true, false })]   // below the 3.x range
    [InlineData(20, new[] { true, true, false, true, true, false })]    // 3.x lower bound
    [InlineData(26, new[] { true, true, true, true, true, false })]     // 4.x lower bound
    [InlineData(29, new[] { true, true, true, true, true, false })]     // 2.x upper bound
    [InlineData(30, new[] { false, true, true, true, true, false })]    // past 2.x
    // unpVer 36: the method has a special block forcing this result, but the surrounding ranges
    // already compute it (isRAR2 = 36 <= 29 is false; 36 is the upper bound of both 20-36 and
    // 26-36). Deleting that block therefore cannot fail this row - it is pinned for its RESULT, not
    // as evidence the block earns its keep. Preserved by transcription.
    [InlineData(36, new[] { false, true, true, true, true, false })]
    [InlineData(37, new[] { false, false, false, true, true, false })]  // past 3.x/4.x
    public void SetRARVersionsFromSRR_LegacyTable(int unpVer, bool[] expected)
    {
        ReconstructorViewModel vm = CreateVm();

        vm.SetRARVersionsFromSRRForTest(Srr(unpVer));

        Assert.Equal(expected, Majors(vm));
    }

    [Theory]
    [InlineData(true, null)]
    [InlineData(null, true)]
    public void SetRARVersionsFromSRR_FirstVolumeOrUnicode_SuppressesRAR2(bool? firstVolume, bool? unicode)
    {
        // 29 would otherwise select 2.x; either flag rules it out.
        ReconstructorViewModel vm = CreateVm();

        vm.SetRARVersionsFromSRRForTest(Srr(rarVersion: 29, firstVolumeFlag: firstVolume, unicodeNames: unicode));

        Assert.Equal([false, true, true, true, true, false], Majors(vm));
    }

    // ── Volume size units ────────────────────────────────────

    [Theory]
    [InlineData(0L)]
    [InlineData(-1L)]
    public void ApplyVolumeSize_NonPositive_IsIgnoredEntirely(long sizeBytes)
    {
        // "Entirely" means every piece of state the method would otherwise touch, each seeded to a
        // value it does NOT default to - so a regression that changes only the unit index, or only
        // emits the log line, still fails.
        ReconstructorViewModel vm = CreateVm();
        vm.SwitchV = true;
        vm.VolumeSize = "unchanged";
        vm.VolumeSizeUnitIndex = 5;
        int logsBefore = vm.LogEntries.Count;

        vm.ApplyVolumeSizeForTest(sizeBytes);

        Assert.True(vm.SwitchV);
        Assert.Equal("unchanged", vm.VolumeSize);
        Assert.Equal(5, vm.VolumeSizeUnitIndex);
        Assert.Equal(logsBefore, vm.LogEntries.Count);
    }

    [Theory]
    // Decimal units are tried before binary ones, largest first, by EXACT divisibility.
    [InlineData(2_000_000_000L, "2", 3)]              // GB
    [InlineData(5_000_000L, "5", 2)]                  // MB
    [InlineData(7_000L, "7", 1)]                      // KB
    [InlineData(3L * 1024 * 1024 * 1024, "3", 6)]     // GiB - not divisible by any decimal unit
    [InlineData(9L * 1024 * 1024, "9", 5)]            // MiB
    [InlineData(11L * 1024, "11", 4)]                 // KiB
    [InlineData(12_345L, "12345", 0)]                 // Bytes - divisible by nothing above
    // The row that actually pins DECIMAL-BEFORE-BINARY priority. Every other row is divisible by
    // exactly one unit, so reordering the arms cannot change them. This one divides exactly by both
    // GB (-> 2,097,152) and GiB (-> 1,953,125), so it can only come out as GB if decimal is tried
    // first.
    [InlineData(2_097_152_000_000_000L, "2097152", 3)]
    public void ApplyVolumeSize_PicksTheLargestExactlyDividingUnit(long sizeBytes, string expectedSize, int expectedUnitIndex)
    {
        ReconstructorViewModel vm = CreateVm();

        vm.ApplyVolumeSizeForTest(sizeBytes);

        Assert.True(vm.SwitchV);
        Assert.Equal(expectedSize, vm.VolumeSize);
        Assert.Equal(expectedUnitIndex, vm.VolumeSizeUnitIndex);
    }

    [Fact]
    public void ApplyVolumeSize_LogsTheChosenUnitByName()
    {
        // The log line names the unit from VolumeSizeUnits[index], so an off-by-one in the index
        // shows up here as the wrong word rather than a silently wrong number.
        ReconstructorViewModel vm = CreateVm();

        vm.ApplyVolumeSizeForTest(5_000_000L);

        Assert.Contains(vm.LogEntries, l => l.Contains("Volume size: 5 MB", StringComparison.Ordinal));
    }
}
