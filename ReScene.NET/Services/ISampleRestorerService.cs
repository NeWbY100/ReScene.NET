using ReScene.SRS;

namespace ReScene.NET.Services;

public class SrsEntryInfo
{
    public string SrsFileName { get; set; } = string.Empty;
    public string SampleFileName { get; set; } = string.Empty;
    public ulong SampleSize
    {
        get; set;
    }
    public uint ExpectedCrc
    {
        get; set;
    }
}

public interface ISampleRestorerService
{
    public event EventHandler<SrsReconstructionProgressEventArgs>? Progress;

    public List<SrsEntryInfo> GetSrsEntries(string srrFilePath);

    public Task<SrsReconstructionResult> RestoreSampleAsync(
        string srrFilePath, string srsFileName,
        string mediaFilePath, string outputPath, CancellationToken ct);
}
