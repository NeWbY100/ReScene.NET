using ReScene.App.Core.Services;
using ReScene.App.Core.ViewModels;
using ReScene.Core.Comparison;
using ReScene.Hex;
using ReScene.RAR;

namespace ReScene.App.Core.Tests;

/// <summary>
/// Tests for <see cref="FileCompareViewModel.CloseAllAsync"/> — the shell invokes it when the
/// user leaves the Compare tab or switches mode, so it must clear both panes' visible state
/// AND release the memory-mapped handles (OS-level locks) on the compared files.
/// </summary>
public class FileCompareViewModelCloseAllTests : TempDirTestBase
{
    private sealed class StubHexDiffComputer : IHexDiffComputer
    {
        public Task<HexDiffResult> ComputeAsync(
            IHexDataSource leftSource, long leftOffset, long leftLength,
            IHexDataSource rightSource, long rightOffset, long rightLength,
            IProgress<HexDiffProgress>? progress, CancellationToken ct) =>
            Task.FromResult(new HexDiffResult([], []));
    }

    private sealed class NullCompareService : IFileCompareService
    {
        public object? LoadFileData(string filePath) => null;

        public IReadOnlyList<RARDetailedBlock>? ParseDetailedBlocks(string filePath) => [];

        public CompareResult Compare(object? left, object? right,
            IReadOnlyList<RARDetailedBlock>? leftBlocks = null, IReadOnlyList<RARDetailedBlock>? rightBlocks = null,
            IHexDataSource? leftSource = null, IHexDataSource? rightSource = null) => new();
    }

    private string WriteTempFile(string name)
    {
        string path = Path.Combine(TempDir, name);
        File.WriteAllBytes(path, [0x69, 0x69, 0x69, 0x01, 0x00, 0x2A, 0x2A, 0x2A]);
        return path;
    }

    [Fact]
    public async Task CloseAllAsync_ClearsBothPanes_AndReleasesFileLocks()
    {
        string left = WriteTempFile("left.bin");
        string right = WriteTempFile("right.bin");

        using var vm = new FileCompareViewModel(
            new NullCompareService(), new NoOpFileDialogService(),
            new StubHexDiffComputer(), new TestUiDispatcher());

        await vm.LoadLeftFileAsync(left);
        await vm.LoadRightFileAsync(right);

        Assert.Equal(left, vm.LeftFilePath);
        Assert.Equal(right, vm.RightFilePath);

        // The loaded panes memory-map the files: on Windows, deleting must fail while they are open,
        // otherwise the release assertion below would prove nothing. POSIX has no mandatory sharing
        // lock — unlinking a mapped file always succeeds there — so the probe is Windows-only, and
        // running it elsewhere would delete the file this test still needs.
        if (OperatingSystem.IsWindows())
        {
            bool lockedWhileLoaded = true;
            try
            {
                File.Delete(left);
                lockedWhileLoaded = false;
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            Assert.True(lockedWhileLoaded, "expected the loaded left file to be locked");
        }

        await vm.CloseAllAsync();

        Assert.Equal(string.Empty, vm.LeftFilePath);
        Assert.Equal(string.Empty, vm.RightFilePath);
        Assert.Null(vm.LeftHexDataSource);
        Assert.Null(vm.RightHexDataSource);

        // The locks must be gone: both files delete cleanly now.
        File.Delete(left);
        File.Delete(right);
    }

    [Fact]
    public async Task CloseAllAsync_NothingLoaded_IsNoOp()
    {
        using var vm = new FileCompareViewModel(
            new NullCompareService(), new NoOpFileDialogService(),
            new StubHexDiffComputer(), new TestUiDispatcher());

        await vm.CloseAllAsync();

        Assert.Equal(string.Empty, vm.LeftFilePath);
        Assert.Equal(string.Empty, vm.RightFilePath);
    }
}
