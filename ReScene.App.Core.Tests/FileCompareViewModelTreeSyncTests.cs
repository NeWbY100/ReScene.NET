using ReScene.App.Core.Services;
using ReScene.App.Core.ViewModels;
using ReScene.App.Core.ViewModels.Comparison;
using ReScene.Core.Comparison;
using ReScene.Hex;
using ReScene.RAR;

namespace ReScene.App.Core.Tests;

/// <summary>
/// Regression tests for the Compare tab's cross-tree selection sync. The RAR fallback tree tags its
/// structural placeholder blocks (Signature, Service Block, End Archive) with
/// <c>NodeType == Root</c> and a null <c>FileName</c> — the same identity as the real root. The
/// matcher's fallback collapsed any such node onto the opposite ROOT (found first), mis-highlighting
/// it and clearing its property grid. These pin the corrected behavior: a placeholder syncs to its
/// peer placeholder, the root still syncs to the root even when block counts (embedded in the label)
/// differ.
/// </summary>
public sealed class FileCompareViewModelTreeSyncTests
{
    private sealed class InertCompareService : IFileCompareService
    {
        public object? LoadFileData(string filePath) => null;
        public IReadOnlyList<RARDetailedBlock>? ParseDetailedBlocks(string filePath) => null;
        public CompareResult Compare(object? leftData, object? rightData,
            IReadOnlyList<RARDetailedBlock>? leftBlocks = null, IReadOnlyList<RARDetailedBlock>? rightBlocks = null,
            IHexDataSource? leftSource = null, IHexDataSource? rightSource = null) => new();
    }

    private sealed class InertHexDiffComputer : IHexDiffComputer
    {
        public Task<HexDiffResult> ComputeAsync(
            IHexDataSource leftSource, long leftOffset, long leftLength,
            IHexDataSource rightSource, long rightOffset, long rightLength,
            IProgress<HexDiffProgress>? progress, CancellationToken ct) => Task.FromResult(new HexDiffResult([], []));
    }

    private static FileCompareViewModel CreateVm() =>
        new(new InertCompareService(), new NoOpFileDialogService(), new InertHexDiffComputer(), new TestUiDispatcher());

    // Mirrors FileCompareTreeBuilder.BuildRAR's fallback shape: a data-carrying root plus null-Data,
    // null-FileName Root placeholders. The root's label embeds a block count so differing counts can
    // be exercised.
    private static (TreeNodeViewModel root, TreeNodeViewModel signature, TreeNodeViewModel endArchive)
        BuildRarFallbackTree(bool isLeft, int blockCount)
    {
        var root = new TreeNodeViewModel
        {
            Text = $"RAR 4.x Archive (~{blockCount} blocks)",
            // Non-null Data marks the true root (BuildRAR passes the RARFileData here).
            Tag = new CompareNodeData { NodeType = CompareNodeType.Root, Data = new object(), IsLeft = isLeft },
        };
        var signature = new TreeNodeViewModel
        {
            Text = "[0] Signature",
            Tag = new CompareNodeData { NodeType = CompareNodeType.Root, IsLeft = isLeft }, // Data == null
        };
        var archiveHeader = new TreeNodeViewModel
        {
            Text = "[1] Archive Header",
            Tag = new CompareNodeData { NodeType = CompareNodeType.ArchiveInfo, Data = new object(), IsLeft = isLeft },
        };
        var endArchive = new TreeNodeViewModel
        {
            Text = "[2] End Archive",
            Tag = new CompareNodeData { NodeType = CompareNodeType.Root, IsLeft = isLeft }, // Data == null
        };
        root.Children.Add(signature);
        root.Children.Add(archiveHeader);
        root.Children.Add(endArchive);
        return (root, signature, endArchive);
    }

