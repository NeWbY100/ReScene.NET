namespace ReScene.NET.Models;

/// <summary>
/// One name/value pair exported from the Inspector property grid.
/// </summary>
public sealed class PropertyExportEntry
{
    /// <summary>
    /// Gets or sets the property name as shown in the grid.
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the property value as shown in the grid.
    /// </summary>
    public string Value { get; set; } = string.Empty;
}
