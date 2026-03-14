using RARLib;
using ReScene.Core.Comparison;

namespace ReScene.NET.Services;

public interface IFileCompareService
{
    object? LoadFileData(string filePath);
    List<RARDetailedBlock>? ParseDetailedBlocks(string filePath);
    CompareResult Compare(object? leftData, object? rightData,
        List<RARDetailedBlock>? leftBlocks = null, List<RARDetailedBlock>? rightBlocks = null);
}
