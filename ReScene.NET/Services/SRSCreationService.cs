using ReScene.SRS;

namespace ReScene.NET.Services;

/// <summary>
/// Default implementation of <see cref="ISrsCreationService"/> that delegates to <see cref="SRSWriter"/>.
/// </summary>
public class SRSCreationService : ISrsCreationService
{
    private readonly SRSWriter _writer = new();

    /// <inheritdoc />
    public event EventHandler<SRSCreationProgressEventArgs>? Progress
    {
        add => _writer.Progress += value;
        remove => _writer.Progress -= value;
    }

    /// <inheritdoc />
    public Task<SRSCreationResult> CreateAsync(
        string outputPath,
        string sampleFilePath,
        SRSCreationOptions options,
        CancellationToken ct) => _writer.CreateAsync(outputPath, sampleFilePath, options, ct);
}
