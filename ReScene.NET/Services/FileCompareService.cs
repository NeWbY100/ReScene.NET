using RARLib;
using ReScene.Core.Comparison;
using SRRLib;

namespace ReScene.NET.Services;

public class FileCompareService : IFileCompareService
{
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

    public CompareResult Compare(object? leftData, object? rightData,
        List<RARDetailedBlock>? leftBlocks = null, List<RARDetailedBlock>? rightBlocks = null)
    {
        return FileComparer.Compare(leftData, rightData, leftBlocks, rightBlocks);
    }
}
