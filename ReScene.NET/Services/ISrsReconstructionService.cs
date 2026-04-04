using ReScene.SRS;

namespace ReScene.NET.Services;

public interface ISrsReconstructionService
{
    public event EventHandler<SrsReconstructionProgressEventArgs>? Progress;

    public Task<SrsReconstructionResult> RebuildAsync(
        string srsFilePath, string mediaFilePath, string outputPath, CancellationToken ct);
}
