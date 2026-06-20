using ReScene.NET.Models;
using ReScene.NET.Services;
using ReScene.NET.ViewModels;
using ReScene.SRR;

namespace ReScene.NET.Tests;

public class InspectorViewModelImageTests : TempDirTestBase
{
    // Editing service that only serves ReadStoredFileBytesAsync; other members are unused here.
    private sealed class FakeReadEditingService : ISrrEditingService
    {
        public byte[]? BytesToReturn { get; set; }
        public (string Path, string Name)? LastRead { get; private set; }

        public void AddStoredFiles(string srrFilePath, IReadOnlyList<(string StoredName, string FilePath)> files) => throw new NotSupportedException();
        public void RemoveStoredFiles(string srrFilePath, IReadOnlyList<string> storedNames) => throw new NotSupportedException();
        public Task RenameStoredFileAsync(string srrPath, string oldName, string newName, CancellationToken ct = default) => throw new NotSupportedException();
        public Task MoveStoredFileAsync(string srrPath, string storedName, int offset, CancellationToken ct = default) => throw new NotSupportedException();
        public IReadOnlyList<StoredFileInfo> GetStoredFiles(string srrFilePath) => throw new NotSupportedException();
        public Task<string?> ExtractStoredFileAsync(string srrFilePath, string outputDirectory, string storedName, CancellationToken ct = default) => throw new NotSupportedException();

        public Task<byte[]?> ReadStoredFileBytesAsync(string srrFilePath, string storedName, CancellationToken ct = default)
        {
            LastRead = (srrFilePath, storedName);
            return Task.FromResult(BytesToReturn);
        }
    }

    private sealed class StubVerifyService : ISrrVerifyService
    {
        public Task<SRRVerifyResult> VerifyAsync(string srrFilePath, CancellationToken ct = default) => throw new NotSupportedException();
    }

    private sealed class StubPropertyExportService : IPropertyExportService
    {
        public Task ExportSelectedAsync(string outputPath, TreeNodeViewModel node, IEnumerable<PropertyItem> properties, CancellationToken ct = default) => throw new NotSupportedException();
        public Task ExportTreeAsync(string outputPath, IEnumerable<TreeNodeViewModel> roots, CancellationToken ct = default) => throw new NotSupportedException();
    }

    private static InspectorViewModel CreateVm(FakeReadEditingService editing, RecordingImagePreviewService preview) =>
        new(new NoOpFileDialogService(), editing, new StubVerifyService(), new StubPropertyExportService(), preview);

    private InspectorViewModel LoadWithStored(string storedName, FakeReadEditingService editing, RecordingImagePreviewService preview)
    {
        string srr = SRREditingServiceImageTests.WriteMinimalSrr(TempDir, "inspect.srr", storedName, [0x00]);
        InspectorViewModel vm = CreateVm(editing, preview);
        vm.LoadFile(srr);
        vm.SelectedTreeNode = vm.TreeRoots.Flatten()
            .First(n => n.Tag is SRRStoredFileBlock b && b.FileName == storedName);
        return vm;
    }

    [Fact]
    public void SelectImageStoredFile_MakesPreviewAvailable()
    {
        using InspectorViewModel vm = LoadWithStored("proof.jpg", new FakeReadEditingService(), new RecordingImagePreviewService());

        Assert.True(vm.IsImagePreviewAvailable);
        Assert.True(vm.PreviewStoredImageCommand.CanExecute(null));
    }

    [Fact]
    public void SelectNonImageStoredFile_PreviewUnavailable()
    {
        using InspectorViewModel vm = LoadWithStored("readme.nfo", new FakeReadEditingService(), new RecordingImagePreviewService());

        Assert.False(vm.IsImagePreviewAvailable);
        Assert.False(vm.PreviewStoredImageCommand.CanExecute(null));
    }

    [Fact]
    public async Task PreviewCommand_ForwardsBytesAndName()
    {
        var editing = new FakeReadEditingService { BytesToReturn = [0x01, 0x02, 0x03] };
        var preview = new RecordingImagePreviewService();
        using InspectorViewModel vm = LoadWithStored("proof.jpg", editing, preview);

        await vm.PreviewStoredImageCommand.ExecuteAsync(null);

        (byte[] data, string fileName) = Assert.Single(preview.Calls);
        Assert.Equal(new byte[] { 0x01, 0x02, 0x03 }, data);
        Assert.Equal("proof.jpg", fileName);
        Assert.Equal("proof.jpg", editing.LastRead!.Value.Name);
    }
}
