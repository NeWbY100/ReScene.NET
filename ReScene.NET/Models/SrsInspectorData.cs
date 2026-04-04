using ReScene.SRS;

namespace ReScene.NET.Models;

/// <summary>
/// Holds a parsed SRS file for the Inspector tab.
/// </summary>
public class SrsInspectorData
{
    public SRSFile SrsFile { get; set; } = null!;

    /// <summary>
    /// Loads and parses an SRS file from disk.
    /// </summary>
    /// <param name="filePath">Path to the SRS file.</param>
    /// <returns>A new <see cref="SrsInspectorData"/> wrapping the parsed file.</returns>
    public static SrsInspectorData Load(string filePath)
        => new()
        {
            SrsFile = SRSFile.Load(filePath)
        };
}
