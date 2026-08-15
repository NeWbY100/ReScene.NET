using ReScene.App.Core.Services;
using ReScene.Core.IO;
using ReScene.RAR;

namespace ReScene.App.Core.Tests;

/// <summary>
/// Test matrix for the main-set decision tree (pyrescene-rules-excerpt.txt,
/// <c>remove_unwanted_sfvs</c>; see docs/superpowers/plans/2026-07-19-multiset-srr-creation.md),
/// one Fact per row. Proof-related rows drive the injectable <c>proofRarReader</c> seam with fact
/// literals only — <c>RarProofInspectorTests</c> (ReScene.Tests) proves the production
/// <see cref="RarProofInspector"/> against real fixture bytes.
/// </summary>
public class ReleaseScannerMainSetTests : TempDirTestBase
{
    private string CreateRoot(string releaseName)
    {
        string root = Path.Combine(TempDir, releaseName);
        Directory.CreateDirectory(root);
        return root;
    }

    private static string WriteSfv(string path, params string[] entries)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllLines(path, entries.Select(e => $"{e} 00000000"));
        return path;
    }

    private static string Touch(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "x");
        return path;
    }

    [Fact]
    public void TwoCdSfvs_BothBecomeMainSets_InTraversalOrder()
    {
        string root = CreateRoot("Some.Release-GRP");
        string aSfv = WriteSfv(Path.Combine(root, "CD1", "a.sfv"), "a.rar");
        string bSfv = WriteSfv(Path.Combine(root, "CD2", "b.sfv"), "b.rar");

        ReleaseScanResult result = new ReleaseScanner().Scan(root);

        Assert.Equal(2, result.MainSets.Count);
        Assert.Equal("CD1/a.sfv", result.MainSets[0].RelativeName);
        Assert.Equal("CD2/b.sfv", result.MainSets[1].RelativeName);
        Assert.Empty(result.SubtitleSfvs);
        // Every input SFV is unconditionally appended to StoredFiles (generate_srr,
        // `for sfv in sfvs`).
        Assert.Equal([aSfv, bSfv], result.StoredFiles);
    }

    [Fact]
    public void VobsubName_ReleaseLacksCarveOut_Excluded()
    {
        // remove_unwanted_sfvs rule 1
        string root = CreateRoot("Some.Movie-GRP");
        string sfv = WriteSfv(Path.Combine(root, "x.vobsubs.sfv"), "x.rar");

        ReleaseScanResult result = new ReleaseScanner().Scan(root);

        Assert.Empty(result.MainSets);
        Assert.Equal([sfv], result.SubtitleSfvs);
    }

    [Fact]
    public void VobsubName_ReleaseIsSubpack_BecomesMain_AndAlsoQueuedToSubs()
    {
        // rule 1's carve-out (release name contains "subpack") admits the SFV; the release-level
        // subpack/subfix tail then ALSO queues every main SFV to SubtitleSfvs.
        string root = CreateRoot("Some.SUBPACK-GRP");
        string sfv = WriteSfv(Path.Combine(root, "x.vobsubs.sfv"), "x.rar");

        ReleaseScanResult result = new ReleaseScanner().Scan(root);

        Assert.Single(result.MainSets);
        Assert.Equal(sfv, result.MainSets[0].SfvOrRarPath);
        Assert.Equal([sfv], result.SubtitleSfvs);
    }

    [Fact]
    public void SubsName_NoCarveOut_Excluded()
    {
        // remove_unwanted_sfvs rule 2, no fall-through condition applies
        string root = CreateRoot("Some.Release-GRP");
        string sfv = WriteSfv(Path.Combine(root, "grp-subs.sfv"), "grp.rar");

        ReleaseScanResult result = new ReleaseScanner().Scan(root);

        Assert.Empty(result.MainSets);
        Assert.Equal([sfv], result.SubtitleSfvs);
    }

    [Fact]
    public void SubsName_MatchesFalsePositiveRegex_FallsThroughToMain()
    {
        // remove_unwanted_sfvs rule 2's `^000?-` alternative of the false-positive regex
        string root = CreateRoot("Some.Release-GRP");
        string sfv = WriteSfv(Path.Combine(root, "00-grp-subs.sfv"), "grp.rar");

        ReleaseScanResult result = new ReleaseScanner().Scan(root);

        Assert.Single(result.MainSets);
        Assert.Equal(sfv, result.MainSets[0].SfvOrRarPath);
    }

    [Fact]
    public void SubsName_FallsThrough_ThenRule3ExcludesByCoverDir_ProvesPassSemantics()
    {
        // The `pass` branch does NOT accept the SFV — it only continues the sequential rule walk,
        // and here rule 3 (exact "cover" parent dir) excludes it anyway.
        string root = CreateRoot("Some.Release-GRP");
        string sfv = WriteSfv(Path.Combine(root, "Cover", "grp.subs.cd1.sfv"), "grp.rar");

        ReleaseScanResult result = new ReleaseScanner().Scan(root);

        Assert.Empty(result.MainSets);
        Assert.Equal([sfv], result.SubtitleSfvs);
    }

    [Fact]
    public void ExactSubsDir_Excluded()
    {
        // remove_unwanted_sfvs rule 3
        string root = CreateRoot("Some.Release-GRP");
        string sfv = WriteSfv(Path.Combine(root, "Subs", "x.sfv"), "x.rar");

        ReleaseScanResult result = new ReleaseScanner().Scan(root);

        Assert.Empty(result.MainSets);
        Assert.Equal([sfv], result.SubtitleSfvs);
    }

    [Fact]
    public void Proof_SingleRarEntry_LastPackedIsImage_StoresSfvAndRar_NotMainSet()
    {
        // remove_unwanted_sfvs rule 4, image match
        string root = CreateRoot("Some.Release-GRP");
        string sfv = WriteSfv(Path.Combine(root, "Proof", "p.sfv"), "p.rar");
        string rar = Touch(Path.Combine(root, "Proof", "p.rar"));

        var facts = new ProofRarFacts(Readable: true, HasPackedBlocks: true, AnyImage: true, LastPackedIsImage: true);
        var scanner = new ReleaseScanner(sfvEntryReader: null, proofRarReader: (_, _) => facts);

        ReleaseScanResult result = scanner.Scan(root);

        Assert.Empty(result.MainSets);
        // Final order: generate_srr moves the proof `.rar` to sit immediately BEFORE its matching
        // `.sfv` (see ReleaseScannerStoredTests's equivalent test for the full rationale).
        Assert.Equal([rar, sfv], result.StoredFiles);
        Assert.Empty(result.SubtitleSfvs);
    }

    [Fact]
    public void Proof_LastPackedNotImage_EarlierWasImage_LastBlockWins_NotProof_ContinuesToLaterRules()
    {
        // remove_unwanted_sfvs: skip is reassigned on every block; the LAST packed block decides,
        // not the first.
        string root = CreateRoot("Some.Release-GRP");
        string sfv = WriteSfv(Path.Combine(root, "Proof", "p.sfv"), "p.rar");
        string rar = Touch(Path.Combine(root, "Proof", "p.rar"));

        var facts = new ProofRarFacts(Readable: true, HasPackedBlocks: true, AnyImage: true, LastPackedIsImage: false);
        var scanner = new ReleaseScanner(sfvEntryReader: null, proofRarReader: (_, _) => facts);

        ReleaseScanResult result = scanner.Scan(root);

        Assert.Single(result.MainSets);
        Assert.Equal(sfv, result.MainSets[0].SfvOrRarPath);
        // Rule 4 didn't treat p.rar as proof (last block isn't an image), but the INDEPENDENT
        // filter_proof_rar_files pass is unrelated to rule 4's classification — it only checks
        // "proof" in the path and ANY packed block being an image (AnyImage: true here), so it
        // stores p.rar on its own; generate_srr's SFV-append step then appends the (unrelated)
        // main sfv.
        Assert.Equal([rar, sfv], result.StoredFiles);
    }

    [Fact]
    public void Proof_Unreadable_WarnsAndExcludes_TreatedAsProof()
    {
        // remove_unwanted_sfvs: "No RAR5 support yet" / caught ValueError
        string root = CreateRoot("Some.Release-GRP");
        string sfv = WriteSfv(Path.Combine(root, "Proof", "p.sfv"), "p.rar");
        string rar = Touch(Path.Combine(root, "Proof", "p.rar"));

        var facts = new ProofRarFacts(Readable: false, HasPackedBlocks: false, AnyImage: false, LastPackedIsImage: false);
        var scanner = new ReleaseScanner(sfvEntryReader: null, proofRarReader: (_, _) => facts);

        ReleaseScanResult result = scanner.Scan(root);

        Assert.Empty(result.MainSets);
        Assert.Equal([sfv], result.StoredFiles);
        Assert.Contains(result.Warnings, w => w.Contains(rar, StringComparison.Ordinal));
    }

    [Fact]
    public void Proof_SingletonEntryNotLowercaseRarExtension_ExcludedAsProof_RarNeverChecked()
    {
        // remove_unwanted_sfvs: the naming check runs BEFORE any file-existence or content check,
        // so neither the filesystem nor the injected reader is ever touched.
        string root = CreateRoot("Some.Release-GRP");
        string sfv = WriteSfv(Path.Combine(root, "Proof", "p.sfv"), "p.RAR");

        var scanner = new ReleaseScanner(
            sfvEntryReader: null,
            proofRarReader: (_, _) => throw new InvalidOperationException("must not be called"));

        ReleaseScanResult result = scanner.Scan(root);

        Assert.Empty(result.MainSets);
        Assert.Equal([sfv], result.StoredFiles);
    }

    [Fact]
    public void Proof_RarMissingOnDisk_WarnsAndExcludes()
    {
        // remove_unwanted_sfvs rule 4: proof RAR missing on disk
        string root = CreateRoot("Some.Release-GRP");
        string sfv = WriteSfv(Path.Combine(root, "Proof", "p.sfv"), "p.rar");
        // p.rar deliberately never created.

        ReleaseScanResult result = new ReleaseScanner().Scan(root);

        Assert.Empty(result.MainSets);
        Assert.Equal([sfv], result.StoredFiles);
        Assert.Contains(result.Warnings, w => w.Contains("cannot be found", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Proof_NoPackedBlocks_NotProof_ContinuesToLaterRules()
    {
        string root = CreateRoot("Some.Release-GRP");
        string sfv = WriteSfv(Path.Combine(root, "Proof", "p.sfv"), "p.rar");
        Touch(Path.Combine(root, "Proof", "p.rar"));

        var facts = new ProofRarFacts(Readable: true, HasPackedBlocks: false, AnyImage: false, LastPackedIsImage: false);
        var scanner = new ReleaseScanner(sfvEntryReader: null, proofRarReader: (_, _) => facts);

        ReleaseScanResult result = scanner.Scan(root);

        Assert.Single(result.MainSets);
        Assert.Equal(sfv, result.MainSets[0].SfvOrRarPath);
        // sfv is a main SFV, so it's appended to StoredFiles.
        Assert.Equal([sfv], result.StoredFiles);
    }

    [Fact]
    public void Proof_TwoEntries_RequiresSingleton_FallsThroughToLaterRules()
    {
        // remove_unwanted_sfvs rule 4's `len(sfvfiles) == 1` gate.
        string root = CreateRoot("Some.Release-GRP");
        string sfv = WriteSfv(Path.Combine(root, "Proof", "p.sfv"), "p.rar", "p.r00");

        var scanner = new ReleaseScanner(
            sfvEntryReader: null,
            proofRarReader: (_, _) => throw new InvalidOperationException("must not be called"));

        ReleaseScanResult result = scanner.Scan(root);

        Assert.Single(result.MainSets);
        Assert.Equal(sfv, result.MainSets[0].SfvOrRarPath);
        // sfv is a main SFV, so it's appended to StoredFiles.
        Assert.Equal([sfv], result.StoredFiles);
    }

    [Fact]
    public void SubsCdDirectory_Excluded()
    {
        // remove_unwanted_sfvs rule 5
        string root = CreateRoot("Some.Release-GRP");
        string sfv = WriteSfv(Path.Combine(root, "Subs", "CD1", "s.sfv"), "s.rar");

        ReleaseScanResult result = new ReleaseScanner().Scan(root);

        Assert.Empty(result.MainSets);
        Assert.Equal([sfv], result.SubtitleSfvs);
    }

    [Fact]
    public void SubpackSubstringDir_ReleaseLacksSubpack_Excluded()
    {
        // remove_unwanted_sfvs rule 6a
        string root = CreateRoot("Movie-GRP");
        string sfv = WriteSfv(Path.Combine(root, "SubpackStuff", "x.sfv"), "x.rar");

        ReleaseScanResult result = new ReleaseScanner().Scan(root);

        Assert.Empty(result.MainSets);
        Assert.Equal([sfv], result.SubtitleSfvs);
    }

    [Fact]
    public void FixSubstringDir_ReleaseHasFix_MainSet()
    {
        // remove_unwanted_sfvs rule 6c exception: release name also has "fix"
        string root = CreateRoot("Movie.FIX-GRP");
        string sfv = WriteSfv(Path.Combine(root, "MyFix", "x.sfv"), "x.rar");

        ReleaseScanResult result = new ReleaseScanner().Scan(root);

        Assert.Single(result.MainSets);
        Assert.Equal(sfv, result.MainSets[0].SfvOrRarPath);
    }

    [Fact]
    public void Rescue_AllSubsNamed_TwoEntrySfvReadmittedAsMain_OtherStaysExcluded()
    {
        // remove_unwanted_sfvs: the rescue fallback re-examines every SFV found, not just the ones
        // the first pass excluded; the destination split recomputes SubtitleSfvs against the FINAL
        // (post-rescue) main set.
        string root = CreateRoot("Some.Release-GRP");
        string single = WriteSfv(Path.Combine(root, "a-subs.sfv"), "a.rar");
        string multi = WriteSfv(Path.Combine(root, "b-subs.sfv"), "b.rar", "b.r00");

        ReleaseScanResult result = new ReleaseScanner().Scan(root);

        Assert.Single(result.MainSets);
        Assert.Equal(multi, result.MainSets[0].SfvOrRarPath);
        Assert.Equal([single], result.SubtitleSfvs);
    }

    [Fact]
    public void DirfixSubdir_ExcludedSfv_SkippedEntirely_WithWarning()
    {
        // pyrescene: generate_srr's extra_sfvs loop, "not for dirfix releases moved to the main
        // folder" — `"dirfix" in subdir.lower()`, a substring check on the immediate parent dir.
        string root = CreateRoot("Some.Release-GRP");
        string sfv = WriteSfv(Path.Combine(root, "Subs", "dirfix.stuff", "x.sfv"), "x.rar");

        ReleaseScanResult result = new ReleaseScanner().Scan(root);

        Assert.Empty(result.MainSets);
        Assert.Empty(result.SubtitleSfvs);
        // The dirfix skip only affects SubtitleSfvs/nested-SRR routing — generate_srr's
        // unconditional "for sfv in sfvs" embed-raw-bytes step still stores this sfv regardless.
        Assert.Equal([sfv], result.StoredFiles);
        Assert.Contains(result.Warnings, w => w.Contains(sfv, StringComparison.Ordinal));
    }

    [Fact]
    public void RootAccessDenied_ReturnsWarningsOnlyResult()
    {
        AclDenyHelper.DenyAccess(TempDir);
        try
        {
            if (!DenyTookEffect(TempDir))
            {
                return; // host does not enforce the deny ACE; nothing to assert
            }

            ReleaseScanResult result = new ReleaseScanner().Scan(TempDir);

            Assert.Empty(result.MainSets);
            Assert.Empty(result.SampleFiles);
            Assert.Empty(result.SubtitleSfvs);
            Assert.Empty(result.StoredFiles);
            Assert.Empty(result.MusicSfvs);
            Assert.Single(result.Warnings);
        }
        finally
        {
            AclDenyHelper.RestoreAccess(TempDir);
        }
    }

    [Fact]
    public void PreCancelledToken_ThrowsOperationCanceled()
    {
        string root = CreateRoot("Some.Release-GRP");
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.Throws<OperationCanceledException>(() => new ReleaseScanner().Scan(root, cts.Token));
    }

    // --- Edge cases: unreadable SFVs, cancellation, and rescue ordering -----------------------

    [Fact]
    public void C1_RescuedMusicSfv_NotAlsoListedAsSubtitleSfv()
    {
        // A rescue-promoted MUSIC sfv must not double-list in both MusicSfvs and SubtitleSfvs.
        // Repro: single SFV in Subs/, one music entry -> rule 3 excludes it -> zero main -> rescue
        // admits it to MusicSfvs. Before the fix, the post-rescue exclusion filter checked only
        // `main`, so it also leaked into SubtitleSfvs.
        string root = CreateRoot("Some.Release-GRP");
        string sfv = WriteSfv(Path.Combine(root, "Subs", "track.sfv"), "track.mp3");

        ReleaseScanResult result = new ReleaseScanner().Scan(root);

        Assert.Empty(result.MainSets);
        Assert.Equal([sfv], result.MusicSfvs);
        Assert.Empty(result.SubtitleSfvs);
    }

    [Fact]
    public void I3_UnreadableSfv_InProofDir_WarnsAndSkips_OtherSfvsClassifyNormally()
    {
        // An SFV whose entries can't be read must not abort the whole scan. This covers the
        // ClassifyProof call site — a proof-dir SFV that throws must not be admitted as a main set
        // (or crash Scan); the other SFV must still classify normally.
        string root = CreateRoot("Some.Release-GRP");
        string good = WriteSfv(Path.Combine(root, "CD1", "a.sfv"), "a.rar");
        string bad = WriteSfv(Path.Combine(root, "Proof", "p.sfv"), "p.rar");

        var scanner = new ReleaseScanner(
            sfvEntryReader: _ => throw new IOException("simulated read failure"),
            proofRarReader: null);

        ReleaseScanResult result = scanner.Scan(root);

        Assert.Single(result.MainSets);
        Assert.Equal(good, result.MainSets[0].SfvOrRarPath);
        Assert.Empty(result.SubtitleSfvs);
        // Both sfvs are appended regardless of classification — generate_srr's final SFV-append
        // step never re-reads an sfv's entries, it just embeds the file itself — but NON-MAIN sfvs
        // come first and MAIN sfvs are DEFERRED to the bottom (generate_srr's "add RAR sfv files at
        // the bottom"), not plain traversal order. "bad" (Proof/p.sfv, Skipped — not main) precedes
        // "good" (CD1/a.sfv, the one main set) even though "CD1" sorts before "Proof" ordinally.
        Assert.Equal([bad, good], result.StoredFiles);
        Assert.Contains(result.Warnings, w => w.Contains(bad, StringComparison.Ordinal));
    }

    [Fact]
    public void I3_UnreadableSfv_DuringRescue_WarnsAndSkips_OtherSfvsStillRescued()
    {
        // The rescue pass's own _sfvEntryReader call site must be guarded too — one throwing SFV
        // must not abort rescue for the rest.
        string root = CreateRoot("Some.Release-GRP");
        string good = WriteSfv(Path.Combine(root, "a-subs.sfv"), "a.rar", "a.r00"); // 2 entries -> rescuable
        string bad = WriteSfv(Path.Combine(root, "b-subs.sfv"), "b.rar");

        var scanner = new ReleaseScanner(
            sfvEntryReader: path => path == bad
                ? throw new IOException("simulated read failure")
                : [.. SFVFile.ReadFile(path).Entries.Select(e => e.FileName)],
            proofRarReader: null);

        ReleaseScanResult result = scanner.Scan(root);

        Assert.Single(result.MainSets);
        Assert.Equal(good, result.MainSets[0].SfvOrRarPath);
        Assert.Contains(result.Warnings, w => w.Contains(bad, StringComparison.Ordinal));
    }

    [Fact]
    public void I4_CancelledDuringFinalProofRead_ThrowsBeforeReturningResult()
    {
        // Cancellation observed mid-call (inside the injected proofRarReader) must still surface as
        // OperationCanceledException, not a successful result — the per-iteration checks alone miss
        // cancellation that happens during the LAST piece of work before return.
        string root = CreateRoot("Some.Release-GRP");
        WriteSfv(Path.Combine(root, "Proof", "p.sfv"), "p.rar");
        Touch(Path.Combine(root, "Proof", "p.rar"));

        using var cts = new CancellationTokenSource();
        var scanner = new ReleaseScanner(
            sfvEntryReader: null,
            proofRarReader: (_, _) =>
            {
                cts.Cancel();
                return new ProofRarFacts(Readable: true, HasPackedBlocks: true, AnyImage: false, LastPackedIsImage: false);
            });

        Assert.Throws<OperationCanceledException>(() => scanner.Scan(root, cts.Token));
    }

    [Fact]
    public void I5_SubpackRelease_SubtitleSfvs_PreservesTraversalOrder_NotExcludedThenMain()
    {
        // For a subpack/subfix release, SubtitleSfvs must stay in canonical traversal order across
        // the merged excluded+main-queued set, not [excluded...][main...]. A root-level
        // (traversal-early) main sfv must precede a subdirectory (traversal-later) excluded sfv.
        string root = CreateRoot("Some.SUBPACK-GRP");
        string main = WriteSfv(Path.Combine(root, "main.sfv"), "main.rar");
        string excluded = WriteSfv(Path.Combine(root, "Subs", "excluded.sfv"), "excluded.rar");

        ReleaseScanResult result = new ReleaseScanner().Scan(root);

        Assert.Single(result.MainSets);
        Assert.Equal(main, result.MainSets[0].SfvOrRarPath);
        Assert.Equal([main, excluded], result.SubtitleSfvs);
    }

    [Fact]
    public void SubfixSubstringDir_ReleaseLacksSubfix_Excluded()
    {
        // Minor test gap: rule 6b (`subfix` substring pardir) had no dedicated test (6a subpack
        // and 6c fix were covered).
        string root = CreateRoot("Movie-GRP");
        string sfv = WriteSfv(Path.Combine(root, "SubfixStuff", "x.sfv"), "x.rar");

        ReleaseScanResult result = new ReleaseScanner().Scan(root);

        Assert.Empty(result.MainSets);
        Assert.Equal([sfv], result.SubtitleSfvs);
    }

    [Fact]
    public void AllSfvsExcludedAndUnrescuable_WarnsMightBeMissingSfvFile()
    {
        // Minor test gap: the "zero after rescue" warning (remove_unwanted_sfvs) had no dedicated
        // test.
        string root = CreateRoot("Some.Release-GRP");
        WriteSfv(Path.Combine(root, "a-subs.sfv"), "a.rar"); // 1 entry, no music -> not rescuable

        ReleaseScanResult result = new ReleaseScanner().Scan(root);

        Assert.Empty(result.MainSets);
        Assert.Empty(result.MusicSfvs);
        Assert.Contains(result.Warnings, w => w.Contains("might be missing an SFV file", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Some hosts don't actually enforce an <c>icacls</c> deny ACE. Confirms the deny is real
    /// before an assertion depends on it (same pattern as <c>ReleaseTraversalTests</c>).
    /// </summary>
    private static bool DenyTookEffect(string path)
    {
        try
        {
            Directory.GetFiles(path);
            return false;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return true;
        }
    }
}
