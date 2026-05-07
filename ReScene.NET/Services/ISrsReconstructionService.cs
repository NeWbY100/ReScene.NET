using ReScene.SRS;

namespace ReScene.NET.Services;

public interface ISrsReconstructionService
{
    public event EventHandler<SRSReconstructionProgressEventArgs>? Progress;

    public event EventHandler<SRSScanProgressEventArgs>? ScanProgress;

    public Task<SRSReconstructionResult> RebuildAsync(
        string srsFilePath, string mediaFilePath, string outputPath, CancellationToken ct);
}
