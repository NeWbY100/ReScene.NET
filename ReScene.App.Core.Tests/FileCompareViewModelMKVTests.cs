using ReScene.App.Core.Services;
using ReScene.App.Core.ViewModels;
using ReScene.Core.Comparison;
using ReScene.Hex;
using ReScene.RAR;
namespace ReScene.App.Core.Tests;

/// <summary>
/// End-to-end tests for MKV comparison through <see cref="FileCompareViewModel"/>: loading two MKV
/// files must mark differing elements red (IsDifferent) in the structure trees.
/// </summary>
public class FileCompareViewModelMKVTests : TempDirTestBase
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

    private sealed class GatedCompareService : IFileCompareService
    {
        private readonly ManualResetEventSlim _release = new(false);
        public ManualResetEventSlim Entered { get; } = new(false);

        public void Release() => _release.Set();

        public object? LoadFileData(string filePath)
        {
            Entered.Set();
            _release.Wait();
            return null; // data unused by the IsComparing lifecycle; null avoids PopulateTree on an unknown type
        }

        public IReadOnlyList<RARDetailedBlock>? ParseDetailedBlocks(string filePath) => [];

        public CompareResult Compare(object? left, object? right,
            IReadOnlyList<RARDetailedBlock>? leftBlocks = null, IReadOnlyList<RARDetailedBlock>? rightBlocks = null,
            IHexDataSource? leftSource = null, IHexDataSource? rightSource = null) => new();
    }

    #endregion

    #region EBML encoding helpers

    private static byte[] IdEBML => [0x1A, 0x45, 0xDF, 0xA3];
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
    private static byte[] BuildMKV(string muxingApp, byte clusterFill)
    {
        byte[] ebml = EBMLTestWriter.Master(IdEBML, EBMLTestWriter.Str(IdDocType, "matroska"));
        byte[] info = EBMLTestWriter.Master(IdInfo, EBMLTestWriter.Str(IdMuxingApp, muxingApp));
        byte[] payload = new byte[64];
        Array.Fill(payload, clusterFill);
        byte[] cluster = EBMLTestWriter.Master(IdCluster, EBMLTestWriter.Leaf(IdClusterTimestamp, [0x00]), EBMLTestWriter.Leaf(IdSimpleBlock, payload));
        byte[] segment = EBMLTestWriter.Master(IdSegment, info, cluster);
        return EBMLTestWriter.Concat(ebml, segment);
    }

    #endregion

    private string WriteMKV(string name, byte[] bytes)
    {
        string path = Path.Combine(TempDir, name);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    private static FileCompareViewModel CreateViewModel() =>
        new(new FileCompareService(), new NoOpFileDialogService(), new StubHexDiffComputer(), new TestUiDispatcher());

    [Fact]
    public async Task Compare_MetadataDiffers_MarksTreeNodesDifferent()
    {
        string left = WriteMKV("left.mkv", BuildMKV("libebml", 0xAA));
        string right = WriteMKV("right.mkv", BuildMKV("mkvmerge", 0xAA));

        using FileCompareViewModel vm = CreateViewModel();
        await vm.LoadLeftFileAsync(left);
        await vm.LoadRightFileAsync(right);

        TreeNodeViewModel? muxLeft = vm.LeftTreeRoots.Flatten().FirstOrDefault(n => n.Text.StartsWith("MuxingApp", StringComparison.Ordinal));
        TreeNodeViewModel? muxRight = vm.RightTreeRoots.Flatten().FirstOrDefault(n => n.Text.StartsWith("MuxingApp", StringComparison.Ordinal));

        Assert.NotNull(muxLeft);
        Assert.NotNull(muxRight);
        Assert.True(muxLeft.IsDifferent, $"left MuxingApp node should be red; text was '{muxLeft.Text}'");
        Assert.True(muxRight.IsDifferent, $"right MuxingApp node should be red; text was '{muxRight.Text}'");
        Assert.Contains("[DIFF]", muxLeft.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Compare_OnlyClusterContentDiffers_MarksClusterNodesDifferent()
    {
        // Identical metadata; only the audio/video payload bytes inside the Cluster differ
        // (same length). This is the typical "rebuilt sample vs original sample" case.
        string left = WriteMKV("left.mkv", BuildMKV("libebml", 0xAA));
        string right = WriteMKV("right.mkv", BuildMKV("libebml", 0xBB));

        using FileCompareViewModel vm = CreateViewModel();
        await vm.LoadLeftFileAsync(left);
        await vm.LoadRightFileAsync(right);

        TreeNodeViewModel? clusterLeft = vm.LeftTreeRoots.Flatten().FirstOrDefault(n => n.Text.StartsWith("Cluster", StringComparison.Ordinal));
        Assert.NotNull(clusterLeft);
        Assert.True(clusterLeft.IsDifferent,
            $"Cluster node should be red when its content differs; text was '{clusterLeft.Text}'");
        Assert.False(vm.FilesIdentical);
    }

    [Fact]
    public async Task Compare_IdenticalFiles_ReportsIdentical()
    {
        byte[] bytes = BuildMKV("libebml", 0xAA);
        string left = WriteMKV("left.mkv", bytes);
        string right = WriteMKV("right.mkv", bytes);

        using FileCompareViewModel vm = CreateViewModel();
        await vm.LoadLeftFileAsync(left);
        await vm.LoadRightFileAsync(right);

        Assert.True(vm.FilesIdentical, $"status was '{vm.StatusMessage}'");
        Assert.DoesNotContain(vm.LeftTreeRoots.Flatten(), n => n.IsDifferent);
    }

    [Fact]
    public async Task IsComparing_TrueDuringLoad_FalseAfter()
    {
        string path = WriteMKV("one.mkv", BuildMKV("libebml", 0xAA));
        var gated = new GatedCompareService();
        using var vm = new FileCompareViewModel(gated, new NoOpFileDialogService(), new StubHexDiffComputer(), new TestUiDispatcher());

        Task load = vm.LoadLeftFileAsync(path); // runs synchronously up to the Task.Run await

        Assert.True(gated.Entered.Wait(TimeSpan.FromSeconds(5)));
        Assert.True(vm.IsComparing);            // set synchronously before the first await
        Assert.False(vm.IsNotComparing);

        gated.Release();
        await load;

        Assert.False(vm.IsComparing);
        Assert.True(vm.IsNotComparing);
    }

    [Fact]
    public async Task LoadWhileComparing_IsIgnored()
    {
        string path = WriteMKV("one.mkv", BuildMKV("libebml", 0xAA));
        var gated = new GatedCompareService();
        using var vm = new FileCompareViewModel(gated, new NoOpFileDialogService(), new StubHexDiffComputer(), new TestUiDispatcher());

        Task first = vm.LoadLeftFileAsync(path);
        Assert.True(gated.Entered.Wait(TimeSpan.FromSeconds(5)));

        await vm.LoadRightFileAsync(path); // re-entrancy guard: returns immediately, no-op
        Assert.True(vm.IsComparing);       // the first load still owns the flag
        Assert.Equal(string.Empty, vm.RightFilePath); // the ignored load did not set state

        gated.Release();
        await first;
        Assert.False(vm.IsComparing);
    }
}
