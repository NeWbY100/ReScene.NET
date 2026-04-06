using ReScene.SRR;

namespace ReScene.NET.Services;

/// <summary>
/// Service wrapper around <see cref="SRREditor"/> for editing existing SRR files.
/// </summary>
public class SrrEditingService : ISrrEditingService
{
    /// <inheritdoc />
    public void AddStoredFiles(string srrFilePath, IReadOnlyList<(string StoredName, string FilePath)> files)
        => SRREditor.AddStoredFiles(srrFilePath, files);

    /// <inheritdoc />
    public void RemoveStoredFiles(string srrFilePath, IReadOnlyList<string> storedNames)
        => SRREditor.RemoveStoredFiles(srrFilePath, storedNames);
}
