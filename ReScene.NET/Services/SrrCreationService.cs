using ReScene.SRR;

namespace ReScene.NET.Services;

/// <summary>
/// Default implementation of <see cref="ISrrCreationService"/> that delegates to <see cref="SRRWriter"/>.
/// </summary>
public class SrrCreationService : ISrrCreationService
{
    private readonly SRRWriter _writer = new();

    /// <inheritdoc />
    public event EventHandler<SrrCreationProgressEventArgs>? Progress
    {
        add => _writer.Progress += value;
        remove => _writer.Progress -= value;
    }

    /// <inheritdoc />
    public Task<SrrCreationResult> CreateFromRarAsync(
        string outputPath,
        IReadOnlyList<string> rarVolumePaths,
        IReadOnlyDictionary<string, string>? storedFiles,
        SrrCreationOptions options,
        CancellationToken ct)
    {
        return _writer.CreateAsync(outputPath, rarVolumePaths, storedFiles, options, ct);
    }

    /// <inheritdoc />
    public Task<SrrCreationResult> CreateFromSfvAsync(
        string outputPath,
        string sfvFilePath,
        IReadOnlyDictionary<string, string>? additionalFiles,
        SrrCreationOptions options,
        CancellationToken ct)
    {
        return _writer.CreateFromSfvAsync(outputPath, sfvFilePath, additionalFiles, options, ct);
    }
}
