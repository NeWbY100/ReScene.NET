using ReScene.SRS;

namespace ReScene.NET.Services;

public interface ISrsReconstructionService
{
    public event EventHandler<SrsReconstructionProgressEventArgs>? Progress;

    public event EventHandler<SrsScanProgressEventArgs>? ScanProgress;

    public Task<SrsReconstructionResult> RebuildAsync(
        string srsFilePath, string mediaFilePath, string outputPath, CancellationToken ct);
}
