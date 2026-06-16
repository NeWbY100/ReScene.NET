using ReScene.NET.ViewModels;
using ReScene.SRS;

namespace ReScene.NET.Helpers;

/// <summary>
/// Builds the nested tree of SRS container chunks shared by the Inspector and file-compare
/// view-models. The two callers attach different <see cref="TreeNodeViewModel.Tag"/> payloads
/// (the inspector tags the chunk itself; the comparer wraps it in a <c>CompareNodeData</c>), so the
/// tag is supplied per node via the <c>tagFactory</c> callback. The nesting, labels, and stack logic
/// are otherwise identical to the previous inline copies.
/// </summary>
internal static class SrsChunkHierarchy
{
    /// <summary>Builds the nested chunk tree under <paramref name="root"/>, tagging each node via <paramref name="tagFactory"/>.</summary>
    public static void Build(TreeNodeViewModel root, IReadOnlyList<SRSContainerChunk> chunks, Func<SRSContainerChunk, object> tagFactory)
    {
        var nodeStack = new Stack<TreeNodeViewModel>();
        var endStack = new Stack<long>();
        nodeStack.Push(root);
        endStack.Push(long.MaxValue);

        foreach (SRSContainerChunk chunk in chunks)
        {
            long chunkEnd = chunk.BlockPosition + chunk.BlockSize;

            while (endStack.Count > 1 && chunk.BlockPosition >= endStack.Peek())
            {
                nodeStack.Pop();
                endStack.Pop();
            }

            var node = new TreeNodeViewModel
            {
                Text = $"{chunk.Label} (0x{chunk.BlockPosition:X}, {FormatUtilities.FormatSize(chunk.BlockSize)})",
                Tag = tagFactory(chunk)
            };
            nodeStack.Peek().Children.Add(node);

            nodeStack.Push(node);
            endStack.Push(chunkEnd);
        }
    }
}
