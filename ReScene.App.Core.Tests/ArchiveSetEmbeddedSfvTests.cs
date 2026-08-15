using ReScene.App.Core.ViewModels;
using ReScene.App.Core.ViewModels.Reconstruction;
using ReScene.SRR;

namespace ReScene.App.Core.Tests;

/// <summary>
/// Integration coverage for the per-set embedded-SFV resolution chain that the reconstructor relies
/// on for full per-volume verification: for a real multi-set SRR each set must resolve its OWN
/// embedded SFV (matched by <see cref="ReconstructorViewModel.EmbeddedSfvMatchesSet"/>) and get full
/// per-volume CRC coverage, with no cross-contamination between sets. Regression for the defect where
/// a normal SRR left SRRFilePath null / matched only by directory-prefixed key, so the second disc
/// silently fell back to the first disc's SFV and verification was skipped.
/// </summary>
public class ArchiveSetEmbeddedSfvTests
{
    private static string FixturePath => Path.Combine(AppContext.BaseDirectory, "TestData",
        "cleanup_script",
        "007.A.View.To.A.Kill.1985.UE.iNTERNAL.DVDRip.XviD-iNCiTE.fine_2cd.srr");

    [Fact]
    public void EachSet_ResolvesItsOwnEmbeddedSfv_WithFullPerVolumeCrcCoverage()
    {
        Assert.True(File.Exists(FixturePath), $"Fixture not found: {FixturePath}");

        var srr = SRRFile.Load(FixturePath);
        Assert.Equal(2, srr.ArchiveSets.Count);

        var crcMaps = new Dictionary<string, Dictionary<string, string>>();

        foreach (SRRArchiveSet set in srr.ArchiveSets)
        {
            // Resolve THIS set's embedded SFV using the exact predicate the fix uses.
            byte[]? embedded = srr.ReadStoredFile(FixturePath, name => ReconstructorViewModel.EmbeddedSfvMatchesSet(name, set));
            Assert.NotNull(embedded);
            Assert.NotEmpty(embedded);

            // No user verification snapshot — the embedded SFV alone must cover every volume in the set.
            Dictionary<string, string> crcs = ArchiveSetPlanner.BuildExpectedVolumeCrcs(set, embedded, snapshot: null);

            // Full per-volume coverage: every volume name has a CRC.
            Assert.Equal(set.VolumeNames.Count, crcs.Count);
            foreach (string volume in set.VolumeNames)
            {
                Assert.Contains(Path.GetFileName(volume), crcs.Keys, StringComparer.OrdinalIgnoreCase);
            }

            crcMaps[set.Key] = crcs;
        }

        // Two distinct sets, each with its own (non-empty) CRC map.
        Assert.Equal(2, crcMaps.Count);

        // No cross-contamination: set A's map must not contain any of set B's volume names, and
        // vice-versa. This is what proves disc B got disc B's CRCs (the silently-broken behaviour).
        SRRArchiveSet setA = srr.ArchiveSets[0];
        SRRArchiveSet setB = srr.ArchiveSets[1];
        Dictionary<string, string> mapA = crcMaps[setA.Key];
        Dictionary<string, string> mapB = crcMaps[setB.Key];

        foreach (string volB in setB.VolumeNames)
        {
            Assert.DoesNotContain(Path.GetFileName(volB), mapA.Keys, StringComparer.OrdinalIgnoreCase);
        }

        foreach (string volA in setA.VolumeNames)
        {
            Assert.DoesNotContain(Path.GetFileName(volA), mapB.Keys, StringComparer.OrdinalIgnoreCase);
        }
    }
}
