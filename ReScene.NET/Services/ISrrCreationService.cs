using SRRLib;

namespace ReScene.NET.Services;

public interface ISrrCreationService
{
    event EventHandler<SrrCreationProgressEventArgs>? Progress;

    Task<SrrCreationResult> CreateFromRarAsync(
        string outputPath,
        IReadOnlyList<string> rarVolumePaths,
        IReadOnlyDictionary<string, string>? storedFiles,
        SrrCreationOptions options,
        CancellationToken ct);

    Task<SrrCreationResult> CreateFromSfvAsync(
        string outputPath,
        string sfvFilePath,
        IReadOnlyList<string>? additionalFiles,
        SrrCreationOptions options,
        CancellationToken ct);
}
