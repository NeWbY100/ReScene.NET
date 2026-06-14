using System.Collections.ObjectModel;
using ReScene.Hex;
using ReScene.NET.Services;
using ReScene.NET.ViewModels;

namespace ReScene.NET.Tests;

/// <summary>
/// End-to-end tests for MKV comparison through <see cref="FileCompareViewModel"/>: loading two MKV
/// files must mark differing elements red (IsDifferent) in the structure trees.
/// </summary>
public class FileCompareViewModelMkvTests : IDisposable
{
    private readonly string _tempDir;

    public FileCompareViewModelMkvTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"comparevm_mkv_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_tempDir, true);
        }
        catch (IOException)
        {
            // Best-effort cleanup.
        }

        GC.SuppressFinalize(this);
    }

    #region Stub services

    private sealed class StubFileDialogService : IFileDialogService
    {
        public Task<string?> OpenFileAsync(string title, IReadOnlyList<string> filters) => Task.FromResult<string?>(null);
        public Task<IReadOnlyList<string>> OpenFilesAsync(string title, IReadOnlyList<string> filters) => Task.FromResult<IReadOnlyList<string>>([]);
        public Task<string?> SaveFileAsync(string title, string defaultExtension, IReadOnlyList<string> filters, string? defaultFileName = null) => Task.FromResult<string?>(null);
        public Task<string?> OpenFolderAsync(string title) => Task.FromResult<string?>(null);
        public Task<bool> ShowConfirmAsync(string title, string message) => Task.FromResult(false);
        public Task<string?> PromptForTextAsync(string title, string message, string initialValue) => Task.FromResult<string?>(null);
    }

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

    private static byte[] Master(byte[] id, params byte[][] children)
    {
        byte[] body = Concat(children);
        return Concat(id, EncodeSize(body.Length), body);
    }

    private static byte[] Leaf(byte[] id, byte[] payload) => Concat(id, EncodeSize(payload.Length), payload);

    private static byte[] Str(byte[] id, string value) => Leaf(id, System.Text.Encoding.UTF8.GetBytes(value));

    private static byte[] EncodeSize(long size) =>
        size < 0x7F ? [(byte)(0x80 | size)] : [(byte)(0x40 | (size >> 8)), (byte)size];

    private static byte[] Concat(params byte[][] parts)
    {
        byte[] result = new byte[parts.Sum(p => p.Length)];
        int offset = 0;
        foreach (byte[] p in parts)
        {
            Buffer.BlockCopy(p, 0, result, offset, p.Length);
            offset += p.Length;
        }

        return result;
    }

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
        byte[] ebml = Master(IdEbml, Str(IdDocType, "matroska"));
        byte[] info = Master(IdInfo, Str(IdMuxingApp, muxingApp));
        byte[] payload = new byte[64];
        Array.Fill(payload, clusterFill);
        byte[] cluster = Master(IdCluster, Leaf(IdClusterTimestamp, [0x00]), Leaf(IdSimpleBlock, payload));
        byte[] segment = Master(IdSegment, info, cluster);
        return Concat(ebml, segment);
    }

    #endregion

    private string WriteMkv(string name, byte[] bytes)
    {
        string path = Path.Combine(_tempDir, name);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    private static FileCompareViewModel CreateViewModel() =>
        new(new FileCompareService(), new StubFileDialogService(), new StubHexDiffComputer());

    private static IEnumerable<TreeNodeViewModel> Flatten(IEnumerable<TreeNodeViewModel> nodes)
    {
        foreach (TreeNodeViewModel node in nodes)
        {
            yield return node;
            foreach (TreeNodeViewModel child in Flatten(node.Children))
            {
                yield return child;
            }
        }
    }

    [Fact]
    public void Compare_MetadataDiffers_MarksTreeNodesDifferent()
    {
        string left = WriteMkv("left.mkv", BuildMkv("libebml", 0xAA));
        string right = WriteMkv("right.mkv", BuildMkv("mkvmerge", 0xAA));

        using FileCompareViewModel vm = CreateViewModel();
        vm.LoadLeftFile(left);
        vm.LoadRightFile(right);

        TreeNodeViewModel? muxLeft = Flatten(vm.LeftTreeRoots).FirstOrDefault(n => n.Text.StartsWith("MuxingApp", StringComparison.Ordinal));
        TreeNodeViewModel? muxRight = Flatten(vm.RightTreeRoots).FirstOrDefault(n => n.Text.StartsWith("MuxingApp", StringComparison.Ordinal));

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

        TreeNodeViewModel? clusterLeft = Flatten(vm.LeftTreeRoots).FirstOrDefault(n => n.Text.StartsWith("Cluster", StringComparison.Ordinal));
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
        Assert.DoesNotContain(Flatten(vm.LeftTreeRoots), n => n.IsDifferent);
    }
}
