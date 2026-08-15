using ReScene.App.Core.ViewModels.Reconstruction;
using ReScene.Core;
using ReScene.Core.Cryptography;
using ReScene.Core.Diagnostics;
using ReScene.SRR;

namespace ReScene.App.Core.Tests;

public class ArchiveSetPlannerTests
{
    // SRRArchiveSet.ArchivedFilesInOrder is populated only by the SRR parser — its backing list is
    // internal, deliberately not settable from outside the parser. Reflecting into it here lets
    // planner tests pin a specific archive order without a real SRR parse, mirroring the parser's
    // own dedupe-on-HashSet-add rule (a file is added to the order list only the first time its
    // name is newly added to ArchivedFiles).
    private static readonly System.Reflection.PropertyInfo ArchivedFilesInOrderProperty =
        typeof(SRRArchiveSet).GetProperty("_archivedFilesInOrder",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;

    private static SRRArchiveSet MakeSet(string key, string dir, string[] volumes, (string file, string crc)[] content)
    {
        var set = new SRRArchiveSet { Key = key, Directory = dir };
        foreach (string v in volumes)
        {
            set.VolumeNames.Add(v);
        }

        var orderedFiles = (List<string>)ArchivedFilesInOrderProperty.GetValue(set)!;
        foreach ((string file, string crc) in content)
        {
            if (set.ArchivedFiles.Add(file))
            {
                orderedFiles.Add(file);
            }

            set.ArchivedFileCrcs[file] = crc;
        }

        return set;
    }

    [Fact]
    public void BuildExpectedVolumeCrcs_FromUserSnapshot_FilteredToSetVolumes_QualifiedKeys()
    {
        // #9: the result is keyed by each volume's OWN canonical dir-qualified key (not the bare
        // basename the flat SFV happens to use), so same-basename volumes in another set never collide.
        SRRArchiveSet set = MakeSet("DVD1/aln-re4a", "DVD1",
            ["DVD1\\aln-re4a.rar", "DVD1\\aln-re4a.r00"], [("aln-re4a.iso", "00000000")]);
        var snapshot = new VerificationSnapshot(HashType.CRC32,
        [
            ("aln-re4a.rar", "f1a3ec0d"),
            ("aln-re4a.r00", "88b361c9"),
            ("aln-re4b.rar", "631d681c"), // other set — excluded
        ]);

        Dictionary<string, string> crcs = ArchiveSetPlanner.BuildExpectedVolumeCrcs(set, embeddedSfvBytes: null, snapshot);

        Assert.Equal(2, crcs.Count);
        Assert.Equal("f1a3ec0d", crcs["DVD1/aln-re4a.rar"]);
        Assert.False(crcs.ContainsKey("aln-re4b.rar"));
    }

    [Fact]
    public void BuildExpectedVolumeCrcs_PrefersEmbeddedSfvOverUserSnapshot()
    {
        SRRArchiveSet set = MakeSet("DVD1/aln-re4a", "DVD1",
            ["DVD1\\aln-re4a.rar", "DVD1\\aln-re4a.r00"], [("aln-re4a.iso", "00000000")]);

        byte[] embedded = System.Text.Encoding.Latin1.GetBytes(
            "aln-re4a.rar aaaaaaaa\r\naln-re4a.r00 bbbbbbbb\r\n");

        var snapshot = new VerificationSnapshot(HashType.CRC32,
        [
            ("aln-re4a.rar", "f1a3ec0d"),
            ("aln-re4a.r00", "88b361c9"),
        ]);

        Dictionary<string, string> crcs = ArchiveSetPlanner.BuildExpectedVolumeCrcs(set, embedded, snapshot);

        Assert.Equal(2, crcs.Count);
        Assert.Equal("aaaaaaaa", crcs["DVD1/aln-re4a.rar"]);
        Assert.Equal("bbbbbbbb", crcs["DVD1/aln-re4a.r00"]);
    }

    [Fact]
    public void BuildExpectedVolumeCrcs_UserSnapshotFillsGap_EmbeddedSfvOmitsAVolume()
    {
        // Embedded SFV covers only one volume; the user snapshot fills the other, and the embedded
        // entry still wins where both cover the same volume.
        SRRArchiveSet set = MakeSet("DVD1/aln-re4a", "DVD1",
            ["DVD1\\aln-re4a.rar", "DVD1\\aln-re4a.r00"], [("aln-re4a.iso", "00000000")]);

        byte[] embedded = System.Text.Encoding.Latin1.GetBytes("aln-re4a.rar aaaaaaaa\r\n"); // omits .r00

        var snapshot = new VerificationSnapshot(HashType.CRC32,
        [
            ("aln-re4a.rar", "f1a3ec0d"),
            ("aln-re4a.r00", "88b361c9"),
        ]);

        Dictionary<string, string> crcs = ArchiveSetPlanner.BuildExpectedVolumeCrcs(set, embedded, snapshot);

        Assert.Equal(2, crcs.Count);
        Assert.Equal("aaaaaaaa", crcs["DVD1/aln-re4a.rar"]);   // embedded wins
        Assert.Equal("88b361c9", crcs["DVD1/aln-re4a.r00"]);   // user snapshot fills the gap
    }

    [Fact]
    public void BuildExpectedVolumeCrcs_FlatSfv_OneCanonicalKeyPerVolume_NoDoubleCount()
    {
        // #9: the flat-SFV case (snapshot entries are bare basenames, set volumes are dir-qualified)
        // must resolve every volume to EXACTLY one canonical key — never both a qualified alias and a
        // basename alias for the same volume.
        SRRArchiveSet set = MakeSet("DVD1/aln-re4a", "DVD1",
            ["DVD1\\aln-re4a.rar", "DVD1\\aln-re4a.r00", "DVD1\\aln-re4a.r01"],
            [("aln-re4a.iso", "00000000")]);
        var snapshot = new VerificationSnapshot(HashType.CRC32,
        [
            ("aln-re4a.rar", "f1a3ec0d"),
            ("aln-re4a.r00", "88b361c9"),
            ("aln-re4a.r01", "12345678"),
        ]);

        Dictionary<string, string> crcs = ArchiveSetPlanner.BuildExpectedVolumeCrcs(set, embeddedSfvBytes: null, snapshot);

        Assert.Equal(set.VolumeNames.Count, crcs.Count);
    }

    // ── Basename matching (#10): consumes VerificationSnapshot.HashesForVolumes/LastSegment (T5) ──

    [Theory]
    [InlineData("DVD1\\x.rar")]
    [InlineData("DVD1/x.rar")]
    public void BuildExpectedVolumeCrcs_BasenameFallback_MatchesEitherSeparator(string volumeName)
    {
        // #10: LastSegment splits on both '\' and '/' (unlike Path.GetFileName, which is
        // platform-separator-only), so a bare basename entry in the verification file matches a
        // set volume regardless of which separator the SRR captured it with.
        SRRArchiveSet set = MakeSet("DVD1/x", "DVD1", [volumeName], []);
        var snapshot = new VerificationSnapshot(HashType.CRC32, [("x.rar", "aaaaaaaa")]);

        Dictionary<string, string> crcs = ArchiveSetPlanner.BuildExpectedVolumeCrcs(set, embeddedSfvBytes: null, snapshot);

        Assert.Equal("aaaaaaaa", crcs["DVD1/x.rar"]);
    }

    [Fact]
    public void BuildExpectedVolumeCrcs_SameBasenameDifferentSets_NoCrossSetAliasing()
    {
        // #10: CD1\x.rar and CD2\x.rar share a basename but must resolve to their OWN set's CRC —
        // never each other's. Both snapshot entries are dir-qualified, so the qualified-key match
        // wins outright (no ambiguous-basename fallback even needed here).
        SRRArchiveSet setCd1 = MakeSet("CD1/x", "CD1", ["CD1\\x.rar"], []);
        SRRArchiveSet setCd2 = MakeSet("CD2/x", "CD2", ["CD2\\x.rar"], []);
        var snapshot = new VerificationSnapshot(HashType.CRC32,
        [
            ("CD1/x.rar", "aaaaaaaa"),
            ("CD2/x.rar", "bbbbbbbb"),
        ]);

        Dictionary<string, string> crcsCd1 = ArchiveSetPlanner.BuildExpectedVolumeCrcs(setCd1, embeddedSfvBytes: null, snapshot);
        Dictionary<string, string> crcsCd2 = ArchiveSetPlanner.BuildExpectedVolumeCrcs(setCd2, embeddedSfvBytes: null, snapshot);

        Assert.Equal("aaaaaaaa", crcsCd1["CD1/x.rar"]);
        Assert.Equal("bbbbbbbb", crcsCd2["CD2/x.rar"]);
    }

    [Fact]
    public void BuildOptionsForSet_UsesOnlyThisSetsContentAndNames()
    {
        SRRArchiveSet set = MakeSet("DVD1/aln-re4a", "DVD1",
            ["DVD1\\aln-re4a.rar", "DVD1\\aln-re4a.r00"], [("aln-re4a.iso", "00000000")]);
        SharedReconstructionSettings shared = ArchiveSetPlannerTestData.SharedSettings();

        BruteForceOptions opts = ArchiveSetPlanner.BuildOptionsForSet(set, shared, expectedVolumeCrcs:
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["aln-re4a.rar"] = "f1a3ec0d" });

        Assert.Contains("aln-re4a.iso", opts.RAROptions.ArchiveFilePaths);
        Assert.DoesNotContain("aln-re4b.iso", opts.RAROptions.ArchiveFilePaths);
        Assert.Equal(["DVD1\\aln-re4a.rar", "DVD1\\aln-re4a.r00"], opts.RAROptions.OriginalRARFileNames);
        Assert.True(opts.ExpectedVolumeCrcs.ContainsKey("aln-re4a.rar"));
        Assert.Contains("f1a3ec0d", opts.Hashes);
    }

