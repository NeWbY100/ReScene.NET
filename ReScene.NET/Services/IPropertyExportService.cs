using ReScene.NET.ViewModels;

namespace ReScene.NET.Services;

/// <summary>
/// Exports Inspector tree nodes and their properties to JSON.
/// </summary>
public interface IPropertyExportService
{
    /// <summary>
    /// Exports a single node and its already-resolved properties to a JSON file.
    /// </summary>
    public Task ExportSelectedAsync(string outputPath, TreeNodeViewModel node, IEnumerable<PropertyItem> properties, CancellationToken ct = default);

    /// <summary>
    /// Exports the structural tree under the given roots to a JSON file (no properties).
    /// </summary>
    public Task ExportTreeAsync(string outputPath, IEnumerable<TreeNodeViewModel> roots, CancellationToken ct = default);
}
