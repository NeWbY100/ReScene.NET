using ReScene.Core;
using ReScene.NET.Models;
using ReScene.NET.Services;
using ReScene.NET.ViewModels;
using ReScene.NET.ViewModels.Reconstruction;
using ReScene.SRR;

namespace ReScene.NET.Tests;

/// <summary>
/// Pins the scalar option mapping in <see cref="ReconstructorConfigMapper"/> (Capture/Apply over
/// ~60 bound fields) and the import-snapshot mapping in <see cref="ImportedSrrStateMapper"/>.
/// Both mappers are pure (no WPF binding, no I/O), so the view-model is driven through its
/// internal fakes without a UI thread.
/// </summary>
public sealed class ReconstructorConfigMapperTests
{
    // ── Minimal fakes (kept private+nested so they cannot collide with other test files) ──

    /// <summary>No-op dispatcher: runs everything inline on the calling thread.</summary>
    private sealed class InlineUiDispatcher : IUiDispatcher
    {
        public void Invoke(Action action) => action();
        public void Post(Action action) => action();
        public void Post(Action action, System.Windows.Threading.DispatcherPriority priority) => action();
        public bool CheckAccess() => true;
    }

    /// <summary>Brute-force service that is never invoked in these mapper-only tests.</summary>
    private sealed class InertBruteForceService : IBruteForceService
    {
        public event EventHandler<BruteForceProgressEventArgs>? Progress { add { } remove { } }
        public event EventHandler<BruteForceStatusChangedEventArgs>? StatusChanged { add { } remove { } }
        public event EventHandler<LogEventArgs>? LogMessage { add { } remove { } }
        public event EventHandler<FileCopyProgressEventArgs>? FileCopyProgress { add { } remove { } }
        public event EventHandler<CRCValidationProgressEventArgs>? CRCValidationProgress { add { } remove { } }
        public event EventHandler<TimestampPreservationFailedEventArgs>? TimestampPreservationFailed { add { } remove { } }

        public Task<bool> RunAsync(BruteForceOptions options, CancellationToken cancellationToken = default)
            => Task.FromResult(true);
    }

    private static ReconstructorViewModel CreateVm()
        => new(new InertBruteForceService(), new NoOpFileDialogService(),
               settingsService: null, uiDispatcher: new InlineUiDispatcher());

    /// <summary>
    /// Sets a distinctive value on every option field Capture reads. Strings get unique markers,
    /// ints get unique numbers, every bool is forced to true, the nullable file-attr bools to true.
    /// This is the "source" state we capture from and expect Apply to restore exactly.
    /// </summary>
    private static void StampDistinctiveValues(ReconstructorViewModel vm)
    {
        vm.WinRarPath = "WR-PATH";
        vm.ReleasePath = "REL-PATH";
        vm.VerificationPath = "VER-PATH";
        vm.OutputPath = "OUT-PATH";

        vm.Version2 = true; vm.Version3 = true; vm.Version4 = true;
        vm.Version5 = true; vm.Version6 = true; vm.Version7 = true;

        vm.SwitchM0 = true; vm.SwitchM1 = true; vm.SwitchM2 = true;
        vm.SwitchM3 = true; vm.SwitchM4 = true; vm.SwitchM5 = true;

        vm.SwitchMA4 = true; vm.SwitchMA5 = true;

        vm.SwitchMD64K = true; vm.SwitchMD128K = true; vm.SwitchMD256K = true;
        vm.SwitchMD512K = true; vm.SwitchMD1024K = true; vm.SwitchMD2048K = true;
        vm.SwitchMD4096K = true; vm.SwitchMD8M = true; vm.SwitchMD16M = true;
        vm.SwitchMD32M = true; vm.SwitchMD64M = true; vm.SwitchMD128M = true;
        vm.SwitchMD256M = true; vm.SwitchMD512M = true; vm.SwitchMD1G = true;

        vm.SwitchTSM0 = true; vm.SwitchTSM1 = true; vm.SwitchTSM2 = true;
        vm.SwitchTSM3 = true; vm.SwitchTSM4 = true;
        vm.SwitchTSC0 = true; vm.SwitchTSC1 = true; vm.SwitchTSC2 = true;
        vm.SwitchTSC3 = true; vm.SwitchTSC4 = true;
        vm.SwitchTSA0 = true; vm.SwitchTSA1 = true; vm.SwitchTSA2 = true;
        vm.SwitchTSA3 = true; vm.SwitchTSA4 = true;

        vm.SwitchAI = true; vm.SwitchR = true; vm.SwitchDS = true; vm.SwitchSDash = true;
        vm.SwitchMT = true; vm.SwitchMTStart = 3; vm.SwitchMTEnd = 9;

        vm.SwitchV = true; vm.VolumeSize = "12345"; vm.VolumeSizeUnitIndex = 2;
        vm.UseOldVolumeNaming = true;

        vm.FileA = true; vm.FileI = true;

        vm.DeleteRARFiles = true; vm.DeleteDuplicateCRCFiles = true;
        vm.StopOnFirstMatch = true; vm.CompleteAllVolumes = true;
        vm.RenameToOriginal = true; vm.RenameToSfvNames = true;

        vm.EnableHostOSPatching = true;
    }

