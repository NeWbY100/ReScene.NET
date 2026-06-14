using ReScene.SRR;

namespace ReScene.NET.Services;

/// <summary>
/// Service for creating SRR files from RAR volumes or SFV file listings.
/// </summary>
public interface ISrrCreationService
{
    /// <summary>
    /// Raised to report progress during SRR creation.
    /// </summary>
    public event EventHandler<SRRCreationProgressEventArgs>? Progress;

    /// <summary>
    /// Creates an SRR file from a list of RAR volume paths.
    /// </summary>
    /// <param name="outputPath">
    /// Destination path for the SRR file.
    /// </param>
    /// <param name="rarVolumePaths">
    /// Ordered list of RAR volume paths.
    /// </param>
    /// <param name="storedFiles">
    /// Optional ordered list of files to embed; blocks are written in this order.
    /// </param>
    /// <param name="options">
    /// Creation options.
    /// </param>
    /// <param name="ct">
    /// Cancellation token.
    /// </param>
    /// <returns>
    /// The creation result including success status and statistics.
    /// </returns>
    public Task<SRRCreationResult> CreateFromRarAsync(
        string outputPath,
        IReadOnlyList<string> rarVolumePaths,
        IReadOnlyList<StoredFileEntry>? storedFiles,
        SRRCreationOptions options,
        CancellationToken ct);

    /// <summary>
    /// Creates an SRR file from an SFV file that references RAR volumes.
    /// </summary>
    /// <param name="outputPath">
    /// Destination path for the SRR file.
    /// </param>
    /// <param name="sfvFilePath">
    /// Path to the SFV file.
    /// </param>
    /// <param name="additionalFiles">
    /// Optional ordered list of additional files to embed; blocks are written in this order.
    /// </param>
    /// <param name="options">
    /// Creation options.
    /// </param>
    /// <param name="ct">
    /// Cancellation token.
    /// </param>
    /// <returns>
    /// The creation result including success status and statistics.
    /// </returns>
    public Task<SRRCreationResult> CreateFromSFVAsync(
        string outputPath,
        string sfvFilePath,
        IReadOnlyList<StoredFileEntry>? additionalFiles,
        SRRCreationOptions options,
        CancellationToken ct);
}
