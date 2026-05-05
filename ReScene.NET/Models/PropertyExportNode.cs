namespace ReScene.NET.Models;

/// <summary>
/// A serializable snapshot of an Inspector tree node and its descendants.
/// </summary>
public sealed class PropertyExportNode
{
    /// <summary>
    /// Gets or sets the display text of the tree node.
    /// </summary>
    public string Text { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the block type label (e.g. "SRR Header", "RAR File"), or null.
    /// </summary>
    public string? BlockType { get; set; }

    /// <summary>
    /// Gets or sets the byte offset of the block within the file, or null.
    /// </summary>
    public long? Offset { get; set; }

    /// <summary>
    /// Gets or sets the byte length of the block, or null.
    /// </summary>
    public long? Length { get; set; }

    /// <summary>
    /// Gets or sets the property entries to include with this node (selected-node export only).
    /// </summary>
    public List<PropertyExportEntry> Properties { get; set; } = [];

    /// <summary>
    /// Gets or sets the child nodes (entire-tree export only).
    /// </summary>
    public List<PropertyExportNode> Children { get; set; } = [];
}
