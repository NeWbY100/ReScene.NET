using ReScene.SRS;

namespace ReScene.NET.Services;

public class SrsReconstructionService : ISrsReconstructionService
{
    private readonly SRSRebuilder _rebuilder = new();

    public event EventHandler<SrsReconstructionProgressEventArgs>? Progress
    {
        add => _rebuilder.Progress += value;
        remove => _rebuilder.Progress -= value;
    }

    public event EventHandler<SrsScanProgressEventArgs>? ScanProgress
    {
        add => _rebuilder.ScanProgress += value;
        remove => _rebuilder.ScanProgress -= value;
    }

    public Task<SrsReconstructionResult> RebuildAsync(
        string srsFilePath, string mediaFilePath, string outputPath, CancellationToken ct) => _rebuilder.RebuildAsync(srsFilePath, mediaFilePath, outputPath, ct);
}
