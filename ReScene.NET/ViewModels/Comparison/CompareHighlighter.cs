using System.Collections.ObjectModel;
using ReScene.Core.Comparison;
using ReScene.NET.Services;
using ReScene.RAR;

namespace ReScene.NET.ViewModels.Comparison;

/// <summary>
/// Applies the side-by-side comparison annotations to the already-built structure trees: the
/// <c>[DIFF]</c>/<c>[NEW]</c>/<c>[REMOVED]</c> text markers, the <c>IsDifferent</c> flags, the
/// "exists on only one side" marking, and the bubble-up of differences to parent nodes. The
/// comparison state (<see cref="CompareResult"/>, both sides' detailed RAR blocks and mapped file
/// sources) is reassigned on every load/close/swap, so it is passed to <see cref="Apply"/> per call
/// rather than captured. Behavior — including the exact marker strings — is preserved verbatim.
/// </summary>
internal static class CompareHighlighter
{
    public static void Apply(
        CompareResult? compareResult,
        ObservableCollection<TreeNodeViewModel> leftRoots,
        ObservableCollection<TreeNodeViewModel> rightRoots,
        IReadOnlyList<RARDetailedBlock>? leftDetailedBlocks,
        IReadOnlyList<RARDetailedBlock>? rightDetailedBlocks,
        MemoryMappedDataSource? leftFileSource,
        MemoryMappedDataSource? rightFileSource)
    {
        if (compareResult is null)
        {
            return;
        }

        var addedFiles = compareResult.FileDifferences
            .Where(d => d.Type == DifferenceType.Added)
            .Select(d => d.FileName)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var removedFiles = compareResult.FileDifferences
            .Where(d => d.Type == DifferenceType.Removed)
            .Select(d => d.FileName)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var modifiedFiles = compareResult.FileDifferences
            .Where(d => d.Type == DifferenceType.Modified)
            .Select(d => d.FileName)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var addedStoredFiles = compareResult.StoredFileDifferences
            .Where(d => d.Type == DifferenceType.Added)
            .Select(d => d.FileName)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var removedStoredFiles = compareResult.StoredFileDifferences
            .Where(d => d.Type == DifferenceType.Removed)
            .Select(d => d.FileName)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        // Apply text annotations to tree nodes
        foreach (TreeNodeViewModel root in leftRoots)
        {
            ApplyNodeHighlighting(root, removedFiles, addedFiles, modifiedFiles, removedStoredFiles, addedStoredFiles, true,
                compareResult, leftDetailedBlocks, rightDetailedBlocks, leftFileSource, rightFileSource);
        }

        foreach (TreeNodeViewModel root in rightRoots)
        {
            ApplyNodeHighlighting(root, addedFiles, removedFiles, modifiedFiles, addedStoredFiles, removedStoredFiles, false,
                compareResult, leftDetailedBlocks, rightDetailedBlocks, leftFileSource, rightFileSource);
        }

        // Mark sections that exist on only one side
        MarkUniqueSections(leftRoots, rightRoots);
        MarkUniqueSections(rightRoots, leftRoots);
    }

    private static void MarkUniqueSections(ObservableCollection<TreeNodeViewModel> roots, ObservableCollection<TreeNodeViewModel> otherRoots)
    {
        var otherNodeTypes = new HashSet<CompareNodeType>();
        foreach (TreeNodeViewModel root in otherRoots)
        {
            foreach (TreeNodeViewModel child in root.Children)
            {
                if (child.Tag is CompareNodeData d)
                {
                    otherNodeTypes.Add(d.NodeType);
                }
            }
        }

        foreach (TreeNodeViewModel root in roots)
        {
            foreach (TreeNodeViewModel child in root.Children)
            {
                if (child.Tag is CompareNodeData d && !otherNodeTypes.Contains(d.NodeType))
                {
                    MarkNodeAndChildren(child);
                }
            }
        }
    }

    private static void MarkNodeAndChildren(TreeNodeViewModel node)
    {
        node.IsDifferent = true;
        foreach (TreeNodeViewModel child in node.Children)
        {
            MarkNodeAndChildren(child);
        }
    }

