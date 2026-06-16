using System.Text.Json;
using System.Text.Json.Serialization;
using ReScene.NET.Models;
using ReScene.NET.ViewModels;

namespace ReScene.NET.Services;

/// <summary>
/// Exports Inspector tree nodes and their properties to JSON using System.Text.Json.
/// </summary>
public sealed class PropertyExportService : IPropertyExportService
{
    private static readonly JsonSerializerOptions _options = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    /// <inheritdoc />
    public Task ExportSelectedAsync(string outputPath, TreeNodeViewModel node, IEnumerable<PropertyItem> properties, CancellationToken ct = default)
    {
        PropertyExportNode export = ToExport(node, includeChildren: false);
        var entries = properties
            .Where(p => !p.IsSeparator)
            .Select(p => new PropertyExportEntry { Name = p.Name, Value = p.Value })
            .ToList();
        if (entries.Count > 0)
        {
            export.Properties = entries;
        }
        string json = JsonSerializer.Serialize(export, _options);
        return File.WriteAllTextAsync(outputPath, json, ct);
    }

    /// <inheritdoc />
    public Task ExportTreeAsync(string outputPath, IEnumerable<TreeNodeViewModel> roots, CancellationToken ct = default)
    {
        var list = roots.Select(r => ToExport(r, includeChildren: true)).ToList();
        string json = JsonSerializer.Serialize(list, _options);
        return File.WriteAllTextAsync(outputPath, json, ct);
    }

    private static PropertyExportNode ToExport(TreeNodeViewModel node, bool includeChildren)
    {
        var export = new PropertyExportNode
        {
            Text = node.Text,
            BlockType = BlockMetadata.BlockTypeOf(node.Tag)
        };

        (long? offset, long? length) = BlockMetadata.RangeOf(node.Tag);
        export.Offset = offset;
        export.Length = length;

        if (includeChildren && node.Children.Count > 0)
        {
            List<PropertyExportNode> children = [];
            foreach (TreeNodeViewModel child in node.Children)
            {
                children.Add(ToExport(child, includeChildren: true));
            }
            export.Children = children;
        }

        return export;
    }
}