    /// <summary>
    /// Mutates every field to a value DIFFERENT from <see cref="StampDistinctiveValues"/> so a
    /// successful Apply cannot accidentally pass by leaving the VM unchanged.
    /// </summary>
    private static void StampOppositeValues(ReconstructorViewModel vm)
    {
        vm.WinRarPath = "X"; vm.ReleasePath = "X"; vm.VerificationPath = "X"; vm.OutputPath = "X";

        vm.Version2 = false; vm.Version3 = false; vm.Version4 = false;
        vm.Version5 = false; vm.Version6 = false; vm.Version7 = false;

        vm.SwitchM0 = false; vm.SwitchM1 = false; vm.SwitchM2 = false;
        vm.SwitchM3 = false; vm.SwitchM4 = false; vm.SwitchM5 = false;

        vm.SwitchMA4 = false; vm.SwitchMA5 = false;

        vm.SwitchMD64K = false; vm.SwitchMD128K = false; vm.SwitchMD256K = false;
        vm.SwitchMD512K = false; vm.SwitchMD1024K = false; vm.SwitchMD2048K = false;
        vm.SwitchMD4096K = false; vm.SwitchMD8M = false; vm.SwitchMD16M = false;
        vm.SwitchMD32M = false; vm.SwitchMD64M = false; vm.SwitchMD128M = false;
        vm.SwitchMD256M = false; vm.SwitchMD512M = false; vm.SwitchMD1G = false;

        vm.SwitchTSM0 = false; vm.SwitchTSM1 = false; vm.SwitchTSM2 = false;
        vm.SwitchTSM3 = false; vm.SwitchTSM4 = false;
        vm.SwitchTSC0 = false; vm.SwitchTSC1 = false; vm.SwitchTSC2 = false;
        vm.SwitchTSC3 = false; vm.SwitchTSC4 = false;
        vm.SwitchTSA0 = false; vm.SwitchTSA1 = false; vm.SwitchTSA2 = false;
        vm.SwitchTSA3 = false; vm.SwitchTSA4 = false;

        vm.SwitchAI = false; vm.SwitchR = false; vm.SwitchDS = false; vm.SwitchSDash = false;
        vm.SwitchMT = false; vm.SwitchMTStart = 0; vm.SwitchMTEnd = 0;

        vm.SwitchV = false; vm.VolumeSize = "0"; vm.VolumeSizeUnitIndex = 0;
        vm.UseOldVolumeNaming = false;

        vm.FileA = false; vm.FileI = false;

        vm.DeleteRARFiles = false; vm.DeleteDuplicateCRCFiles = false;
        vm.StopOnFirstMatch = false; vm.CompleteAllVolumes = false;
        vm.RenameToOriginal = false; vm.RenameToSfvNames = false;

        vm.EnableHostOSPatching = false;
    }

    // ── ReconstructorConfigMapper ─────────────────────────────

