using ReScene.RAR;
using ReScene.Core.Comparison;
using ReScene.SRS;

namespace ReScene.NET.Services;

/// <summary>
/// Default implementation of <see cref="IFileCompareService"/> for loading and comparing scene files.
/// </summary>
public class FileCompareService : IFileCompareService
{
    /// <inheritdoc />
    public object? LoadFileData(string filePath)
    {
        string ext = Path.GetExtension(filePath).ToLowerInvariant();
        return ext switch
        {
            ".srr" => SRRFileData.Load(filePath),
            ".srs" => SRSFile.Load(filePath),
            _ => RARFileData.Load(filePath)
        };
    }

    /// <inheritdoc />
    public List<RARDetailedBlock>? ParseDetailedBlocks(string filePath)
    {
        string ext = Path.GetExtension(filePath).ToLowerInvariant();
        if (ext != ".rar") return null;

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
        List<RARDetailedBlock>? leftBlocks = null, List<RARDetailedBlock>? rightBlocks = null)
    {
        return FileComparer.Compare(leftData, rightData, leftBlocks, rightBlocks);
    }
}
