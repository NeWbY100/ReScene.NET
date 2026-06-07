namespace ReScene.NET.Services;

/// <summary>
/// Service for editing existing SRR files by adding or removing stored files.
/// </summary>
public interface ISrrEditingService
{
    /// <summary>
    /// Adds one or more stored files to an existing SRR file.
    /// </summary>
    /// <param name="srrFilePath">
    /// Path to the SRR file to modify.
    /// </param>
    /// <param name="files">
    /// List of tuples containing the stored name and file path for each file to add.
    /// </param>
    public void AddStoredFiles(string srrFilePath, IReadOnlyList<(string StoredName, string FilePath)> files);

    /// <summary>
    /// Removes stored files from an existing SRR file by name.
    /// </summary>
    /// <param name="srrFilePath">
    /// Path to the SRR file to modify.
    /// </param>
    /// <param name="storedNames">
    /// List of stored file names to remove.
    /// </param>
    public void RemoveStoredFiles(string srrFilePath, IReadOnlyList<string> storedNames);

    /// <summary>
    /// Renames a stored file inside an existing SRR file.
    /// </summary>
    /// <param name="srrPath">
    /// Path to the SRR file to modify.
    /// </param>
    /// <param name="oldName">
    /// The current stored file name.
    /// </param>
    /// <param name="newName">
    /// The new stored file name.
    /// </param>
    /// <param name="ct">
    /// Cancellation token.
    /// </param>
    public Task RenameStoredFileAsync(string srrPath, string oldName, string newName, CancellationToken ct = default);

    /// <summary>
    /// Moves a stored file within an existing SRR file by a given offset among stored-file blocks.
    /// </summary>
    /// <param name="srrPath">
    /// Path to the SRR file to modify.
    /// </param>
    /// <param name="storedName">
    /// The stored file name to move.
    /// </param>
    /// <param name="offset">
    /// Number of positions to move. Use -1 for up, +1 for down.
    /// </param>
    /// <param name="ct">
    /// Cancellation token.
    /// </param>
    public Task MoveStoredFileAsync(string srrPath, string storedName, int offset, CancellationToken ct = default);

    /// <summary>
    /// Lists the stored-file names currently inside an SRR file, in stored order.
    /// </summary>
    /// <param name="srrFilePath">
    /// Path to the SRR file to read.
    /// </param>
    /// <returns>
    /// The stored-file names, in the order they appear in the SRR.
    /// </returns>
    public IReadOnlyList<string> GetStoredFileNames(string srrFilePath);
}
