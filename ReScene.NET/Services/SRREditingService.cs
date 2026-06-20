using ReScene.NET.Models;
using ReScene.SRR;

namespace ReScene.NET.Services;

/// <summary>
/// Service wrapper around <see cref="SRREditor"/> for editing existing SRR files.
/// </summary>
public class SRREditingService : ISrrEditingService
{
    /// <inheritdoc />
    public void AddStoredFiles(string srrFilePath, IReadOnlyList<(string StoredName, string FilePath)> files)
        => SRREditor.AddStoredFiles(srrFilePath, files);

    /// <inheritdoc />
    public void RemoveStoredFiles(string srrFilePath, IReadOnlyList<string> storedNames)
        => SRREditor.RemoveStoredFiles(srrFilePath, storedNames);

    /// <inheritdoc />
    public Task RenameStoredFileAsync(string srrPath, string oldName, string newName, CancellationToken ct = default)
        => Task.Run(() => SRREditor.RenameStoredFile(srrPath, oldName, newName), ct);

    /// <inheritdoc />
    public Task MoveStoredFileAsync(string srrPath, string storedName, int offset, CancellationToken ct = default)
        => Task.Run(() => SRREditor.MoveStoredFile(srrPath, storedName, offset), ct);

    /// <inheritdoc />
    public IReadOnlyList<StoredFileInfo> GetStoredFiles(string srrFilePath)
        => SRRFile.Load(srrFilePath).StoredFiles.Select(s => new StoredFileInfo(s.FileName, s.FileLength)).ToList();

    /// <inheritdoc />
    public Task<string?> ExtractStoredFileAsync(string srrFilePath, string outputDirectory, string storedName, CancellationToken ct = default)
        => Task.Run(() => SRRFile.Load(srrFilePath).ExtractStoredFile(srrFilePath, outputDirectory, name => name == storedName), ct);

    /// <inheritdoc />
    public Task<byte[]?> ReadStoredFileBytesAsync(string srrFilePath, string storedName, CancellationToken ct = default)
        => Task.Run(() => SRRFile.Load(srrFilePath).ReadStoredFile(srrFilePath, name => name == storedName), ct);
}
