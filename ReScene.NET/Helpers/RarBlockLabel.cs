using ReScene.RAR;

namespace ReScene.NET.Helpers;

/// <summary>
/// Shared formatting for RAR detailed-block tree nodes: the <c>[index] Type[: ItemName]</c> label
/// and the RAR5 signature detection. Both the Inspector and the file-compare view-models built
/// these inline with copy-pasted logic (including the literal RAR5 marker bytes); centralizing
/// them keeps the output byte-for-byte identical while removing the duplication.
/// </summary>
internal static class RarBlockLabel
{
    /// <summary>The RAR 5.x marker prefix as it appears in a Signature block's first field value.</summary>
    private const string Rar5SignaturePrefix = "52 61 72 21 1A 07 01";

    /// <summary>
    /// Formats a detailed RAR block as a tree-node label, matching the previous inline logic:
    /// <c>[index] File Data</c> for blocks that carry file data, otherwise <c>[index] {BlockType}</c>,
    /// with <c>: {ItemName}</c> appended when the block names an item.
    /// </summary>
    public static string FormatBlockLabel(int index, RARDetailedBlock block)
    {
        string blockType = block.HasData && block.BlockType.Contains("File", StringComparison.Ordinal)
            ? "File Data"
            : block.BlockType;
        string blockLabel = $"[{index}] {blockType}";

        if (!string.IsNullOrEmpty(block.ItemName))
        {
            blockLabel = $"[{index}] {blockType}: {block.ItemName}";
        }

        return blockLabel;
    }

    /// <summary>
    /// Returns true when the detailed blocks describe a RAR 5.x archive: a leading Signature block
    /// whose first field value starts with the RAR5 marker bytes.
    /// </summary>
    public static bool IsRar5Signature(IReadOnlyList<RARDetailedBlock> blocks)
    {
        return blocks.Count > 0
            && blocks[0].BlockType == "Signature"
            && blocks[0].Fields.Count > 0
            && blocks[0].Fields[0].Value.StartsWith(Rar5SignaturePrefix, StringComparison.Ordinal);
    }
}
