using ReScene.SRS;

namespace ReScene.NET.Services;

public class SRSEntryInfo
{
    public string SRSFileName { get; set; } = string.Empty;
    public string SampleFileName { get; set; } = string.Empty;
    public ulong SampleSize
    {
        get; set;
    }
    public uint ExpectedCRC
    {
        get; set;
    }
}

public interface ISampleRestorerService
{
    public event EventHandler<SRSReconstructionProgressEventArgs>? Progress;

    public List<SRSEntryInfo> GetSRSEntries(string srrFilePath);

    public Task<SRSReconstructionResult> RestoreSampleAsync(
        string srrFilePath, string srsFileName,
        string mediaFilePath, string outputPath, CancellationToken ct);
}
