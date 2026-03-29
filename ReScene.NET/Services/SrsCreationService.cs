using ReScene.SRS;

namespace ReScene.NET.Services;

/// <summary>
/// Default implementation of <see cref="ISrsCreationService"/> that delegates to <see cref="SRSWriter"/>.
/// </summary>
public class SrsCreationService : ISrsCreationService
{
    private readonly SRSWriter _writer = new();

    /// <inheritdoc />
    public event EventHandler<SrsCreationProgressEventArgs>? Progress
    {
        add => _writer.Progress += value;
        remove => _writer.Progress -= value;
    }

    /// <inheritdoc />
    public Task<SrsCreationResult> CreateAsync(
        string outputPath,
        string sampleFilePath,
        SrsCreationOptions options,
        CancellationToken ct)
    {
        return _writer.CreateAsync(outputPath, sampleFilePath, options, ct);
    }
}
