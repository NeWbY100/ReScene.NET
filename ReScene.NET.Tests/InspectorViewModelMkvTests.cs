using ReScene.NET.Models;
using ReScene.NET.Services;
using ReScene.NET.ViewModels;
using ReScene.SRR;

namespace ReScene.NET.Tests;

/// <summary>
/// Tests MKV support in the Inspector: loading an MKV must build the EBML element tree and
/// selecting an element must populate the property grid with its details.
/// </summary>
public class InspectorViewModelMkvTests : TempDirTestBase
{
    #region Stub services

    private sealed class StubSrrEditingService : ISrrEditingService
    {
        public void AddStoredFiles(string srrFilePath, IReadOnlyList<(string StoredName, string FilePath)> files) => throw new NotSupportedException();
        public void RemoveStoredFiles(string srrFilePath, IReadOnlyList<string> storedNames) => throw new NotSupportedException();
        public Task RenameStoredFileAsync(string srrPath, string oldName, string newName, CancellationToken ct = default) => throw new NotSupportedException();
        public Task MoveStoredFileAsync(string srrPath, string storedName, int offset, CancellationToken ct = default) => throw new NotSupportedException();
        public IReadOnlyList<StoredFileInfo> GetStoredFiles(string srrFilePath) => throw new NotSupportedException();
        public Task<string?> ExtractStoredFileAsync(string srrFilePath, string outputDirectory, string storedName, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<byte[]?> ReadStoredFileBytesAsync(string srrFilePath, string storedName, CancellationToken ct = default) => throw new NotSupportedException();
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

    private static byte[] BuildMkv()
    {
        byte[] ebml = EbmlTestWriter.Master([0x1A, 0x45, 0xDF, 0xA3], EbmlTestWriter.Str([0x42, 0x82], "matroska"));
        byte[] info = EbmlTestWriter.Master([0x15, 0x49, 0xA9, 0x66], EbmlTestWriter.Str([0x4D, 0x80], "libebml"));
        byte[] cluster = EbmlTestWriter.Master([0x1F, 0x43, 0xB6, 0x75], EbmlTestWriter.Leaf([0xE7], [0x00]));
        byte[] segment = EbmlTestWriter.Master([0x18, 0x53, 0x80, 0x67], info, cluster);
        return EbmlTestWriter.Concat(ebml, segment);
    }

    #endregion

    private static InspectorViewModel CreateViewModel() => new(
        new NoOpFileDialogService(), new StubSrrEditingService(),
        new StubSrrVerifyService(), new StubPropertyExportService());

    [Fact]
    public void LoadFile_Mkv_BuildsElementTree()
    {
        string path = Path.Combine(TempDir, "sample.mkv");
        File.WriteAllBytes(path, BuildMkv());

        using InspectorViewModel vm = CreateViewModel();
        vm.LoadFile(path);

        Assert.True(vm.HasFile, $"status was '{vm.StatusMessage}'");
        Assert.Contains("MKV", vm.StatusMessage, StringComparison.Ordinal);

        TreeNodeViewModel root = Assert.Single(vm.TreeRoots);
        Assert.StartsWith("MKV File", root.Text, StringComparison.Ordinal);
        Assert.Contains(vm.TreeRoots.Flatten(), n => n.Text == "Segment");
        Assert.Contains(vm.TreeRoots.Flatten(), n => n.Text.StartsWith("MuxingApp: libebml", StringComparison.Ordinal));
    }

    [Fact]
    public void SelectElement_ShowsPropertiesWithByteRanges()
    {
        string path = Path.Combine(TempDir, "sample.mkv");
        File.WriteAllBytes(path, BuildMkv());

        using InspectorViewModel vm = CreateViewModel();
        vm.LoadFile(path);

        TreeNodeViewModel muxing = vm.TreeRoots.Flatten()
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
