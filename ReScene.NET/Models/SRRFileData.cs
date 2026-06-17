using ReScene.RAR;
using ReScene.SRR;

namespace ReScene.NET.Models;

/// <summary>
/// Holds a parsed SRR file along with detailed RAR block data for each embedded volume.
/// </summary>
public class SRRFileData
{
    /// <summary>
    /// The parsed SRR file.
    /// </summary>
    public required SRRFile SRRFile { get; init; }

    /// <summary>
    /// Detailed RAR blocks per volume, keyed by volume filename.
    /// </summary>
    public Dictionary<string, List<RARDetailedBlock>> VolumeDetailedBlocks { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Loads an SRR file and parses embedded RAR headers for each volume into detailed blocks.
    /// </summary>
    /// <param name="filePath">
    /// The path to the SRR file.
    /// </param>
    /// <returns>
    /// A populated <see cref="SRRFileData"/> instance.
    /// </returns>
    public static SRRFileData Load(string filePath)
    {
        var srrFile = SRRFile.Load(filePath);
        var volumeBlocks = new Dictionary<string, List<RARDetailedBlock>>(StringComparer.OrdinalIgnoreCase);

        if (srrFile.RARFiles.Count > 0)
        {
            try
            {
                using FileStream fs = File.OpenRead(filePath);
                foreach (SRRRarFileBlock rarFile in srrFile.RARFiles)
                {
                    try
                    {
                        long embeddedStart = rarFile.BlockPosition + rarFile.HeaderSize;
                        fs.Position = embeddedStart;

                        List<RARDetailedBlock> detailedBlocks = [.. RARDetailedParser.ParseFromPosition(fs)];
                        volumeBlocks[rarFile.FileName] = detailedBlocks;
                    }
                    catch
                    {
                        // Skip volumes with unparseable embedded RAR data
                    }
                }
            }
            catch
            {
                // Skip if file cannot be re-opened
            }
        }

        return new SRRFileData
        {
            SRRFile = srrFile,
            VolumeDetailedBlocks = volumeBlocks
        };
    }
}