    /// <summary>
    /// Capture-then-Apply must restore every bound option field. We stamp distinctive values,
    /// capture, scramble the VM to the opposite, apply, then assert every property came back.
    /// This pins the 1:1 field copy in both Capture and Apply.
    /// </summary>
    [Fact]
    public void CaptureThenApply_RestoresEveryBoundField()
    {
        ReconstructorViewModel vm = CreateVm();
        StampDistinctiveValues(vm);

        ReconstructorConfig config = ReconstructorConfigMapper.Capture(vm);

        // Scramble so a no-op Apply could not masquerade as success.
        StampOppositeValues(vm);

        ReconstructorConfigMapper.Apply(vm, config);

        // Paths (strings).
        Assert.Equal("WR-PATH", vm.WinRarPath);
        Assert.Equal("REL-PATH", vm.ReleasePath);
        Assert.Equal("VER-PATH", vm.VerificationPath);
        Assert.Equal("OUT-PATH", vm.OutputPath);

        // RAR versions.
        Assert.True(vm.Version2); Assert.True(vm.Version3); Assert.True(vm.Version4);
        Assert.True(vm.Version5); Assert.True(vm.Version6); Assert.True(vm.Version7);

        // Compression method.
        Assert.True(vm.SwitchM0); Assert.True(vm.SwitchM1); Assert.True(vm.SwitchM2);
        Assert.True(vm.SwitchM3); Assert.True(vm.SwitchM4); Assert.True(vm.SwitchM5);

        // Archive format.
        Assert.True(vm.SwitchMA4); Assert.True(vm.SwitchMA5);

        // Dictionary sizes (all 15).
        Assert.True(vm.SwitchMD64K); Assert.True(vm.SwitchMD128K); Assert.True(vm.SwitchMD256K);
        Assert.True(vm.SwitchMD512K); Assert.True(vm.SwitchMD1024K); Assert.True(vm.SwitchMD2048K);
        Assert.True(vm.SwitchMD4096K); Assert.True(vm.SwitchMD8M); Assert.True(vm.SwitchMD16M);
        Assert.True(vm.SwitchMD32M); Assert.True(vm.SwitchMD64M); Assert.True(vm.SwitchMD128M);
        Assert.True(vm.SwitchMD256M); Assert.True(vm.SwitchMD512M); Assert.True(vm.SwitchMD1G);

        // Timestamp toggles (modification / creation / access).
        Assert.True(vm.SwitchTSM0); Assert.True(vm.SwitchTSM1); Assert.True(vm.SwitchTSM2);
        Assert.True(vm.SwitchTSM3); Assert.True(vm.SwitchTSM4);
        Assert.True(vm.SwitchTSC0); Assert.True(vm.SwitchTSC1); Assert.True(vm.SwitchTSC2);
        Assert.True(vm.SwitchTSC3); Assert.True(vm.SwitchTSC4);
        Assert.True(vm.SwitchTSA0); Assert.True(vm.SwitchTSA1); Assert.True(vm.SwitchTSA2);
        Assert.True(vm.SwitchTSA3); Assert.True(vm.SwitchTSA4);

        // Other option flags + multithread bounds (ints).
        Assert.True(vm.SwitchAI); Assert.True(vm.SwitchR); Assert.True(vm.SwitchDS);
        Assert.True(vm.SwitchSDash); Assert.True(vm.SwitchMT);
        Assert.Equal(3, vm.SwitchMTStart);
        Assert.Equal(9, vm.SwitchMTEnd);

        // Volume (bool + string + int).
        Assert.True(vm.SwitchV);
        Assert.Equal("12345", vm.VolumeSize);
        Assert.Equal(2, vm.VolumeSizeUnitIndex);
        Assert.True(vm.UseOldVolumeNaming);

        // File attributes (nullable bools).
        Assert.True(vm.FileA);
        Assert.True(vm.FileI);

        // Output options.
        Assert.True(vm.DeleteRARFiles); Assert.True(vm.DeleteDuplicateCRCFiles);
        Assert.True(vm.StopOnFirstMatch); Assert.True(vm.CompleteAllVolumes);
        Assert.True(vm.RenameToOriginal); Assert.True(vm.RenameToSfvNames);

        // Header patching.
        Assert.True(vm.EnableHostOSPatching);
    }

