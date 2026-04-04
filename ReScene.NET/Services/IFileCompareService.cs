using ReScene.RAR;
using ReScene.Core.Comparison;

namespace ReScene.NET.Services;

/// <summary>
/// Service for loading scene file data and computing structural comparisons between two files.
/// </summary>
public interface IFileCompareService
{
    /// <summary>
    /// Loads and parses a scene file (SRR, SRS, or RAR) into its data model.
    /// </summary>
    /// <param name="filePath">Path to the file to load.</param>
    /// <returns>The parsed file data, or <c>null</c> on failure.</returns>
    public object? LoadFileData(string filePath);

    /// <summary>
    /// Parses a RAR file into detailed header blocks for field-level comparison.
    /// </summary>
    /// <param name="filePath">Path to the RAR file.</param>
    /// <returns>The list of detailed blocks, or <c>null</c> if the file is not a RAR.</returns>
    public List<RARDetailedBlock>? ParseDetailedBlocks(string filePath);

    /// <summary>
    /// Compares two parsed file data objects and returns the structural differences.
    /// </summary>
    /// <param name="leftData">Parsed data for the left file.</param>
    /// <param name="rightData">Parsed data for the right file.</param>
    /// <param name="leftBlocks">Optional detailed RAR blocks for the left file.</param>
    /// <param name="rightBlocks">Optional detailed RAR blocks for the right file.</param>
    /// <returns>A <see cref="CompareResult"/> describing all differences found.</returns>
    public CompareResult Compare(object? leftData, object? rightData,
        List<RARDetailedBlock>? leftBlocks = null, List<RARDetailedBlock>? rightBlocks = null);
}
