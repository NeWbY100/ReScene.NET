using System.Text;
using ReScene.App.Core.Models;
using ReScene.App.Core.Services;
using ReScene.App.Core.ViewModels;
using ReScene.Hex;
using ReScene.SRR;
namespace ReScene.App.Core.Tests;

public class InspectorViewModelImageTests : TempDirTestBase
{
    // Editing service that only serves ReadStoredFileBytesAsync; other members are unused here.
    private sealed class FakeReadEditingService : ISRREditingService
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

    private sealed class StubVerifyService : ISRRVerifyService
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

    // Drives the now-async InspectorViewModel.LoadFileAsync synchronously for tests. Wrapping in
    // Task.Run detaches from any ambient synchronization context, so the internal `await Task.Run`
    // continuation runs on the thread pool and GetResult cannot deadlock.
    private static void LoadInspector(InspectorViewModel vm, string path)
        => Task.Run(() => vm.LoadFileAsync(path)).GetAwaiter().GetResult();

    private InspectorViewModel LoadWithStored(string storedName, FakeReadEditingService editing, RecordingImagePreviewService preview)
    {
        string srr = SRREditingServiceImageTests.WriteMinimalSRR(TempDir, "inspect.srr", storedName, [0x00]);
        InspectorViewModel vm = CreateVm(editing, preview);
        LoadInspector(vm, srr);
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
    public async Task ExportBlock_StoredFile_WritesPayloadWithoutSRRHeader()
    {
        // A distinctive payload so we can prove only it (not the wrapping SRR block header) is written.
        byte[] payload = [0x66, 0x4C, 0x61, 0x43, 0x73, 0x00, 0x01, 0x02]; // "fLaCs"…
        string srr = SRREditingServiceImageTests.WriteMinimalSRR(TempDir, "wrap.srr", "song.srs", payload);
        string outPath = Path.Combine(TempDir, "exported.srs");

        using InspectorViewModel vm = new(
            new SaveToPathDialog(outPath), new FakeReadEditingService(),
            new StubVerifyService(), new StubPropertyExportService(), new RecordingImagePreviewService());
        LoadInspector(vm, srr);
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

        LoadInspector(vm, bad);

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
        string srr = SRREditingServiceImageTests.WriteMinimalSRR(TempDir, "note.srr", "note.nfo", payload);

        using InspectorViewModel vm = CreateVm(new FakeReadEditingService(), new RecordingImagePreviewService());
        LoadInspector(vm, srr);
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
        string srr = SRREditingServiceImageTests.WriteMinimalSRR(TempDir, "enc.srr", "enc.bin", payload);

        using InspectorViewModel vm = CreateVm(new FakeReadEditingService(), new RecordingImagePreviewService());
        LoadInspector(vm, srr);
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
        string srr = SRREditingServiceImageTests.WriteMinimalSRR(TempDir, "lazy.srr", "lazy.nfo", payload);

        using InspectorViewModel vm = CreateVm(new FakeReadEditingService(), new RecordingImagePreviewService());
        LoadInspector(vm, srr);
        vm.SelectedTreeNode = vm.TreeRoots.Flatten()
            .First(n => n.Tag is SRRStoredFileBlock b && b.FileName == "lazy.nfo");

        // Still in Hex mode → no decode happened.
        Assert.Equal(string.Empty, vm.TextViewContent);
    }

    [Fact]
    public void LoadFile_SecondFileWhileTextActive_DoesNotFailFromDisposedSource()
    {
        byte[] a = Encoding.ASCII.GetBytes("AAA_FILE_ONE");
        byte[] b = Encoding.ASCII.GetBytes("BBB_FILE_TWO");
        string srrA = SRREditingServiceImageTests.WriteMinimalSRR(TempDir, "first.srr", "a.nfo", a);
        string srrB = SRREditingServiceImageTests.WriteMinimalSRR(TempDir, "second.srr", "b.nfo", b);

        using InspectorViewModel vm = CreateVm(new FakeReadEditingService(), new RecordingImagePreviewService());
        LoadInspector(vm, srrA);
        vm.SelectedTreeNode = vm.TreeRoots.Flatten()
            .First(n => n.Tag is SRRStoredFileBlock s && s.FileName == "a.nfo");
        vm.IsTextViewActive = true;
        Assert.Contains("AAA_FILE_ONE", vm.TextViewContent, StringComparison.Ordinal);

        // Opening a second valid file while the Text view is active must not read the now-disposed
        // data source of the first file (which would throw and be reported as a load failure).
        LoadInspector(vm, srrB);

        Assert.True(vm.HasFile, $"second load failed; status='{vm.StatusMessage}'");

        // And the Text view tracks the new file once a node is selected.
        vm.SelectedTreeNode = vm.TreeRoots.Flatten()
            .First(n => n.Tag is SRRStoredFileBlock s && s.FileName == "b.nfo");
        Assert.Contains("BBB_FILE_TWO", vm.TextViewContent, StringComparison.Ordinal);
    }

    [Fact]
    public void ShowHexSearch_FromTextTab_SwitchesToHexAndShowsSearch()
    {
        using InspectorViewModel vm = CreateVm(new FakeReadEditingService(), new RecordingImagePreviewService());
        vm.IsTextViewActive = true;

        vm.ShowHexSearchCommand.Execute(null);

        // Search lives in the Hex tab, so invoking it must switch back to Hex and reveal the bar.
        Assert.False(vm.IsTextViewActive);
        Assert.True(vm.IsHexSearchVisible);
    }

    [Fact]
    public void HexSearch_InsideSubBlock_ReportsAbsoluteFileOffset()
    {
        // A stored-file block never starts at file offset 0 (the SRR header precedes it), so its
        // hex slice has BlockOffset > 0. A pattern in the payload must resolve to the TRUE absolute
        // file offset — not the slice-relative one — so the address column, status bar, highlight
        // ranges, and Export all stay coordinate-consistent.
        byte[] payload = [0x11, 0x22, 0xDE, 0xAD, 0xBE, 0xEF, 0x33];
        string srr = SRREditingServiceImageTests.WriteMinimalSRR(TempDir, "search.srr", "data.bin", payload);

        using InspectorViewModel vm = CreateVm(new FakeReadEditingService(), new RecordingImagePreviewService());
        LoadInspector(vm, srr);

        TreeNodeViewModel node = vm.TreeRoots.Flatten()
            .First(n => n.Tag is SRRStoredFileBlock b && b.FileName == "data.bin");
        vm.SelectedTreeNode = node;

        var stored = (SRRStoredFileBlock)node.Tag!;
        Assert.True(vm.HexBlockOffset > 0, "sub-block slice must be based past file offset 0");

        // The pattern sits at payload index 2 → absolute offset is DataOffset + 2.
        long expected = stored.DataOffset + 2;

        vm.HighlightAllMatches = true;
        vm.HexSearchText = "DEADBEEF"; // hex mode is the default
        vm.FindNextCommand.Execute(null);

        Assert.Equal(expected, vm.HexSelectionOffset);
        // The block base must NOT be clobbered to 0 (which would make Export read from file offset 0).
        Assert.Equal(stored.BlockPosition, vm.HexBlockOffset);
        Assert.Contains(expected.ToString("X"), vm.HexSearchStatus, StringComparison.Ordinal);

        // Highlight-all ranges are also rebased to absolute file offsets.
        HexMatchRange range = Assert.Single(vm.HexMatchRanges!);
        Assert.Equal(expected, range.Offset);
    }
}