    /// <summary>
    /// The TSC/TSA timestamp triplets are independently mapped (a common copy-paste hazard).
    /// Capturing a state where TSC and TSA differ must restore them to their own distinct values,
    /// proving the two field groups are not cross-wired.
    /// </summary>
    [Fact]
    public void CaptureThenApply_KeepsTscAndTsaIndependent()
    {
        ReconstructorViewModel vm = CreateVm();
        // Distinct, asymmetric pattern across the swap-prone TSC/TSA pairs.
        vm.SwitchTSC0 = true; vm.SwitchTSC1 = false; vm.SwitchTSC2 = true;
        vm.SwitchTSC3 = false; vm.SwitchTSC4 = true;
        vm.SwitchTSA0 = false; vm.SwitchTSA1 = true; vm.SwitchTSA2 = false;
        vm.SwitchTSA3 = true; vm.SwitchTSA4 = false;

        ReconstructorConfig config = ReconstructorConfigMapper.Capture(vm);

        // Wipe to all-false before restoring.
        vm.SwitchTSC0 = vm.SwitchTSC1 = vm.SwitchTSC2 = vm.SwitchTSC3 = vm.SwitchTSC4 = false;
        vm.SwitchTSA0 = vm.SwitchTSA1 = vm.SwitchTSA2 = vm.SwitchTSA3 = vm.SwitchTSA4 = false;

        ReconstructorConfigMapper.Apply(vm, config);

        // Creation triplet restored to its own pattern.
        Assert.True(vm.SwitchTSC0); Assert.False(vm.SwitchTSC1); Assert.True(vm.SwitchTSC2);
        Assert.False(vm.SwitchTSC3); Assert.True(vm.SwitchTSC4);
        // Access triplet restored to ITS pattern (inverse), proving no TSC<->TSA bleed.
        Assert.False(vm.SwitchTSA0); Assert.True(vm.SwitchTSA1); Assert.False(vm.SwitchTSA2);
        Assert.True(vm.SwitchTSA3); Assert.False(vm.SwitchTSA4);
    }

    // ── ImportedSrrStateMapper ────────────────────────────────

    /// <summary>
    /// Apply(null) means "no SRR imported": it must yield a brand-new empty state with all
    /// collections empty and the packer type reset to None — never null.
    /// </summary>
    [Fact]
    public void ApplyNull_YieldsAllEmptyState()
    {
        ReconstructionImportState state = ImportedSrrStateMapper.Apply(null);

        Assert.NotNull(state);
        Assert.Empty(state.ArchiveFiles);
        Assert.Empty(state.ArchiveDirectories);
        Assert.Empty(state.FileTimestamps);
        Assert.Empty(state.DirTimestamps);
        Assert.Empty(state.ArchiveFileCrcs);
        Assert.Empty(state.OriginalRarFileNames);
        Assert.Null(state.SRRFilePath);
        Assert.Null(state.ArchiveComment);
        Assert.Null(state.CmtCompressedData);
        Assert.Equal(CustomPackerType.None, state.CustomPackerType);
    }

    /// <summary>
    /// An unrecognised CustomPackerType string in the persisted DTO must degrade gracefully to
    /// CustomPackerType.None rather than throwing (Enum.TryParse fallback path).
    /// </summary>
    [Fact]
    public void Apply_WithUnknownCustomPackerType_FallsBackToNone()
    {
        var dto = new ImportedSrrState { SRRFilePath = "x.srr", CustomPackerType = "Bogus" };

        ReconstructionImportState state = ImportedSrrStateMapper.Apply(dto);

        Assert.Equal(CustomPackerType.None, state.CustomPackerType);
    }

