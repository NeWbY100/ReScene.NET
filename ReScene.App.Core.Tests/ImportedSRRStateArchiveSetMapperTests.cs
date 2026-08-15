using System.Text.Json;
using ReScene.App.Core.Models;
using ReScene.App.Core.ViewModels.Reconstruction;
using ReScene.SRR;

namespace ReScene.App.Core.Tests;

/// <summary>
/// Pins the complete, versioned per-archive-set round-trip in <see cref="ImportedSRRStateMapper"/>
/// (#22): <see cref="ArchiveSetDto"/> must carry every per-set field, <c>SchemaVersion</c> is a
/// presence marker (not a "non-empty" check), and a legacy DTO with no set list still falls back to
/// re-parsing the SRR via <see cref="ArchiveSetPlanner.ResolveSets"/>.
/// </summary>
public class ImportedSRRStateArchiveSetMapperTests
{
    // Mirrors the real (de)serializer options used by ReconstructorViewModel's config export/import
    // (System.Text.Json, WriteIndented + PropertyNameCaseInsensitive) — the round-trip test below
    // goes through the actual serializer, not just in-memory object copying.
    private static readonly JsonSerializerOptions SerializerOptions =
        new() { WriteIndented = true, PropertyNameCaseInsensitive = true };

    private static readonly string TwoSetSrrPath = Path.Combine(AppContext.BaseDirectory, "TestData",
        "cleanup_script", "007.A.View.To.A.Kill.1985.UE.iNTERNAL.DVDRip.XviD-iNCiTE.fine_2cd.srr");

    private static SRRArchiveSet MakeFullSet(
        string key, string dir, string[] volumes,
        (string file, string crc, DateTime m, DateTime c, DateTime a)[] files,
        (string dir, DateTime m, DateTime c, DateTime a)[] dirs,
        int compression, int dictionarySize, int rarVersion, bool isSolid, bool hasRecovery,
        byte hostOs, uint fileAttrs, bool hasLarge, uint highPack, uint highUnp)
    {
        var set = new SRRArchiveSet { Key = key, Directory = dir };

        foreach (string v in volumes)
        {
            set.VolumeNames.Add(v);
        }

        foreach ((string file, string crc, DateTime m, DateTime c, DateTime a) in files)
        {
            set.ArchivedFiles.Add(file);
            set.ArchivedFileCrcs[file] = crc;
            set.ArchivedFileTimestamps[file] = m;
            set.ArchivedFileCreationTimes[file] = c;
            set.ArchivedFileAccessTimes[file] = a;
        }

        foreach ((string d, DateTime m, DateTime c, DateTime a) in dirs)
        {
            set.ArchivedDirectories.Add(d);
            set.ArchivedDirectoryTimestamps[d] = m;
            set.ArchivedDirectoryCreationTimes[d] = c;
            set.ArchivedDirectoryAccessTimes[d] = a;
        }

        set.CompressionMethod = compression;
        set.DictionarySize = dictionarySize;
        set.RARVersion = rarVersion;
        set.IsSolid = isSolid;
        set.HasRecoveryRecord = hasRecovery;
        set.DetectedHostOS = hostOs;
        set.DetectedFileAttributes = fileAttrs;
        set.HasLargeFiles = hasLarge;
        set.DetectedHighPackSize = highPack;
        set.DetectedHighUnpSize = highUnp;

        return set;
    }

