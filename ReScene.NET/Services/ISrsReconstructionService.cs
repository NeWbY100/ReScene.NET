using ReScene.SRS;

namespace ReScene.NET.Services;

public interface ISrsReconstructionService
{
    event EventHandler<SrsReconstructionProgressEventArgs>? Progress;

    Task<SrsReconstructionResult> RebuildAsync(
        string srsFilePath, string mediaFilePath, string outputPath, CancellationToken ct);
}