    /// <summary>
    /// A valid (but not the default) packer name must round-trip into the matching enum member,
    /// confirming the success branch of the Enum.TryParse is wired up.
    /// </summary>
    [Fact]
    public void Apply_WithKnownCustomPackerType_ParsesToEnumMember()
    {
        var dto = new ImportedSrrState
        {
            SRRFilePath = "x.srr",
            CustomPackerType = nameof(CustomPackerType.MaxUint32WithoutLargeFlag)
        };

        ReconstructionImportState state = ImportedSrrStateMapper.Apply(dto);

        Assert.Equal(CustomPackerType.MaxUint32WithoutLargeFlag, state.CustomPackerType);
    }

    /// <summary>
    /// Restored dictionaries and sets must use a case-insensitive comparer so that timestamp/CRC
    /// lookups by a path whose case differs from the stored key still hit. This pins the
    /// StringComparer.OrdinalIgnoreCase used when rebuilding the state.
    /// </summary>
    [Fact]
    public void Apply_RestoredCollections_UseCaseInsensitiveComparer()
    {
        DateTime ts = new(2026, 6, 14, 10, 30, 0, DateTimeKind.Utc);
        var dto = new ImportedSrrState
        {
            SRRFilePath = "x.srr",
            FileTimestamps = { ["Folder/File.RAR"] = ts },
            ArchiveFileCrcs = { ["Folder/File.RAR"] = "DEADBEEF" },
            ArchiveFiles = { "Folder/File.RAR" },
        };

        ReconstructionImportState state = ImportedSrrStateMapper.Apply(dto);

        // Lookups by a DIFFERENT case must succeed on every rebuilt collection.
        Assert.True(state.FileTimestamps.TryGetValue("folder/file.rar", out DateTime got));
        Assert.Equal(ts, got);
        Assert.True(state.ArchiveFileCrcs.TryGetValue("FOLDER/FILE.rar", out string? crc));
        Assert.Equal("DEADBEEF", crc);
        Assert.Contains("folder/FILE.rar", state.ArchiveFiles);
    }

    [Fact]
    public void Apply_InconsistentConfig_NormalisesRenamesOffWhenStopIsOff()
    {
        ReconstructorViewModel vm = CreateVm();
        var config = new ReconstructorConfig
        {
            StopOnFirstMatch = false,
            RenameToOriginal = true,
            RenameToSfvNames = true,
        };

        ReconstructorConfigMapper.Apply(vm, config);

        Assert.False(vm.StopOnFirstMatch);
        Assert.False(vm.RenameToOriginal);
        Assert.False(vm.RenameToSfvNames);
    }

    /// <summary>
    /// Apply faithfully copies the scalar/detected header fields and comment bytes from the DTO,
    /// so a captured snapshot reconstructs the same detected-header context on restore.
    /// </summary>
    [Fact]
    public void Apply_CopiesDetectedHeaderFieldsAndComment()
    {
        byte[] commentBytes = [1, 2, 3];
        byte[] cmtData = [9, 8, 7, 6];
        var dto = new ImportedSrrState
        {
            SRRFilePath = "rel.srr",
            ArchiveComment = "hello",
            ArchiveCommentBytes = commentBytes,
            CmtCompressedData = cmtData,
            CmtCompressionMethod = 0x35,
            DetectedFileHostOS = 2,
            DetectedFileAttributes = 0x20u,
            DetectedLargeFlag = true,
            DetectedHighPackSize = 5u,
            OriginalRarFileNames = ["cd1.rar", "cd2.rar"],
        };

        ReconstructionImportState state = ImportedSrrStateMapper.Apply(dto);

        Assert.Equal("rel.srr", state.SRRFilePath);
        Assert.Equal("hello", state.ArchiveComment);
        Assert.Equal(commentBytes, state.ArchiveCommentBytes);
        Assert.Equal(cmtData, state.CmtCompressedData);
        Assert.Equal((byte)0x35, state.CmtCompressionMethod);
        Assert.Equal((byte)2, state.DetectedFileHostOS);
        Assert.Equal(0x20u, state.DetectedFileAttributes);
        Assert.True(state.DetectedLargeFlag);
        Assert.Equal(5u, state.DetectedHighPackSize);
        Assert.Equal(new[] { "cd1.rar", "cd2.rar" }, state.OriginalRarFileNames);
    }
}