    /// <summary>
    /// Capture -> real JSON (de)serialize -> Apply must restore a two-set state with every per-set
    /// field intact: volumes in order, per-file/per-directory CRCs and all three timestamp kinds,
    /// compression/dictionary/version/solid, host OS/attributes, large flags, and
    /// HasRecoveryRecord — with no cross-set bleed (the two sets are stamped with distinct, in
    /// places inverted, values) and case-insensitive lookups preserved on every restored collection.
    /// </summary>
    [Fact]
    public void CaptureThenApply_RoundTripsArchiveSetsCompletely_ThroughRealSerializer()
    {
        DateTime t1 = new(2024, 1, 1, 1, 1, 1, DateTimeKind.Utc);
        DateTime t2 = new(2024, 1, 1, 2, 2, 2, DateTimeKind.Utc);
        DateTime t3 = new(2024, 1, 1, 3, 3, 3, DateTimeKind.Utc);
        DateTime t4 = new(2024, 1, 1, 4, 4, 4, DateTimeKind.Utc);
        DateTime t5 = new(2024, 1, 1, 5, 5, 5, DateTimeKind.Utc);
        DateTime t6 = new(2024, 1, 1, 6, 6, 6, DateTimeKind.Utc);

        SRRArchiveSet set1 = MakeFullSet(
            "DVD1/aln-re4a", "DVD1",
            ["DVD1\\aln-re4a.rar", "DVD1\\aln-re4a.r00", "DVD1\\aln-re4a.r01"],
            [("DVD1/aln-re4a.iso", "AAAAAAAA", t1, t2, t3)],
            [("DVD1/Subs", t4, t5, t6)],
            compression: 3, dictionarySize: 4096, rarVersion: 29, isSolid: true, hasRecovery: true,
            hostOs: 2, fileAttrs: 0x20u, hasLarge: false, highPack: 10u, highUnp: 20u);

        SRRArchiveSet set2 = MakeFullSet(
            "DVD2/aln-re4b", "DVD2",
            ["DVD2\\aln-re4b.rar", "DVD2\\aln-re4b.r00"],
            [("DVD2/aln-re4b.iso", "CCCCCCCC", t6, t5, t4)],
            [("DVD2/Subs", t3, t2, t1)],
            compression: 0, dictionarySize: 65536, rarVersion: 50, isSolid: false, hasRecovery: false,
            hostOs: 3, fileAttrs: 0x21u, hasLarge: true, highPack: 30u, highUnp: 40u);

        var state = new ReconstructionImportState { ArchiveSets = [set1, set2] };

        ImportedSRRState? dto = ImportedSRRStateMapper.Capture(state, customPackerWarning: null);
        Assert.NotNull(dto);
        Assert.Equal(ImportedSRRState.CurrentSchemaVersion, dto.SchemaVersion);

        // Round-trip through the REAL serializer (matching ReconstructorViewModel's config
        // export/import), not just an in-memory copy.
        string json = JsonSerializer.Serialize(dto, SerializerOptions);
        ImportedSRRState? roundTripped = JsonSerializer.Deserialize<ImportedSRRState>(json, SerializerOptions);
        Assert.NotNull(roundTripped);

        ReconstructionImportState restored = ImportedSRRStateMapper.Apply(roundTripped);

        Assert.Equal(2, restored.ArchiveSets.Count);
        SRRArchiveSet r1 = Assert.Single(restored.ArchiveSets, s => s.Key == "DVD1/aln-re4a");
        SRRArchiveSet r2 = Assert.Single(restored.ArchiveSets, s => s.Key == "DVD2/aln-re4b");

        // Set 1 — full field check.
        Assert.Equal("DVD1", r1.Directory);
        Assert.Equal(["DVD1\\aln-re4a.rar", "DVD1\\aln-re4a.r00", "DVD1\\aln-re4a.r01"], r1.VolumeNames);
        Assert.Contains("DVD1/aln-re4a.iso", r1.ArchivedFiles);
        Assert.Equal("AAAAAAAA", r1.ArchivedFileCrcs["DVD1/aln-re4a.iso"]);
        Assert.Equal(t1, r1.ArchivedFileTimestamps["DVD1/aln-re4a.iso"]);
        Assert.Equal(t2, r1.ArchivedFileCreationTimes["DVD1/aln-re4a.iso"]);
        Assert.Equal(t3, r1.ArchivedFileAccessTimes["DVD1/aln-re4a.iso"]);
        Assert.Contains("DVD1/Subs", r1.ArchivedDirectories);
        Assert.Equal(t4, r1.ArchivedDirectoryTimestamps["DVD1/Subs"]);
        Assert.Equal(t5, r1.ArchivedDirectoryCreationTimes["DVD1/Subs"]);
        Assert.Equal(t6, r1.ArchivedDirectoryAccessTimes["DVD1/Subs"]);
        Assert.Equal(3, r1.CompressionMethod);
        Assert.Equal(4096, r1.DictionarySize);
        Assert.Equal(29, r1.RARVersion);
        Assert.True(r1.IsSolid);
        Assert.True(r1.HasRecoveryRecord);
        Assert.Equal((byte)2, r1.DetectedHostOS);
        Assert.Equal(0x20u, r1.DetectedFileAttributes);
        Assert.False(r1.HasLargeFiles);
        Assert.Equal(10u, r1.DetectedHighPackSize);
        Assert.Equal(20u, r1.DetectedHighUnpSize);

        // Set 2 — distinct/inverted values, proving no cross-set bleed.
        Assert.Equal("DVD2", r2.Directory);
        Assert.Equal(["DVD2\\aln-re4b.rar", "DVD2\\aln-re4b.r00"], r2.VolumeNames);
        Assert.Equal("CCCCCCCC", r2.ArchivedFileCrcs["DVD2/aln-re4b.iso"]);
        Assert.Equal(0, r2.CompressionMethod);
        Assert.Equal(65536, r2.DictionarySize);
        Assert.Equal(50, r2.RARVersion);
        Assert.False(r2.IsSolid);
        Assert.False(r2.HasRecoveryRecord);
        Assert.Equal((byte)3, r2.DetectedHostOS);
        Assert.Equal(0x21u, r2.DetectedFileAttributes);
        Assert.True(r2.HasLargeFiles);
        Assert.Equal(30u, r2.DetectedHighPackSize);
        Assert.Equal(40u, r2.DetectedHighUnpSize);

        // Case-insensitive comparers preserved on every restored per-set collection.
        Assert.True(r1.ArchivedFileCrcs.TryGetValue("dvd1/ALN-RE4A.iso", out string? crc));
        Assert.Equal("AAAAAAAA", crc);
        Assert.True(r1.ArchivedFileTimestamps.ContainsKey("DVD1/ALN-RE4A.ISO"));
        Assert.Contains("dvd1/subs", r1.ArchivedDirectories);
        Assert.True(r1.ArchivedDirectoryTimestamps.ContainsKey("dvd1/SUBS"));
    }

