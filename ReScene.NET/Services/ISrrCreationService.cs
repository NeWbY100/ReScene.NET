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
    public event EventHandler<SrrCreationProgressEventArgs>? Progress;

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
    /// Optional files to embed, keyed by stored name to full path.
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
    public Task<SrrCreationResult> CreateFromRarAsync(
        string outputPath,
        IReadOnlyList<string> rarVolumePaths,
        IReadOnlyDictionary<string, string>? storedFiles,
        SrrCreationOptions options,
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
    /// Optional additional files to embed, keyed by stored name to full path.
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
    public Task<SrrCreationResult> CreateFromSfvAsync(
        string outputPath,
        string sfvFilePath,
        IReadOnlyDictionary<string, string>? additionalFiles,
        SrrCreationOptions options,
        CancellationToken ct);
}
