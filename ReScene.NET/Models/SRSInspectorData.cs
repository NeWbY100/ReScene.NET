using ReScene.SRS;

namespace ReScene.NET.Models;

/// <summary>
/// Holds a parsed SRS file for the Inspector tab.
/// </summary>
public class SRSInspectorData
{
    public SRSFile SRSFile { get; set; } = null!;

    /// <summary>
    /// Loads and parses an SRS file from disk.
    /// </summary>
    /// <param name="filePath">
    /// Path to the SRS file.
    /// </param>
    /// <returns>
    /// A new <see cref="SRSInspectorData"/> wrapping the parsed file.
    /// </returns>
    public static SRSInspectorData Load(string filePath)
        => new()
        {
            SRSFile = SRSFile.Load(filePath)
        };
}
