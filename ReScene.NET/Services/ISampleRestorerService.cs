using SRR;

namespace ReScene.NET.Services;

public class SrsEntryInfo
{
    public string SrsFileName { get; set; } = string.Empty;
    public string SampleFileName { get; set; } = string.Empty;
    public ulong SampleSize { get; set; }
    public uint ExpectedCrc { get; set; }
}

public interface ISampleRestorerService
{
    event EventHandler<SrsReconstructionProgressEventArgs>? Progress;

    List<SrsEntryInfo> GetSrsEntries(string srrFilePath);

    Task<SrsReconstructionResult> RestoreSampleAsync(
        string srrFilePath, string srsFileName,
        string mediaFilePath, string outputPath, CancellationToken ct);
}
