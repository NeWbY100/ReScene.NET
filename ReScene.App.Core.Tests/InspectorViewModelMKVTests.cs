using ReScene.App.Core.Models;
using ReScene.App.Core.Services;
using ReScene.App.Core.ViewModels;
using ReScene.SRR;
namespace ReScene.App.Core.Tests;

/// <summary>
/// Tests MKV support in the Inspector: loading an MKV must build the EBML element tree and
/// selecting an element must populate the property grid with its details.
/// </summary>
public class InspectorViewModelMKVTests : TempDirTestBase
{
    #region Stub services

    private sealed class StubSRREditingService : ISRREditingService
    {
        public void AddStoredFiles(string srrFilePath, IReadOnlyList<(string StoredName, string FilePath)> files) => throw new NotSupportedException();
        public void RemoveStoredFiles(string srrFilePath, IReadOnlyList<string> storedNames) => throw new NotSupportedException();
        public Task RenameStoredFileAsync(string srrPath, string oldName, string newName, CancellationToken ct = default) => throw new NotSupportedException();
        public Task MoveStoredFileAsync(string srrPath, string storedName, int offset, CancellationToken ct = default) => throw new NotSupportedException();
        public IReadOnlyList<StoredFileInfo> GetStoredFiles(string srrFilePath) => throw new NotSupportedException();
        public Task<string?> ExtractStoredFileAsync(string srrFilePath, string outputDirectory, string storedName, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<byte[]?> ReadStoredFileBytesAsync(string srrFilePath, string storedName, CancellationToken ct = default) => throw new NotSupportedException();
    }

    private sealed class StubSRRVerifyService : ISRRVerifyService
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

    private static byte[] BuildMKV()
    {
        byte[] ebml = EBMLTestWriter.Master([0x1A, 0x45, 0xDF, 0xA3], EBMLTestWriter.Str([0x42, 0x82], "matroska"));
        byte[] info = EBMLTestWriter.Master([0x15, 0x49, 0xA9, 0x66], EBMLTestWriter.Str([0x4D, 0x80], "libebml"));
        byte[] cluster = EBMLTestWriter.Master([0x1F, 0x43, 0xB6, 0x75], EBMLTestWriter.Leaf([0xE7], [0x00]));
        byte[] segment = EBMLTestWriter.Master([0x18, 0x53, 0x80, 0x67], info, cluster);
        return EBMLTestWriter.Concat(ebml, segment);
    }

    #endregion

    // Drives the now-async InspectorViewModel.LoadFileAsync synchronously for tests. Wrapping in
    // Task.Run detaches from any ambient synchronization context, so the internal `await Task.Run`
    // continuation runs on the thread pool and GetResult cannot deadlock.
    private static void LoadInspector(InspectorViewModel vm, string path)
        => Task.Run(() => vm.LoadFileAsync(path)).GetAwaiter().GetResult();

    private static InspectorViewModel CreateViewModel() => new(
        new NoOpFileDialogService(), new StubSRREditingService(),
        new StubSRRVerifyService(), new StubPropertyExportService(),
        new RecordingImagePreviewService());

    [Fact]
    public void LoadFile_MKV_BuildsElementTree()
    {
        string path = Path.Combine(TempDir, "sample.mkv");
        File.WriteAllBytes(path, BuildMKV());

        using InspectorViewModel vm = CreateViewModel();
        LoadInspector(vm, path);

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
        File.WriteAllBytes(path, BuildMKV());

        using InspectorViewModel vm = CreateViewModel();
        LoadInspector(vm, path);

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
