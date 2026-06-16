using System.Collections.ObjectModel;
using ReScene.Hex;
using ReScene.NET.Services;
using ReScene.NET.ViewModels;

namespace ReScene.NET.Tests;

/// <summary>
/// End-to-end tests for MKV comparison through <see cref="FileCompareViewModel"/>: loading two MKV
/// files must mark differing elements red (IsDifferent) in the structure trees.
/// </summary>
public class FileCompareViewModelMkvTests : TempDirTestBase
{
    #region Stub services

    private sealed class StubHexDiffComputer : IHexDiffComputer
    {
        public Task<HexDiffResult> ComputeAsync(
            IHexDataSource leftSource, long leftOffset, long leftLength,
            IHexDataSource rightSource, long rightOffset, long rightLength,
            IProgress<HexDiffProgress>? progress, CancellationToken ct) =>
            Task.FromResult(new HexDiffResult([], []));
    }

    #endregion

    #region EBML encoding helpers

    private static byte[] IdEbml => [0x1A, 0x45, 0xDF, 0xA3];
    private static byte[] IdDocType => [0x42, 0x82];
    private static byte[] IdSegment => [0x18, 0x53, 0x80, 0x67];
    private static byte[] IdInfo => [0x15, 0x49, 0xA9, 0x66];
    private static byte[] IdMuxingApp => [0x4D, 0x80];
    private static byte[] IdCluster => [0x1F, 0x43, 0xB6, 0x75];
    private static byte[] IdClusterTimestamp => [0xE7];
    private static byte[] IdSimpleBlock => [0xA3];

    /// <summary>
    /// Builds a minimal MKV: EBML header + Segment(Info(MuxingApp), Cluster(Timestamp, SimpleBlock)).
    /// </summary>
    private static byte[] BuildMkv(string muxingApp, byte clusterFill)
    {
        byte[] ebml = EbmlTestWriter.Master(IdEbml, EbmlTestWriter.Str(IdDocType, "matroska"));
        byte[] info = EbmlTestWriter.Master(IdInfo, EbmlTestWriter.Str(IdMuxingApp, muxingApp));
        byte[] payload = new byte[64];
        Array.Fill(payload, clusterFill);
        byte[] cluster = EbmlTestWriter.Master(IdCluster, EbmlTestWriter.Leaf(IdClusterTimestamp, [0x00]), EbmlTestWriter.Leaf(IdSimpleBlock, payload));
        byte[] segment = EbmlTestWriter.Master(IdSegment, info, cluster);
        return EbmlTestWriter.Concat(ebml, segment);
    }

    #endregion

    private string WriteMkv(string name, byte[] bytes)
    {
        string path = Path.Combine(TempDir, name);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    private static FileCompareViewModel CreateViewModel() =>
        new(new FileCompareService(), new NoOpFileDialogService(), new StubHexDiffComputer());

    [Fact]
    public void Compare_MetadataDiffers_MarksTreeNodesDifferent()
    {
        string left = WriteMkv("left.mkv", BuildMkv("libebml", 0xAA));
        string right = WriteMkv("right.mkv", BuildMkv("mkvmerge", 0xAA));

        using FileCompareViewModel vm = CreateViewModel();
        vm.LoadLeftFile(left);
        vm.LoadRightFile(right);

        TreeNodeViewModel? muxLeft = vm.LeftTreeRoots.Flatten().FirstOrDefault(n => n.Text.StartsWith("MuxingApp", StringComparison.Ordinal));
        TreeNodeViewModel? muxRight = vm.RightTreeRoots.Flatten().FirstOrDefault(n => n.Text.StartsWith("MuxingApp", StringComparison.Ordinal));

        Assert.NotNull(muxLeft);
        Assert.NotNull(muxRight);
        Assert.True(muxLeft!.IsDifferent, $"left MuxingApp node should be red; text was '{muxLeft.Text}'");
        Assert.True(muxRight!.IsDifferent, $"right MuxingApp node should be red; text was '{muxRight.Text}'");
        Assert.Contains("[DIFF]", muxLeft.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Compare_OnlyClusterContentDiffers_MarksClusterNodesDifferent()
    {
        // Identical metadata; only the audio/video payload bytes inside the Cluster differ
        // (same length). This is the typical "rebuilt sample vs original sample" case.
        string left = WriteMkv("left.mkv", BuildMkv("libebml", 0xAA));
        string right = WriteMkv("right.mkv", BuildMkv("libebml", 0xBB));

        using FileCompareViewModel vm = CreateViewModel();
        vm.LoadLeftFile(left);
        vm.LoadRightFile(right);

        TreeNodeViewModel? clusterLeft = vm.LeftTreeRoots.Flatten().FirstOrDefault(n => n.Text.StartsWith("Cluster", StringComparison.Ordinal));
        Assert.NotNull(clusterLeft);
        Assert.True(clusterLeft!.IsDifferent,
            $"Cluster node should be red when its content differs; text was '{clusterLeft.Text}'");
        Assert.False(vm.FilesIdentical);
    }

    [Fact]
    public void Compare_IdenticalFiles_ReportsIdentical()
    {
        byte[] bytes = BuildMkv("libebml", 0xAA);
        string left = WriteMkv("left.mkv", bytes);
        string right = WriteMkv("right.mkv", bytes);

        using FileCompareViewModel vm = CreateViewModel();
        vm.LoadLeftFile(left);
        vm.LoadRightFile(right);

        Assert.True(vm.FilesIdentical, $"status was '{vm.StatusMessage}'");
        Assert.DoesNotContain(vm.LeftTreeRoots.Flatten(), n => n.IsDifferent);
    }
}
