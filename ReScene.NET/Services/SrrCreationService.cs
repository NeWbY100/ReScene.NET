using SRRLib;

namespace ReScene.NET.Services;

public class SrrCreationService : ISrrCreationService
{
    private readonly SRRWriter _writer = new();

    public event EventHandler<SrrCreationProgressEventArgs>? Progress
    {
        add => _writer.Progress += value;
        remove => _writer.Progress -= value;
    }

    public Task<SrrCreationResult> CreateFromRarAsync(
        string outputPath,
        IReadOnlyList<string> rarVolumePaths,
        IReadOnlyDictionary<string, string>? storedFiles,
        SrrCreationOptions options,
        CancellationToken ct)
    {
        return _writer.CreateAsync(outputPath, rarVolumePaths, storedFiles, options, ct);
    }

    public Task<SrrCreationResult> CreateFromSfvAsync(
        string outputPath,
        string sfvFilePath,
        IReadOnlyList<string>? additionalFiles,
        SrrCreationOptions options,
        CancellationToken ct)
    {
        return _writer.CreateFromSfvAsync(outputPath, sfvFilePath, additionalFiles, options, ct);
    }
}