    private static void ApplyNodeHighlighting(TreeNodeViewModel node, HashSet<string> removed, HashSet<string> added,
        HashSet<string> modified, HashSet<string> storedRemoved, HashSet<string> storedAdded, bool isLeft,
        CompareResult? compareResult,
        IReadOnlyList<RARDetailedBlock>? leftDetailedBlocks,
        IReadOnlyList<RARDetailedBlock>? rightDetailedBlocks,
        MemoryMappedDataSource? leftFileSource,
        MemoryMappedDataSource? rightFileSource)
    {
        if (node.Tag is CompareNodeData data)
        {
            if (data.NodeType is CompareNodeType.ArchiveInfo or CompareNodeType.SRSFileInfo
                && compareResult?.ArchiveDifferences.Count > 0)
            {
                node.Text = $"{GetBaseNodeText(node.Text)} [DIFF]";
                node.IsDifferent = true;
            }
            else if (data.NodeType == CompareNodeType.DetailedBlock && data.Data is RARDetailedBlock block)
            {
                IReadOnlyList<RARDetailedBlock>? otherBlocks = isLeft ? rightDetailedBlocks : leftDetailedBlocks;
                if (otherBlocks is not null)
                {
                    RARDetailedBlock? otherBlock = otherBlocks.FirstOrDefault(b =>
                        b.BlockType == block.BlockType && b.ItemName == block.ItemName);
                    if (otherBlock is not null
                        && FileComparer.HasBlockDifferences(block, otherBlock, leftFileSource, rightFileSource))
                    {
                        node.Text = $"{GetBaseNodeText(node.Text)} [DIFF]";
                        node.IsDifferent = true;
                    }
                }
            }
            else if (data.NodeType is CompareNodeType.SRSTrack or CompareNodeType.MKVElement && data.FileName is not null)
            {
                if (removed.Contains(data.FileName))
                {
                    node.Text = isLeft ? $"{GetBaseNodeText(node.Text)} [REMOVED]" : $"{GetBaseNodeText(node.Text)} [NEW]";
                    node.IsDifferent = true;
                }
                else if (modified.Contains(data.FileName))
                {
                    node.Text = $"{GetBaseNodeText(node.Text)} [DIFF]";
                    node.IsDifferent = true;
                }
            }
            else if (data.NodeType is CompareNodeType.ArchivedFile or CompareNodeType.StoredFile)
            {
                string fileName = data.FileName ?? "";

                if (data.NodeType == CompareNodeType.StoredFile)
                {
                    if (storedRemoved.Contains(fileName))
                    {
                        node.Text = isLeft ? $"{GetBaseNodeText(node.Text)} [REMOVED]" : $"{GetBaseNodeText(node.Text)} [NEW]";
                        node.IsDifferent = true;
                    }
                }
                else
                {
                    if (removed.Contains(fileName))
                    {
                        node.Text = isLeft ? $"{GetBaseNodeText(node.Text)} [REMOVED]" : $"{GetBaseNodeText(node.Text)} [NEW]";
                        node.IsDifferent = true;
                    }
                    else if (modified.Contains(fileName))
                    {
                        node.Text = $"{GetBaseNodeText(node.Text)} [DIFF]";
                        node.IsDifferent = true;
                    }
                }
            }
        }

        foreach (TreeNodeViewModel child in node.Children)
        {
            ApplyNodeHighlighting(child, removed, added, modified, storedRemoved, storedAdded, isLeft,
                compareResult, leftDetailedBlocks, rightDetailedBlocks, leftFileSource, rightFileSource);
        }

        // Bubble up: mark parent as different if any child has differences
        if (!node.IsDifferent && node.Children.Any(c => c.IsDifferent))
        {
            node.IsDifferent = true;
        }
    }

    private static string GetBaseNodeText(string text)
    {
        int bracketIndex = text.LastIndexOf(" [", StringComparison.Ordinal);
        if (bracketIndex > 0 && (text.EndsWith("[REMOVED]", StringComparison.Ordinal) || text.EndsWith("[NEW]", StringComparison.Ordinal) || text.EndsWith("[DIFF]", StringComparison.Ordinal)))
        {
            return text[..bracketIndex];
        }

        return text;
    }
}