    [Fact]
    public void BuildOptionsForSet_CopiesArchivedFilesInOrder_PreservingSrrOrder()
    {
        // The SRR's own archive order (non-alphabetical here) must survive into RAROptions verbatim —
        // the engine needs it to drive rar with an explicit, ordered file list instead of its own
        // platform input mask.
        SRRArchiveSet set = MakeSet("DVD1/aln-re4a", "DVD1",
            ["DVD1\\aln-re4a.rar"], [("z.bin", "00000000"), ("a.cue", "11111111")]);
        SharedReconstructionSettings shared = ArchiveSetPlannerTestData.SharedSettings();

        BruteForceOptions opts = ArchiveSetPlanner.BuildOptionsForSet(set, shared,
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));

        Assert.Equal(["z.bin", "a.cue"], opts.RAROptions.OrderedArchiveFiles);
    }

    [Fact]
    public void BuildOptionsForSet_CarriesSharedReleaseWideFields()
    {
        SRRArchiveSet set = MakeSet("DVD1/aln-re4a", "DVD1",
            ["DVD1\\aln-re4a.rar"], [("aln-re4a.iso", "00000000")]);
        SharedReconstructionSettings shared = ArchiveSetPlannerTestData.SharedSettings() with
        {
            ArchiveComment = "hello",
            ArchiveCommentBytes = new byte[] { 1, 2, 3 },
            CmtCompressedData = new byte[] { 4, 5, 6 },
            CmtCompressionMethod = 0x30,
            CustomPackerDetected = CustomPackerType.None,
            SRRFilePath = "C:\\foo.srr",
            EnableHostOSPatching = true,
        };

        BruteForceOptions opts = ArchiveSetPlanner.BuildOptionsForSet(set, shared,
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));

        Assert.Equal("hello", opts.RAROptions.ArchiveComment);
        Assert.True(opts.RAROptions.ArchiveCommentBytes.HasValue);
        Assert.True(opts.RAROptions.CmtCompressedData.HasValue);
        Assert.True(new byte[] { 1, 2, 3 }.AsSpan().SequenceEqual(opts.RAROptions.ArchiveCommentBytes.Value.Span));
        Assert.True(new byte[] { 4, 5, 6 }.AsSpan().SequenceEqual(opts.RAROptions.CmtCompressedData.Value.Span));
        Assert.Equal((byte)0x30, opts.RAROptions.CmtCompressionMethod);
        Assert.Equal("C:\\foo.srr", opts.RAROptions.SRRFilePath);
        Assert.True(opts.RAROptions.EnableHostOSPatching);
    }

    [Fact]
    public void BuildOptionsForSet_UsesPerSetMetadata()
    {
        SRRArchiveSet set = MakeSet("DVD1/aln-re4a", "DVD1",
            ["DVD1\\aln-re4a.rar"], [("aln-re4a.iso", "00000000")]);
        set.DetectedHostOS = 3;
        set.DetectedFileAttributes = 0x20;
        set.HasLargeFiles = true;
        set.DetectedHighPackSize = 1;
        set.DetectedHighUnpSize = 2;

        SharedReconstructionSettings shared = ArchiveSetPlannerTestData.SharedSettings();

        BruteForceOptions opts = ArchiveSetPlanner.BuildOptionsForSet(set, shared,
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));

        Assert.Equal((byte)3, opts.RAROptions.DetectedFileHostOS);
        Assert.Equal((uint)0x20, opts.RAROptions.DetectedFileAttributes);
        Assert.Equal(true, opts.RAROptions.DetectedLargeFlag);
        Assert.Equal((uint)1, opts.RAROptions.DetectedHighPackSize);
        Assert.Equal((uint)2, opts.RAROptions.DetectedHighUnpSize);
    }

    // ── Per-set hash gate (#8) ──────────────────────────────────────────────────────────────────

    [Fact]
    public void BuildOptionsForSet_HashesGate_ScopedToThisSetsVolumes_ExcludesOtherSetsFirstVolumeCrc()
    {
        // #8: the cheap first-volume Hashes gate must be seeded from only THIS set's own volumes —
        // pouring every release verification hash into every set's gate would let a produced first
        // volume matching ANOTHER set's CRC be falsely accepted.
        SRRArchiveSet setB = MakeSet("DVD2/b", "DVD2", ["DVD2\\b.rar", "DVD2\\b.r00"], []);

        var snapshot = new VerificationSnapshot(HashType.CRC32,
        [
            ("DVD1/a.rar", "aaaaaaaa"), // set A's first volume — must NOT gate set B
            ("DVD1/a.r00", "bbbbbbbb"),
            ("DVD2/b.rar", "cccccccc"), // set B's own first volume — must still gate set B
            ("DVD2/b.r00", "dddddddd"),
        ]);

        SharedReconstructionSettings shared = ArchiveSetPlannerTestData.SharedSettings() with
        {
            Verification = snapshot,
            VerificationHashes = snapshot.AllHashes, // the old (buggy) all-sets source
        };

        BruteForceOptions opts = ArchiveSetPlanner.BuildOptionsForSet(setB, shared,
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));

        Assert.DoesNotContain("aaaaaaaa", opts.Hashes);
        Assert.DoesNotContain("bbbbbbbb", opts.Hashes);
        Assert.Contains("cccccccc", opts.Hashes);
    }

    [Fact]
    public void BuildOptionsForSet_Sha1Run_KeepsSeedingAllVerificationHashes_NoPerSetCrcFilterAvailable()
    {
        // #8 fallback: HashesForVolumes only resolves CRC32 entries — a SHA1 snapshot has no
        // per-set filter available, so the gate must keep seeding every SHA1 hash exactly as
        // before rather than being silently starved by the #8 fix.
        SRRArchiveSet set = MakeSet("DVD1/a", "DVD1", ["DVD1\\a.rar"], []);
        var snapshot = new VerificationSnapshot(HashType.SHA1,
        [
            ("movie.mkv", "0123456789abcdef0123456789abcdef01234567"),
        ]);

        SharedReconstructionSettings shared = ArchiveSetPlannerTestData.SharedSettings() with
        {
            HashType = HashType.SHA1,
            Verification = snapshot,
            VerificationHashes = snapshot.AllHashes,
        };

        BruteForceOptions opts = ArchiveSetPlanner.BuildOptionsForSet(set, shared,
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));

        Assert.Contains("0123456789abcdef0123456789abcdef01234567", opts.Hashes);
    }

    // ── Per-set directories + timestamps (#7) ──────────────────────────────────────────────────────

    [Fact]
    public void BuildOptionsForSet_KeyedSet_DirectoriesAndTimesComeFromTheSet_NotSharedUnion()
    {
        // #7: a keyed (non-flat) set must carry only ITS OWN archived directories/timestamps — the
        // release-wide union would leak another set's same-named subdirectory into this set's headers.
        SRRArchiveSet set = MakeSet("DVD1/aln-re4a", "DVD1", ["DVD1\\aln-re4a.rar"], []);
        set.ArchivedDirectories.Add("Subs");
        var modified = new DateTime(2020, 1, 1);
        var created = new DateTime(2020, 1, 2);
        var accessed = new DateTime(2020, 1, 3);
        set.ArchivedDirectoryTimestamps["Subs"] = modified;
        set.ArchivedDirectoryCreationTimes["Subs"] = created;
        set.ArchivedDirectoryAccessTimes["Subs"] = accessed;

        SharedReconstructionSettings shared = ArchiveSetPlannerTestData.SharedSettings() with
        {
            ArchiveDirectories = ["OtherSetsDir"],
            DirectoryTimestamps = new Dictionary<string, DateTime> { ["OtherSetsDir"] = DateTime.MinValue },
            DirectoryCreationTimes = new Dictionary<string, DateTime> { ["OtherSetsDir"] = DateTime.MinValue },
            DirectoryAccessTimes = new Dictionary<string, DateTime> { ["OtherSetsDir"] = DateTime.MinValue },
        };

        BruteForceOptions opts = ArchiveSetPlanner.BuildOptionsForSet(set, shared,
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));

        Assert.Contains("Subs", opts.RAROptions.ArchiveDirectoryPaths);
        Assert.DoesNotContain("OtherSetsDir", opts.RAROptions.ArchiveDirectoryPaths);
        Assert.Equal(modified, opts.RAROptions.DirectoryTimestamps["Subs"]);
        Assert.Equal(created, opts.RAROptions.DirectoryCreationTimes["Subs"]);
        Assert.Equal(accessed, opts.RAROptions.DirectoryAccessTimes["Subs"]);
    }

    [Fact]
    public void BuildOptionsForSet_FlatSet_KeepsSharedUnionForDirectoriesAndTimes()
    {
        // #7 fallback: the legacy flat single-set path (Key=="") has no per-set directory data of
        // its own (the synthesized flat SRRArchiveSet never populates it) — it keeps the shared
        // release-wide union.
        SRRArchiveSet set = MakeSet("", "", ["x.rar"], []);
        var modified = new DateTime(2021, 5, 1);

        SharedReconstructionSettings shared = ArchiveSetPlannerTestData.SharedSettings() with
        {
            ArchiveDirectories = ["Subs"],
            DirectoryTimestamps = new Dictionary<string, DateTime> { ["Subs"] = modified },
        };

        BruteForceOptions opts = ArchiveSetPlanner.BuildOptionsForSet(set, shared,
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));

        Assert.Contains("Subs", opts.RAROptions.ArchiveDirectoryPaths);
        Assert.Equal(modified, opts.RAROptions.DirectoryTimestamps["Subs"]);
    }

    // ── Per-set matrix (#6): metadata replaces switch groups, field by field ───────────────────────

    [Fact]
    public void BuildOptionsForSet_Rar4NativeCompressionAndSolidDash_NoMa()
    {
        // A: {unpack 29, m0, s-} — RAR4 (unpack < 50), native on a <=499 exe, no -ma.
        SRRArchiveSet set = MakeSet("DVD1/a", "DVD1", ["DVD1\\a.rar"], []);
        set.CompressionMethod = 0;
        set.IsSolid = false;
        set.RARVersion = 29;

        SharedReconstructionSettings shared = ArchiveSetPlannerTestData.SharedSettings() with
        {
            RARVersions = [new VersionRange(200, 800)],
            InstalledVersions = [new InstalledRARVersion(390, "winrar-390", "p390")],
        };

        BruteForceOptions opts = ArchiveSetPlanner.BuildOptionsForSet(set, shared,
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));

        RARCommandLineArgument[] row = Assert.Single(opts.RAROptions.CommandLineArguments);
        Assert.Contains(row, a => a.Argument == "-m0");
        Assert.Contains(row, a => a.Argument == "-s-");
        Assert.DoesNotContain(row, a => a.Argument.StartsWith("-ma", StringComparison.Ordinal));

        VersionRange range = Assert.Single(opts.RAROptions.RARVersions);
        Assert.Equal((390, 391), (range.Start, range.End));
        Assert.Equal(["winrar-390"], opts.RAROptions.AllowedVersionFolders);
    }

    [Fact]
    public void BuildOptionsForSet_Rar5CompressionAndSolid_AddsVersionBoundedMa5()
    {
        // B: {unpack 50, m5, s} — RAR5; -ma5 is REQUIRED (500-699 cannot natively make RAR5) and
        // version-bounded (Min=500,Max=699), matching RARCommandLineBuilder.cs's own -ma5 argument.
        SRRArchiveSet set = MakeSet("DVD1/b", "DVD1", ["DVD1\\b.rar"], []);
        set.CompressionMethod = 0x35; // RAR5-encoded ASCII digit for method 5
        set.IsSolid = true;
        set.RARVersion = 50;

        SharedReconstructionSettings shared = ArchiveSetPlannerTestData.SharedSettings() with
        {
            RARVersions = [new VersionRange(200, 800)],
            InstalledVersions =
            [
                new InstalledRARVersion(390, "winrar-390", "p390"),
                new InstalledRARVersion(560, "winrar-560", "p560"),
            ],
        };

        BruteForceOptions opts = ArchiveSetPlanner.BuildOptionsForSet(set, shared,
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));

        RARCommandLineArgument[] row = Assert.Single(opts.RAROptions.CommandLineArguments);
        Assert.Contains(row, a => a.Argument == "-m5");
        Assert.Contains(row, a => a.Argument == "-s");
        RARCommandLineArgument ma5 = Assert.Single(row, a => a.Argument == "-ma5");
        Assert.Equal(500, ma5.MinimumVersion);
        Assert.Equal(699, ma5.MaximumVersion);

        VersionRange range = Assert.Single(opts.RAROptions.RARVersions);
        Assert.Equal((560, 561), (range.Start, range.End)); // 390 excluded — not RAR5-capable
        Assert.Equal(["winrar-560"], opts.RAROptions.AllowedVersionFolders);
    }

    [Fact]
    public void BuildOptionsForSet_Rar5FormatButOnly700ExeSelected_FailsHonestly()
    {
        // 7.x is RAR7-native and cannot be coerced down to RAR5 (-ma5 is bounded 500-699) — the set
        // must fail rather than silently fall back to the global matrix.
        SRRArchiveSet set = MakeSet("DVD1/b", "DVD1", ["DVD1\\b.rar"], []);
        set.RARVersion = 50;

        SharedReconstructionSettings shared = ArchiveSetPlannerTestData.SharedSettings() with
        {
            RARVersions = [new VersionRange(200, 800)],
            InstalledVersions = [new InstalledRARVersion(700, "winrar-700", "p700")],
        };

        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() =>
            ArchiveSetPlanner.BuildOptionsForSet(set, shared, new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)));
        Assert.Contains("no selected WinRAR version can produce RAR5", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildOptionsForSet_Rar5FormatButOnly390ExeSelected_EmptyIntersectionFailsHonestly()
    {
        // A 3.90-only selection is RAR4-only — no exe in the surviving selection can make RAR5 either.
        SRRArchiveSet set = MakeSet("DVD1/b", "DVD1", ["DVD1\\b.rar"], []);
        set.RARVersion = 55;

        SharedReconstructionSettings shared = ArchiveSetPlannerTestData.SharedSettings() with
        {
            RARVersions = [new VersionRange(200, 800)],
            InstalledVersions = [new InstalledRARVersion(390, "winrar-390", "p390")],
        };

        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() =>
            ArchiveSetPlanner.BuildOptionsForSet(set, shared, new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)));
        Assert.Contains("no selected WinRAR version can produce RAR5", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildOptionsForSet_FormatKnownOnly_CompressionDictionarySolidSurviveFromSnapshot()
    {
        // RARVersion present but compression/dictionary/solid all null: ONLY the format/version group
        // is replaced — the snapshot's own -m3/-md64k/-s toggles (baked into shared.Switches) survive.
        SRRArchiveSet set = MakeSet("DVD1/b", "DVD1", ["DVD1\\b.rar"], []);
        set.RARVersion = 50;

        SharedReconstructionSettings shared = ArchiveSetPlannerTestData.SharedSettings() with
        {
            RARVersions = [new VersionRange(200, 800)],
            InstalledVersions = [new InstalledRARVersion(560, "winrar-560", "p560")],
            Switches = new RARSwitchSettings { SwitchM3 = true, SwitchMD64K = true, SwitchS = true },
        };

        BruteForceOptions opts = ArchiveSetPlanner.BuildOptionsForSet(set, shared,
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));

        RARCommandLineArgument[] row = Assert.Single(opts.RAROptions.CommandLineArguments);
        Assert.Contains(row, a => a.Argument == "-m3");     // survives from the snapshot
        Assert.Contains(row, a => a.Argument == "-md64k");  // survives from the snapshot
        Assert.Contains(row, a => a.Argument == "-s");      // survives from the snapshot
        Assert.Contains(row, a => a.Argument == "-ma5");    // added — the only group replaced

        VersionRange range = Assert.Single(opts.RAROptions.RARVersions);
        Assert.Equal((560, 561), (range.Start, range.End));
    }

    [Fact]
    public void BuildOptionsForSet_NoRelevantMetadata_FallsBackToGlobalMatrix_EvenWithNonEmptyKey()
    {
        // No compression/dictionary/solid/RARVersion at all: falls back to the global matrix
        // regardless of whether set.Key is empty — this set's Key is non-empty.
        SRRArchiveSet set = MakeSet("DVD1/a", "DVD1", ["DVD1\\a.rar"], []);

        SharedReconstructionSettings shared = ArchiveSetPlannerTestData.SharedSettings() with
        {
            RARVersions = [new VersionRange(300, 400)],
            CommandLineArguments = [[new RARCommandLineArgument("a", 200), new RARCommandLineArgument("-m3", 200)]],
            SelectedVersionFolders = ["winrar-390"],
        };

        BruteForceOptions opts = ArchiveSetPlanner.BuildOptionsForSet(set, shared,
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));

        Assert.Same(shared.CommandLineArguments, opts.RAROptions.CommandLineArguments);
        Assert.Same(shared.RARVersions, opts.RAROptions.RARVersions);
        Assert.Same(shared.SelectedVersionFolders, opts.RAROptions.AllowedVersionFolders);
    }

    [Fact]
    public void WorkRootFor_SingleRootSet_IsOutputPath()
    {
        SRRArchiveSet set = MakeSet("", "", ["x.rar"], [("x.iso", "00000000")]);
        SharedReconstructionSettings shared = ArchiveSetPlannerTestData.SharedSettings() with { OutputPath = "C:\\out" };

        Assert.Equal("C:\\out", ArchiveSetPlanner.WorkRootFor(shared, set));
    }

    [Fact]
    public void WorkRootFor_KeyedSet_IsIsolatedSubdir()
    {
        SRRArchiveSet set = MakeSet("DVD1/aln-re4a", "DVD1", ["DVD1\\aln-re4a.rar"], [("aln-re4a.iso", "00000000")]);
        // A keyed set's work root is real-resolved against the filesystem, so the output path has to
        // be a genuine absolute path for this OS — a bare "C:\out" is a relative name off Windows and
        // would resolve under the test's working directory instead.
        string outputPath = Path.Combine(Path.GetTempPath(), "rescene_planner_out");
        SharedReconstructionSettings shared = ArchiveSetPlannerTestData.SharedSettings() with { OutputPath = outputPath };

        string root = ArchiveSetPlanner.WorkRootFor(shared, set);

        // The expectation must be canonicalized the same way the planner canonicalizes: macOS's
        // GetTempPath hands out /var/... which real-resolves to /private/var/....
        Directory.CreateDirectory(outputPath);
        Assert.StartsWith(
            Path.Combine(ReconstructionPathGuard.ResolveReal(outputPath), ".rescene-work"),
            root, StringComparison.Ordinal);
        Assert.DoesNotContain('/', Path.GetFileName(root)); // key separators sanitized
    }

    [Fact]
    public void NarrowToCombo_RestrictsVersionsAndArgsToWinner()
    {
        BruteForceOptions full = ArchiveSetPlannerTestData.SampleOptions();
        var combo = new WinningCombo(351, [new RARCommandLineArgument("-m0", 300)]);

        BruteForceOptions narrowed = ArchiveSetPlanner.NarrowToCombo(full, combo);

        Assert.Single(narrowed.RAROptions.RARVersions);
        // End is exclusive, so the single-version range must be [351, 352) and actually accept 351.
        Assert.Equal(351, narrowed.RAROptions.RARVersions[0].Start);
        Assert.Equal(352, narrowed.RAROptions.RARVersions[0].End);
        Assert.True(narrowed.RAROptions.RARVersions[0].InRange(351));
        Assert.Single(narrowed.RAROptions.CommandLineArguments);
        Assert.Equal("-m0", narrowed.RAROptions.CommandLineArguments[0][0].Argument);
    }

    [Fact]
    public void NarrowToCombo_PreservesHashesAndExpectedCrcs()
    {
        BruteForceOptions full = ArchiveSetPlannerTestData.SampleOptions();
        full.Hashes.Add("deadbeef");
        full.ExpectedVolumeCrcs["x.rar"] = "deadbeef";
        var combo = new WinningCombo(351, [new RARCommandLineArgument("-m0", 300)]);

        BruteForceOptions narrowed = ArchiveSetPlanner.NarrowToCombo(full, combo);

        Assert.Contains("deadbeef", narrowed.Hashes);
        Assert.Equal("deadbeef", narrowed.ExpectedVolumeCrcs["x.rar"]);
    }

    [Fact]
    public void NarrowToCombo_PreservesOrderedArchiveFiles()
    {
        // Seeded re-runs (NarrowToCombo) must keep the SRR-guided order too — losing it here would
        // silently fall back to rar's own input mask for the seed attempt only.
        var full = new BruteForceOptions("C:\\rar", "C:\\release", "C:\\out")
        {
            RAROptions = new RAROptions { OrderedArchiveFiles = ["z.bin", "a.cue"] },
        };
        var combo = new WinningCombo(351, [new RARCommandLineArgument("-m0", 300)]);

        BruteForceOptions narrowed = ArchiveSetPlanner.NarrowToCombo(full, combo);

        Assert.Equal(["z.bin", "a.cue"], narrowed.RAROptions.OrderedArchiveFiles);
    }

    [Fact]
    public void ResolveSets_PrefersParsedArchiveSets()
    {
        SRRArchiveSet existing = MakeSet("DVD1/x", "DVD1", ["DVD1\\x.rar"], [("x.iso", "00000000")]);

        IReadOnlyList<SRRArchiveSet> sets = ArchiveSetPlanner.ResolveSets(
            archiveSets: [existing], srrFilePath: null,
            flatOriginalNames: ["ignored.rar"], flatArchiveFiles: ["ignored.iso"]);

        Assert.Single(sets);
        Assert.Same(existing, sets[0]);
    }

    [Fact]
    public void RealMultiSetSRR_ProducesIsolatedPerSetOptions()
    {
        string srrPath = Path.Combine(AppContext.BaseDirectory, "TestData",
            "cleanup_script",
            "007.A.View.To.A.Kill.1985.UE.iNTERNAL.DVDRip.XviD-iNCiTE.fine_2cd.srr");
        Assert.True(File.Exists(srrPath), $"Fixture not found: {srrPath}");

        var srr = SRRFile.Load(srrPath);
        Assert.Equal(2, srr.ArchiveSets.Count);

        SharedReconstructionSettings shared = ArchiveSetPlannerTestData.SharedSettings();

        var allArchiveFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (SRRArchiveSet set in srr.ArchiveSets)
        {
            BruteForceOptions opts = ArchiveSetPlanner.BuildOptionsForSet(set, shared,
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));

            // Each set's options carry only that set's own volume names and archived content.
            Assert.Equal(set.VolumeNames, opts.RAROptions.OriginalRARFileNames);
            Assert.Equal(set.ArchivedFiles.Count, opts.RAROptions.ArchiveFilePaths.Count);
            Assert.Equal(set.ArchivedFilesInOrder, opts.RAROptions.OrderedArchiveFiles);

            foreach (string f in opts.RAROptions.ArchiveFilePaths)
            {
                allArchiveFiles.Add(f);
            }
        }

        // The two sets together do not double-count any single archived file beyond their own.
        Assert.True(allArchiveFiles.Count > 0);
    }

    [Fact]
    public void ResolveSets_NoArchiveSets_NoSRR_SynthesizesSingleFlatSet()
    {
        IReadOnlyList<SRRArchiveSet> sets = ArchiveSetPlanner.ResolveSets(archiveSets: [], srrFilePath: null,
            flatOriginalNames: ["x.rar", "x.r00"], flatArchiveFiles: ["x.iso"]);
        Assert.Single(sets);
        Assert.Equal("", sets[0].Directory);
        Assert.Equal(["x.rar", "x.r00"], sets[0].VolumeNames);
        Assert.Contains("x.iso", sets[0].ArchivedFiles);
    }

    // ── ShouldSkipUnverifiableSet ────────────────────────────────────────────────────────────────

    [Fact]
    public void ShouldSkipUnverifiableSet_Sha1_CompleteAllVolumes_ZeroExpected_ReturnsFalse()
    {
        // SHA1 run: no per-volume CRC source; engine must still run via the first-volume hash gate.
        // Regression case — the old guard (expected.Count < volumeCount) would have skipped this.
        Assert.False(ArchiveSetPlanner.ShouldSkipUnverifiableSet(
            completeAllVolumes: true, hashType: HashType.SHA1, expectedCrcCount: 0, volumeCount: 30));
    }

    [Fact]
    public void ShouldSkipUnverifiableSet_Crc32_CompleteAllVolumes_ZeroExpected_ReturnsFalse()
    {
        // CRC32 run but no expected CRC matched any set volume — no SFV coverage at all.
        // Engine still runs; first-volume gate handles it.
        Assert.False(ArchiveSetPlanner.ShouldSkipUnverifiableSet(
            completeAllVolumes: true, hashType: HashType.CRC32, expectedCrcCount: 0, volumeCount: 30));
    }

    [Fact]
    public void ShouldSkipUnverifiableSet_Crc32_CompleteAllVolumes_PartialExpected_ReturnsTrue()
    {
        // CRC32 + some volumes covered but not all: partial coverage is an honest skip.
        Assert.True(ArchiveSetPlanner.ShouldSkipUnverifiableSet(
            completeAllVolumes: true, hashType: HashType.CRC32, expectedCrcCount: 15, volumeCount: 30));
    }

    [Fact]
    public void ShouldSkipUnverifiableSet_Crc32_CompleteAllVolumes_FullExpected_ReturnsFalse()
    {
        // Full coverage: all volumes have a CRC — verify, don't skip.
        Assert.False(ArchiveSetPlanner.ShouldSkipUnverifiableSet(
            completeAllVolumes: true, hashType: HashType.CRC32, expectedCrcCount: 30, volumeCount: 30));
    }

    [Fact]
    public void ShouldSkipUnverifiableSet_Crc32_NotCompleteAllVolumes_ReturnsFalse()
    {
        // CompleteAllVolumes is off: skip guard should never fire regardless of CRC coverage.
        Assert.False(ArchiveSetPlanner.ShouldSkipUnverifiableSet(
            completeAllVolumes: false, hashType: HashType.CRC32, expectedCrcCount: 0, volumeCount: 30));
    }
}
