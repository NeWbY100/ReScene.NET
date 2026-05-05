using System.Text.Json;
using System.Text.Json.Serialization;
using ReScene.NET.Models;
using ReScene.NET.ViewModels;
using ReScene.RAR;
using ReScene.SRR;
using ReScene.SRS;

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
        List<PropertyExportNode> list = roots.Select(r => ToExport(r, includeChildren: true)).ToList();
        string json = JsonSerializer.Serialize(list, _options);
        return File.WriteAllTextAsync(outputPath, json, ct);
    }

    private static PropertyExportNode ToExport(TreeNodeViewModel node, bool includeChildren)
    {
        var export = new PropertyExportNode
        {
            Text = node.Text,
            BlockType = BlockTypeOf(node.Tag)
        };

        (long? offset, long? length) = RangeOf(node.Tag);
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

    private static string? BlockTypeOf(object? tag) => tag switch
    {
        SrrHeaderBlock => "SRR Header",
        SrrStoredFileBlock => "SRR Stored File",
        SrrOsoHashBlock => "SRR OSO Hash",
        SrrRarPaddingBlock => "SRR RAR Padding",
        SrrRarFileBlock => "SRR RAR File",
        SRRFile => "SRR Archive",
        RARDetailedBlock b => b.BlockType,
        SrsFileDataBlock => "SRS File Data",
        SrsTrackDataBlock => "SRS Track Data",
        SrsContainerChunk => "SRS Container Chunk",
        SRSFile => "SRS File",
        _ => null
    };

    private static (long? Offset, long? Length) RangeOf(object? tag) => tag switch
    {
        SrrHeaderBlock b => (b.BlockPosition, b.HeaderSize),
        SrrStoredFileBlock b => (b.BlockPosition, (long)b.HeaderSize + b.AddSize),
        SrrOsoHashBlock b => (b.BlockPosition, b.HeaderSize),
        SrrRarPaddingBlock b => (b.BlockPosition, (long)b.HeaderSize + b.AddSize),
        SrrRarFileBlock b => (b.BlockPosition, (long)b.HeaderSize + b.AddSize),
        RARDetailedBlock b => (b.StartOffset, b.TotalSize),
        SrsFileDataBlock b => (b.BlockPosition, b.BlockSize),
        SrsTrackDataBlock b => (b.BlockPosition, b.BlockSize),
        SrsContainerChunk b => (b.BlockPosition, b.BlockSize),
        _ => (null, null)
    };
}
