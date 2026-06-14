using ReScene.NET.Models;
using ReScene.NET.Services;
using ReScene.NET.ViewModels;
using ReScene.SRR;

namespace ReScene.NET.Tests;

/// <summary>
/// Tests MKV support in the Inspector: loading an MKV must build the EBML element tree and
/// selecting an element must populate the property grid with its details.
/// </summary>
public class InspectorViewModelMkvTests : IDisposable
{
    private readonly string _tempDir;

    public InspectorViewModelMkvTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"inspector_mkv_{Guid.NewGuid():N}");
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

    private sealed class StubSrrEditingService : ISrrEditingService
    {
        public void AddStoredFiles(string srrFilePath, IReadOnlyList<(string StoredName, string FilePath)> files) => throw new NotSupportedException();
        public void RemoveStoredFiles(string srrFilePath, IReadOnlyList<string> storedNames) => throw new NotSupportedException();
        public Task RenameStoredFileAsync(string srrPath, string oldName, string newName, CancellationToken ct = default) => throw new NotSupportedException();
        public Task MoveStoredFileAsync(string srrPath, string storedName, int offset, CancellationToken ct = default) => throw new NotSupportedException();
        public IReadOnlyList<StoredFileInfo> GetStoredFiles(string srrFilePath) => throw new NotSupportedException();
        public Task<string?> ExtractStoredFileAsync(string srrFilePath, string outputDirectory, string storedName, CancellationToken ct = default) => throw new NotSupportedException();
    }

    private sealed class StubSrrVerifyService : ISrrVerifyService
    {
        public Task<SRRVerifyResult> VerifyAsync(string srrFilePath, CancellationToken ct = default) => throw new NotSupportedException();
    }

    private sealed class StubPropertyExportService : IPropertyExportService
    {
        public Task ExportSelectedAsync(string outputPath, TreeNodeViewModel node, IEnumerable<PropertyItem> properties, CancellationToken ct = default) => throw new NotSupportedException();
        public Task ExportTreeAsync(string outputPath, IEnumerable<TreeNodeViewModel> roots, CancellationToken ct = default) => throw new NotSupportedException();
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

    private static byte[] BuildMkv()
    {
        byte[] ebml = Master([0x1A, 0x45, 0xDF, 0xA3], Str([0x42, 0x82], "matroska"));
        byte[] info = Master([0x15, 0x49, 0xA9, 0x66], Str([0x4D, 0x80], "libebml"));
        byte[] cluster = Master([0x1F, 0x43, 0xB6, 0x75], Leaf([0xE7], [0x00]));
        byte[] segment = Master([0x18, 0x53, 0x80, 0x67], info, cluster);
        return Concat(ebml, segment);
    }

    #endregion

    private static InspectorViewModel CreateViewModel() => new(
        new StubFileDialogService(), new StubSrrEditingService(),
        new StubSrrVerifyService(), new StubPropertyExportService());

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
    public void LoadFile_Mkv_BuildsElementTree()
    {
        string path = Path.Combine(_tempDir, "sample.mkv");
        File.WriteAllBytes(path, BuildMkv());

        using InspectorViewModel vm = CreateViewModel();
        vm.LoadFile(path);

        Assert.True(vm.HasFile, $"status was '{vm.StatusMessage}'");
        Assert.Contains("MKV", vm.StatusMessage, StringComparison.Ordinal);

        TreeNodeViewModel root = Assert.Single(vm.TreeRoots);
        Assert.StartsWith("MKV File", root.Text, StringComparison.Ordinal);
        Assert.Contains(Flatten(vm.TreeRoots), n => n.Text == "Segment");
        Assert.Contains(Flatten(vm.TreeRoots), n => n.Text.StartsWith("MuxingApp: libebml", StringComparison.Ordinal));
    }

    [Fact]
    public void SelectElement_ShowsPropertiesWithByteRanges()
    {
        string path = Path.Combine(_tempDir, "sample.mkv");
        File.WriteAllBytes(path, BuildMkv());

        using InspectorViewModel vm = CreateViewModel();
        vm.LoadFile(path);

        TreeNodeViewModel muxing = Flatten(vm.TreeRoots)
            .First(n => n.Text.StartsWith("MuxingApp", StringComparison.Ordinal));
        vm.SelectedTreeNode = muxing;

        Assert.True(vm.HasProperties);
        Assert.Contains(vm.Properties, p => p.Name == "Element" && p.Value == "MuxingApp");
        Assert.Contains(vm.Properties, p => p.Name == "Value" && p.Value == "libebml");
        // The value row links to the element's data bytes for the hex view.
        Assert.Contains(vm.Properties, p => p.Name == "Value" && p.ByteRange is not null);
        Assert.True(vm.HexBlockLength > 0);
    }
}