    [Fact]
    public void SelectingRarEndArchivePlaceholder_SyncsMatchingPlaceholder_NotTheRoot()
    {
        using FileCompareViewModel vm = CreateVm();
        (TreeNodeViewModel leftRoot, _, TreeNodeViewModel leftEnd) = BuildRarFallbackTree(isLeft: true, blockCount: 3);
        (TreeNodeViewModel rightRoot, _, TreeNodeViewModel rightEnd) = BuildRarFallbackTree(isLeft: false, blockCount: 3);
        vm.LeftTreeRoots.Add(leftRoot);
        vm.RightTreeRoots.Add(rightRoot);

        vm.SelectedLeftTreeNode = leftEnd;

        Assert.Same(rightEnd, vm.SelectedRightTreeNode);
        Assert.NotSame(rightRoot, vm.SelectedRightTreeNode);
    }

    [Fact]
    public void SelectingRarSignaturePlaceholder_SyncsRightSignature()
    {
        using FileCompareViewModel vm = CreateVm();
        (TreeNodeViewModel leftRoot, TreeNodeViewModel leftSig, _) = BuildRarFallbackTree(isLeft: true, blockCount: 3);
        (TreeNodeViewModel rightRoot, TreeNodeViewModel rightSig, _) = BuildRarFallbackTree(isLeft: false, blockCount: 3);
        vm.LeftTreeRoots.Add(leftRoot);
        vm.RightTreeRoots.Add(rightRoot);

        vm.SelectedLeftTreeNode = leftSig;

        Assert.Same(rightSig, vm.SelectedRightTreeNode);
        Assert.NotSame(rightRoot, vm.SelectedRightTreeNode);
    }

    [Fact]
    public void SelectingRarRoot_StillSyncsRoot_EvenWhenBlockCountsDiffer()
    {
        // The root label embeds the block count, so a differing count must not defeat root↔root sync.
        using FileCompareViewModel vm = CreateVm();
        (TreeNodeViewModel leftRoot, _, _) = BuildRarFallbackTree(isLeft: true, blockCount: 3);
        (TreeNodeViewModel rightRoot, _, _) = BuildRarFallbackTree(isLeft: false, blockCount: 4);
        vm.LeftTreeRoots.Add(leftRoot);
        vm.RightTreeRoots.Add(rightRoot);

        vm.SelectedLeftTreeNode = leftRoot;

        Assert.Same(rightRoot, vm.SelectedRightTreeNode);
    }

    // Produces trivially-shaped detailed blocks so BuildDetailed's root label embeds a differing count.
    private static IReadOnlyList<RARDetailedBlock> MakeDetailedBlocks(int count)
    {
        var blocks = new List<RARDetailedBlock>();
        for (int i = 0; i < count; i++)
        {
            blocks.Add(new RARDetailedBlock { BlockType = "File", BlockTypeValue = 0x74, ItemName = $"file{i}.rar", HasData = true });
        }

        return blocks;
    }

    [Fact]
    public void SelectingDetailedRarRoot_StillSyncsRoot_EvenWhenBlockCountsDiffer()
    {
        // Regression guard for the PRIMARY detailed-RAR path (FileCompareTreeBuilder.BuildDetailed),
        // exercised through the REAL builder — not the fallback shape. Its root Text embeds the block
        // count ("RAR 4.x Archive (N blocks)"); the root must carry Data (= the block list) so the
        // tree-sync matcher treats it as data-carrying (root↔root), NOT as a null-Data placeholder that
        // only matches on identical labels. Removing that Data would make two differently-sized RARs
        // stop syncing at the root — the regression both reviews caught.
        using FileCompareViewModel vm = CreateVm();
        TreeNodeViewModel leftRoot = FileCompareTreeBuilder.BuildDetailed(MakeDetailedBlocks(3), isLeft: true);
        TreeNodeViewModel rightRoot = FileCompareTreeBuilder.BuildDetailed(MakeDetailedBlocks(4), isLeft: false);
        vm.LeftTreeRoots.Add(leftRoot);
        vm.RightTreeRoots.Add(rightRoot);

        vm.SelectedLeftTreeNode = leftRoot;

        Assert.Same(rightRoot, vm.SelectedRightTreeNode);
    }
}
