using ReScene.SRS;

namespace ReScene.NET.Services;

public class SRSReconstructionService : ISrsReconstructionService
{
    private readonly SRSRebuilder _rebuilder = new();

    public event EventHandler<SRSReconstructionProgressEventArgs>? Progress
    {
        add => _rebuilder.Progress += value;
        remove => _rebuilder.Progress -= value;
    }

    public event EventHandler<SRSScanProgressEventArgs>? ScanProgress
    {
        add => _rebuilder.ScanProgress += value;
        remove => _rebuilder.ScanProgress -= value;
    }

    public Task<SRSReconstructionResult> RebuildAsync(
        string srsFilePath, string mediaFilePath, string outputPath, CancellationToken ct) => _rebuilder.RebuildAsync(srsFilePath, mediaFilePath, outputPath, ct);
}
