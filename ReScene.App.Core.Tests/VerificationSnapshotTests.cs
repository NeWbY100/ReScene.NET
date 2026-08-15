using ReScene.App.Core.ViewModels.Reconstruction;
using ReScene.Core.Cryptography;

namespace ReScene.App.Core.Tests;

public class VerificationSnapshotTests
{
    [Fact]
    public void VolumeNames_ExcludesNonRarEntry_KeepsRarVolumes()
    {
        // Matches the old ResolveSfvVolumeNames' IsRARVolume filter: a stray non-volume entry
        // (e.g. a .nfo checksum listed alongside the RAR volumes) is excluded.
        var snapshot = new VerificationSnapshot(HashType.CRC32,
        [
            ("movie.rar", "aaaaaaaa"),
            ("movie.r00", "bbbbbbbb"),
            ("movie.nfo", "cccccccc"),
        ]);

        Assert.Equal(["movie.rar", "movie.r00"], snapshot.VolumeNames);
    }

    [Fact]
    public void Sha1Snapshot_Crc32ByNameEmpty_AllHashesPopulated()
    {
        // Only CRC32 snapshots feed per-volume verification; a SHA1 snapshot feeds options.Hashes
        // (via AllHashes) only.
        var snapshot = new VerificationSnapshot(HashType.SHA1,
        [
            ("movie.mkv", "0123456789abcdef0123456789abcdef01234567"),
        ]);

        Assert.Empty(snapshot.Crc32ByName);
        Assert.Equal(["0123456789abcdef0123456789abcdef01234567"], snapshot.AllHashes);
    }

    [Fact]
    public void HashesForVolumes_AmbiguousBasename_DoesNotResolve()
    {
        // Two entries share a basename under different directories — the fallback must not collapse
        // them to either one.
        var snapshot = new VerificationSnapshot(HashType.CRC32,
        [
            ("CD1/movie.rar", "aaaaaaaa"),
            ("CD2/movie.rar", "bbbbbbbb"),
        ]);

        IReadOnlyDictionary<string, string> result = snapshot.HashesForVolumes(["movie.rar"]);

        Assert.Empty(result);
    }

    [Fact]
    public void HashesForVolumes_UnambiguousBasename_ResolvesAndKeysByTheVolumesOwnQualifiedKey()
    {
        var snapshot = new VerificationSnapshot(HashType.CRC32,
        [
            ("aln-re4a.rar", "f1a3ec0d"),
        ]);

        IReadOnlyDictionary<string, string> result = snapshot.HashesForVolumes(["DVD1\\aln-re4a.rar"]);

        Assert.Equal("f1a3ec0d", result["DVD1/aln-re4a.rar"]);
    }

    [Fact]
    public void HashesForVolumes_QualifiedKeyMatch_TakesPriorityOverBasenameFallback()
    {
        var snapshot = new VerificationSnapshot(HashType.CRC32,
        [
            ("CD1/movie.rar", "aaaaaaaa"),
            ("CD2/movie.rar", "bbbbbbbb"),
        ]);

        IReadOnlyDictionary<string, string> result = snapshot.HashesForVolumes(["CD2\\movie.rar"]);

        Assert.Equal("bbbbbbbb", result["CD2/movie.rar"]);
    }
}
