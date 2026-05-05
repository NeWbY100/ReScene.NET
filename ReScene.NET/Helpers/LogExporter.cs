namespace ReScene.NET.Helpers;

/// <summary>
/// Writes log entries to a file, one entry per line, UTF-8 without BOM.
/// </summary>
internal static class LogExporter
{
    /// <summary>
    /// Writes the given entries to <paramref name="outputPath"/>, one per line.
    /// </summary>
    public static Task SaveAsync(IEnumerable<string> entries, string outputPath, CancellationToken ct = default)
        => File.WriteAllLinesAsync(outputPath, entries, ct);
}
