using System.Text;
using ReScene.App.Core.Models;
using ReScene.App.Core.Services;
using ReScene.App.Core.ViewModels;
using ReScene.SRR;

namespace ReScene.App.Core.Tests;

/// <summary>
/// Regression tests for three Inspector defects that all came from treating
/// <c>LoadFileAsync</c> as a harmless "refresh" rather than the navigation primitive it is.
/// </summary>
/// <remarks>
/// <c>LoadFileAsync</c> bumps a generation counter and claims latest-writer-wins. Every SRR edit
/// ended with an unconditional <c>finally { await LoadFileAsync(path); }</c>, so an edit started
/// before the user navigated away finished LAST, made itself the newest generation, invalidated
/// the file the user had actually asked for, and pulled the Inspector back to the old one. The
/// same reload also writes <c>StatusMessage</c> on every path, so an outcome set before it was
/// never seen. Separately, <c>Dispose</c> did not advance that generation at all, so a load still
/// in flight when the Inspector closed passed its staleness check and built a fresh
/// memory-mapped source on a disposed view-model that nothing would ever release.
/// </remarks>
public sealed class InspectorViewModelEditSupersedeTests : TempDirTestBase
{
    /// <summary>Editing service whose rename completes only when the test says so.</summary>
    private sealed class GatedRenameEditingService : ISRREditingService
    {
        public TaskCompletionSource RenameGate { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task RenameStoredFileAsync(string srrPath, string oldName, string newName, CancellationToken ct = default)
            => RenameGate.Task;

        public void AddStoredFiles(string srrFilePath, IReadOnlyList<(string StoredName, string FilePath)> files) { }
        public void RemoveStoredFiles(string srrFilePath, IReadOnlyList<string> storedNames) { }
        public Task MoveStoredFileAsync(string srrPath, string storedName, int offset, CancellationToken ct = default) => Task.CompletedTask;
        public IReadOnlyList<StoredFileInfo> GetStoredFiles(string srrFilePath) => [];
        public Task<string?> ExtractStoredFileAsync(string srrFilePath, string outputDirectory, string storedName, CancellationToken ct = default) => Task.FromResult<string?>(null);
        public Task<byte[]?> ReadStoredFileBytesAsync(string srrFilePath, string storedName, CancellationToken ct = default) => Task.FromResult<byte[]?>(null);
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

    /// <summary>Answers the rename prompt with a fixed new name.</summary>
    private sealed class PromptReturnsNameDialog(string newName) : NoOpFileDialogService
    {
        public override Task<string?> PromptForTextAsync(string title, string prompt, string? initialValue = null)
            => Task.FromResult<string?>(newName);
    }

    private static void RunSync(Func<Task> action) => Task.Run(action).GetAwaiter().GetResult();

    private static InspectorViewModel NewViewModel(ISRREditingService editing, IFileDialogService dialog) =>
        new(dialog, editing, new StubVerifyService(), new StubPropertyExportService(), new RecordingImagePreviewService());

    private string WriteSrr(string name, string storedName) =>
        SRREditingServiceImageTests.WriteMinimalSRR(TempDir, name, storedName, Encoding.ASCII.GetBytes("DATA"));

    [Fact]
    public async Task RenameStoredFile_WhenAnotherFileIsOpenedMidEdit_DoesNotPullTheViewBack()
    {
        string first = WriteSrr("first.srr", "keep.nfo");
        string second = WriteSrr("second.srr", "other.nfo");

        var editing = new GatedRenameEditingService();
        InspectorViewModel vm = NewViewModel(editing, new PromptReturnsNameDialog("renamed.nfo"));

        RunSync(() => vm.LoadFileAsync(first));
        vm.SelectedTreeNode = FindStoredFileNode(vm);

        // Start the rename; it parks on the gate, exactly like a slow edit on a large SRR.
        Task rename = Task.Run(() => vm.RenameStoredFileCommand.ExecuteAsync(null));

        // The user navigates away while it is still running.
        RunSync(() => vm.LoadFileAsync(second));
        Assert.Equal(second, vm.LoadedFilePath);

        // Now the edit finishes. Its reload must NOT win.
        editing.RenameGate.SetResult();
        await rename;

        Assert.Equal(second, vm.LoadedFilePath);
    }

    [Fact]
    public void RenameStoredFile_WhenStillCurrent_ReportsItsOutcomeAfterTheReload()
    {
        string srr = WriteSrr("only.srr", "keep.nfo");

        var editing = new GatedRenameEditingService();
        editing.RenameGate.SetResult();

        InspectorViewModel vm = NewViewModel(editing, new PromptReturnsNameDialog("renamed.nfo"));
        RunSync(() => vm.LoadFileAsync(srr));
        vm.SelectedTreeNode = FindStoredFileNode(vm);

        RunSync(() => vm.RenameStoredFileCommand.ExecuteAsync(null));

        // The reload writes a file summary into StatusMessage on every path, so an outcome set
        // before it was invisible. Both the success confirmation and the error were lost.
        Assert.Contains("Renamed stored file", vm.StatusMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RenameStoredFile_WhenAnotherEditTouchedTheSameFile_StillReloadsAndReports()
    {
        // "Superseded" must mean the view moved to a DIFFERENT file. An earlier version compared
        // the generation counter, which another edit to the SAME file also bumps — so a slow edit
        // overlapping a fast one skipped its reload and left the tree showing the file as it was
        // BEFORE its write landed, with no confirmation that anything happened.
        string srr = WriteSrr("only.srr", "keep.nfo");

        var editing = new GatedRenameEditingService();
        InspectorViewModel vm = NewViewModel(editing, new PromptReturnsNameDialog("renamed.nfo"));

        RunSync(() => vm.LoadFileAsync(srr));
        vm.SelectedTreeNode = FindStoredFileNode(vm);

        Task rename = Task.Run(() => vm.RenameStoredFileCommand.ExecuteAsync(null));

        // A second edit on the SAME file completes first and reloads, bumping the generation.
        RunSync(() => vm.LoadFileAsync(srr));

        editing.RenameGate.SetResult();
        await rename;

        Assert.Equal(srr, vm.LoadedFilePath);
        Assert.Contains("Renamed stored file", vm.StatusMessage, StringComparison.Ordinal);
    }

    [Fact]
    public void LoadFile_AfterDispose_DoesNotInstallANewDataSource()
    {
        string srr = WriteSrr("only.srr", "keep.nfo");
        InspectorViewModel vm = NewViewModel(new GatedRenameEditingService(), new NoOpFileDialogService());

        vm.Dispose();
        RunSync(() => vm.LoadFileAsync(srr));

        // Before the fix this built a MemoryMappedDataSource that no later Dispose could release
        // (the second call returns early on _disposed), leaking the mapping and holding the file
        // open on Windows.
        Assert.False(vm.HasFile);
        Assert.Equal(string.Empty, vm.LoadedFilePath);
    }

    private static TreeNodeViewModel FindStoredFileNode(InspectorViewModel vm)
    {
        TreeNodeViewModel? found = Walk(vm.TreeRoots).FirstOrDefault(n => n.Tag is SRRStoredFileBlock);
        Assert.NotNull(found);
        return found;

        static IEnumerable<TreeNodeViewModel> Walk(IEnumerable<TreeNodeViewModel> nodes)
        {
            foreach (TreeNodeViewModel node in nodes)
            {
                yield return node;
                foreach (TreeNodeViewModel child in Walk(node.Children))
                {
                    yield return child;
                }
            }
        }
    }
}
