using SRRLib;

namespace ReScene.NET.Models;

/// <summary>
/// Holds a parsed SRS file for the Inspector tab.
/// </summary>
public class SrsInspectorData
{
    public SRSFile SrsFile { get; set; } = null!;

    public static SrsInspectorData Load(string filePath)
        => new() { SrsFile = SRSFile.Load(filePath) };
}