    /// <summary>
    /// <c>hasState</c> must be true when ONLY <c>ArchiveSets</c> is populated — none of the older
    /// scalar/flat fields carry data.
    /// </summary>
    [Fact]
    public void Capture_HasStateTrue_WhenOnlyArchiveSetsPopulated()
    {
        var state = new ReconstructionImportState
        {
            ArchiveSets = [new SRRArchiveSet { Key = "K", Directory = "D" }],
        };

        ImportedSRRState? dto = ImportedSRRStateMapper.Capture(state, customPackerWarning: null);

        Assert.NotNull(dto);
        Assert.Equal(ImportedSRRState.CurrentSchemaVersion, dto.SchemaVersion);
        Assert.Single(dto.ArchiveSets);
    }

    /// <summary>
    /// A DTO whose <c>SchemaVersion</c> marks it complete must be restored AS-IS even when a set's
    /// directories are empty and its metadata is null — those are legitimate captured values, not
    /// evidence of an incomplete DTO. In particular it must NOT fall back to re-parsing the SRR at
    /// <c>SRRFilePath</c> (which would yield a completely different, 2-set result here).
    /// </summary>
    [Fact]
    public void Apply_CompleteSchemaVersion_EmptyDirsAndNullMetadata_RestoresAsIs_NoReparse()
    {
        Assert.True(File.Exists(TwoSetSrrPath), $"Fixture not found: {TwoSetSrrPath}");

        var dto = new ImportedSRRState
        {
            SchemaVersion = ImportedSRRState.CurrentSchemaVersion,
            SRRFilePath = TwoSetSrrPath, // present but must be ignored — the DTO is complete
            ArchiveSets =
            [
                new ArchiveSetDto { Key = "S1", Directory = "", VolumeNames = ["a.rar"] },
            ],
        };

        ReconstructionImportState state = ImportedSRRStateMapper.Apply(dto);

        SRRArchiveSet restored = Assert.Single(state.ArchiveSets);
        Assert.Equal("S1", restored.Key);
        Assert.Empty(restored.ArchivedDirectories);
        Assert.Null(restored.CompressionMethod);
        Assert.Null(restored.DictionarySize);
        Assert.Null(restored.RARVersion);
        Assert.Null(restored.IsSolid);
        Assert.Null(restored.HasRecoveryRecord);
    }

    /// <summary>
    /// A legacy DTO (no set list, older/absent <c>SchemaVersion</c>) must leave the restored
    /// <c>ArchiveSets</c> empty rather than fabricate data — but the existing runtime fallback
    /// (<see cref="ArchiveSetPlanner.ResolveSets"/>, called before each run) still re-derives the
    /// real sets from <c>SRRFilePath</c>, so a legacy config is not left unable to reconstruct.
    /// </summary>
    [Fact]
    public void Apply_LegacyDtoWithSrrFilePath_LeavesArchiveSetsEmpty_ButResolveSetsStillReparses()
    {
        Assert.True(File.Exists(TwoSetSrrPath), $"Fixture not found: {TwoSetSrrPath}");

        var dto = new ImportedSRRState { SRRFilePath = TwoSetSrrPath }; // SchemaVersion defaults to 0

        ReconstructionImportState state = ImportedSRRStateMapper.Apply(dto);

        Assert.Empty(state.ArchiveSets);
        Assert.Equal(TwoSetSrrPath, state.SRRFilePath);

        IReadOnlyList<SRRArchiveSet> resolved = ArchiveSetPlanner.ResolveSets(
            state.ArchiveSets, state.SRRFilePath, flatOriginalNames: [], flatArchiveFiles: []);
        Assert.Equal(2, resolved.Count);
    }
}
