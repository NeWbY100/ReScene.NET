using System.Text;
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

    // File dialog whose Save returns a fixed path, so an export writes to a known location.
    private sealed class SaveToPathDialog(string path) : NoOpFileDialogService
    {
        public override Task<string?> SaveFileAsync(string title, string defaultExtension, IReadOnlyList<string> filters, string? defaultFileName = null)
            => Task.FromResult<string?>(path);
    }

    // File dialog that records ShowError calls, so a failed load can be asserted.
    private sealed class RecordingErrorDialog : NoOpFileDialogService
    {
        public List<(string Title, string Message)> Errors { get; } = [];

        public override void ShowError(string title, string message) => Errors.Add((title, message));
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

    [Fact]
    public async Task ExportBlock_StoredFile_WritesPayloadWithoutSrrHeader()
    {
        // A distinctive payload so we can prove only it (not the wrapping SRR block header) is written.
        byte[] payload = [0x66, 0x4C, 0x61, 0x43, 0x73, 0x00, 0x01, 0x02]; // "fLaCs"…
        string srr = SRREditingServiceImageTests.WriteMinimalSrr(TempDir, "wrap.srr", "song.srs", payload);
        string outPath = Path.Combine(TempDir, "exported.srs");

        using InspectorViewModel vm = new(
            new SaveToPathDialog(outPath), new FakeReadEditingService(),
            new StubVerifyService(), new StubPropertyExportService(), new RecordingImagePreviewService());
        vm.LoadFile(srr);
        vm.SelectedTreeNode = vm.TreeRoots.Flatten()
            .First(n => n.Tag is SRRStoredFileBlock b && b.FileName == "song.srs");

        await vm.ExportBlockCommand.ExecuteAsync(null);

        Assert.True(File.Exists(outPath));
        // Exactly the stored payload — no leading SRR StoredFile block header.
        Assert.Equal(payload, File.ReadAllBytes(outPath));
    }

    [Fact]
    public void LoadFile_UnparseableFile_ShowsErrorDialog()
    {
        // A .srs whose bytes match no SRS container marker → the parser throws.
        string bad = Path.Combine(TempDir, "bad.srs");
        File.WriteAllBytes(bad, [0x6A, 0x6A, 0x6A, 0x00, 0x00, 0x00, 0x00, 0x00]);

        var dialog = new RecordingErrorDialog();
        using InspectorViewModel vm = new(
            dialog, new FakeReadEditingService(),
            new StubVerifyService(), new StubPropertyExportService(), new RecordingImagePreviewService());

        vm.LoadFile(bad);

        Assert.False(vm.HasFile);
        (string title, string message) = Assert.Single(dialog.Errors);
        Assert.Equal("Could not open file", title);
        Assert.Contains("bad.srs", message, StringComparison.Ordinal);
    }

    [Fact]
    public void TextView_FreshVm_DefaultsToUtf8Inactive()
    {
        using InspectorViewModel vm = CreateVm(new FakeReadEditingService(), new RecordingImagePreviewService());

        Assert.False(vm.IsTextViewActive);
        Assert.True(vm.IsHexViewActive);
        Assert.False(vm.TextWordWrap);
        Assert.Equal("UTF-8", vm.SelectedEncoding.DisplayName);
        Assert.Equal(string.Empty, vm.TextViewContent);
    }

    [Fact]
    public void TextView_WhenActivated_DecodesSelectedBlock()
    {
        byte[] payload = Encoding.ASCII.GetBytes("MARKER_TEXT_12345");
        string srr = SRREditingServiceImageTests.WriteMinimalSrr(TempDir, "note.srr", "note.nfo", payload);

        using InspectorViewModel vm = CreateVm(new FakeReadEditingService(), new RecordingImagePreviewService());
        vm.LoadFile(srr);
        vm.SelectedTreeNode = vm.TreeRoots.Flatten()
            .First(n => n.Tag is SRRStoredFileBlock b && b.FileName == "note.nfo");

        vm.IsTextViewActive = true;

        // The selected block region (stored file) decodes to text containing the payload.
        Assert.Contains("MARKER_TEXT_12345", vm.TextViewContent, StringComparison.Ordinal);
    }

    [Fact]
    public void TextView_ChangingEncoding_Redecodes()
    {
        // 0xC9 → CP437 '╔' (U+2554) vs Latin-1 'É' (U+00C9): proves a re-decode on encoding change.
        byte[] payload = [0xC9];
        string srr = SRREditingServiceImageTests.WriteMinimalSrr(TempDir, "enc.srr", "enc.bin", payload);

        using InspectorViewModel vm = CreateVm(new FakeReadEditingService(), new RecordingImagePreviewService());
        vm.LoadFile(srr);
        vm.SelectedTreeNode = vm.TreeRoots.Flatten()
            .First(n => n.Tag is SRRStoredFileBlock b && b.FileName == "enc.bin");
        vm.IsTextViewActive = true;

        vm.SelectedEncoding = vm.TextEncodings.First(e => e.DisplayName == "CP437 (DOS)");
        Assert.Contains('╔', vm.TextViewContent);

        vm.SelectedEncoding = vm.TextEncodings.First(e => e.DisplayName == "ISO-8859-1 (Latin-1)");
        Assert.Contains('É', vm.TextViewContent);
        Assert.DoesNotContain('╔', vm.TextViewContent);
    }

    [Fact]
    public void TextView_InactiveByDefault_DoesNotDecodeOnSelection()
    {
        byte[] payload = Encoding.ASCII.GetBytes("SHOULD_NOT_DECODE");
        string srr = SRREditingServiceImageTests.WriteMinimalSrr(TempDir, "lazy.srr", "lazy.nfo", payload);

        using InspectorViewModel vm = CreateVm(new FakeReadEditingService(), new RecordingImagePreviewService());
        vm.LoadFile(srr);
        vm.SelectedTreeNode = vm.TreeRoots.Flatten()
            .First(n => n.Tag is SRRStoredFileBlock b && b.FileName == "lazy.nfo");

        // Still in Hex mode → no decode happened.
        Assert.Equal(string.Empty, vm.TextViewContent);
    }
}
