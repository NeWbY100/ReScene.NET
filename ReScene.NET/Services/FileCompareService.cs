using ReScene.Core.Comparison;
using ReScene.Hex;
using ReScene.RAR;
using ReScene.SRS;

namespace ReScene.NET.Services;

/// <summary>
/// Default implementation of <see cref="IFileCompareService"/> for loading and comparing scene files.
/// </summary>
/// <param name="settingsService">
/// Optional settings source for user-tunable limits (the MKV element cap); defaults apply when null.
/// </param>
public class FileCompareService(IAppSettingsService? settingsService = null) : IFileCompareService
{
    private readonly IAppSettingsService? _settingsService = settingsService;

    /// <inheritdoc />
    public object? LoadFileData(string filePath)
    {
        string ext = Path.GetExtension(filePath).ToLowerInvariant();
        return ext switch
        {
            ".srr" => SRRFileData.Load(filePath),
            ".srs" => SRSFile.Load(filePath),
            ".mkv" or ".webm" => MKVFileData.Load(filePath,
                _settingsService?.Load().MkvMaxElements ?? MKVFileData.DefaultMaxElements),
            _ => RARFileData.Load(filePath)
        };
    }

    /// <inheritdoc />
    public List<RARDetailedBlock>? ParseDetailedBlocks(string filePath)
    {
        string ext = Path.GetExtension(filePath).ToLowerInvariant();
        if (ext != ".rar")
        {
            return null;
        }

        try
        {
            return RARDetailedParser.Parse(filePath);
        }
        catch
        {
            return null;
        }
    }

    /// <inheritdoc />
    public CompareResult Compare(object? leftData, object? rightData,
        List<RARDetailedBlock>? leftBlocks = null, List<RARDetailedBlock>? rightBlocks = null,
        IHexDataSource? leftSource = null, IHexDataSource? rightSource = null) => FileComparer.Compare(leftData, rightData, leftBlocks, rightBlocks, leftSource, rightSource);
}
