using SRRLib;

namespace ReScene.NET.Services;

public interface ISrsCreationService
{
    event EventHandler<SrsCreationProgressEventArgs>? Progress;

    Task<SrsCreationResult> CreateAsync(
        string outputPath,
        string sampleFilePath,
        SrsCreationOptions options,
        CancellationToken ct);
}
