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
}
