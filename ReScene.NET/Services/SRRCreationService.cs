using ReScene.SRR;

namespace ReScene.NET.Services;

/// <summary>
/// Default implementation of <see cref="ISrrCreationService"/> that delegates to <see cref="SRRWriter"/>.
/// </summary>
public class SRRCreationService : ISrrCreationService
{
    private readonly SRRWriter _writer = new();

    /// <inheritdoc />
    public event EventHandler<SRRCreationProgressEventArgs>? Progress
    {
        add => _writer.Progress += value;
        remove => _writer.Progress -= value;
    }

    /// <inheritdoc />
    public Task<SRRCreationResult> CreateFromRarAsync(
        string outputPath,
        IReadOnlyList<string> rarVolumePaths,
        IReadOnlyDictionary<string, string>? storedFiles,
        SRRCreationOptions options,
        CancellationToken ct) => _writer.CreateAsync(outputPath, rarVolumePaths, storedFiles, options, ct);

    /// <inheritdoc />
    public Task<SRRCreationResult> CreateFromSfvAsync(
        string outputPath,
        string sfvFilePath,
        IReadOnlyDictionary<string, string>? additionalFiles,
        SRRCreationOptions options,
        CancellationToken ct) => _writer.CreateFromSfvAsync(outputPath, sfvFilePath, additionalFiles, options, ct);
}
