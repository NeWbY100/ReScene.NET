using ReScene.SRS;

namespace ReScene.NET.Services;

/// <summary>
/// Service for creating SRS (Sample ReScene) files from media samples.
/// </summary>
public interface ISrsCreationService
{
    /// <summary>Raised to report progress during SRS creation.</summary>
    event EventHandler<SrsCreationProgressEventArgs>? Progress;

    /// <summary>
    /// Creates an SRS file from a sample media file.
    /// </summary>
    /// <param name="outputPath">Destination path for the SRS file.</param>
    /// <param name="sampleFilePath">Path to the source sample file.</param>
    /// <param name="options">Creation options.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The creation result including success status and file size.</returns>
    Task<SrsCreationResult> CreateAsync(
        string outputPath,
        string sampleFilePath,
        SrsCreationOptions options,
        CancellationToken ct);
}
