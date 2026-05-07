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
        var list = roots.Select(r => ToExport(r, includeChildren: true)).ToList();
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
        SRRHeaderBlock => "SRR Header",
        SRRStoredFileBlock => "SRR Stored File",
        SRROsoHashBlock => "SRR OSO Hash",
        SRRRarPaddingBlock => "SRR RAR Padding",
        SRRRarFileBlock => "SRR RAR File",
        SRRFile => "SRR Archive",
        RARDetailedBlock b => b.BlockType,
        SRSFileDataBlock => "SRS File Data",
        SRSTrackDataBlock => "SRS Track Data",
        SRSContainerChunk => "SRS Container Chunk",
        SRSFile => "SRS File",
        _ => null
    };

    private static (long? Offset, long? Length) RangeOf(object? tag) => tag switch
    {
        SRRHeaderBlock b => (b.BlockPosition, b.HeaderSize),
        SRRStoredFileBlock b => (b.BlockPosition, (long)b.HeaderSize + b.AddSize),
        SRROsoHashBlock b => (b.BlockPosition, b.HeaderSize),
        SRRRarPaddingBlock b => (b.BlockPosition, (long)b.HeaderSize + b.AddSize),
        SRRRarFileBlock b => (b.BlockPosition, (long)b.HeaderSize + b.AddSize),
        RARDetailedBlock b => (b.StartOffset, b.TotalSize),
        SRSFileDataBlock b => (b.BlockPosition, b.BlockSize),
        SRSTrackDataBlock b => (b.BlockPosition, b.BlockSize),
        SRSContainerChunk b => (b.BlockPosition, b.BlockSize),
        _ => (null, null)
    };
}
