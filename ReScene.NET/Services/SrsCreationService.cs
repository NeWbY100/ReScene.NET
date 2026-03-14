using SRRLib;

namespace ReScene.NET.Services;

public class SrsCreationService : ISrsCreationService
{
    private readonly SRSWriter _writer = new();

    public event EventHandler<SrsCreationProgressEventArgs>? Progress
    {
        add => _writer.Progress += value;
        remove => _writer.Progress -= value;
    }

    public Task<SrsCreationResult> CreateAsync(
        string outputPath,
        string sampleFilePath,
        SrsCreationOptions options,
        CancellationToken ct)
    {
        return _writer.CreateAsync(outputPath, sampleFilePath, options, ct);
    }
}
