using ReScene.SRR;
using ReScene.SRS;

namespace ReScene.NET.Services;

public class SampleRestorerService(ITempDirectoryService tempDir) : ISampleRestorerService
{
    private readonly SRSRebuilder _rebuilder = new();
    private readonly ITempDirectoryService _tempDir = tempDir;

    public event EventHandler<SrsReconstructionProgressEventArgs>? Progress
    {
        add => _rebuilder.Progress += value;
        remove => _rebuilder.Progress -= value;
    }

    public List<SrsEntryInfo> GetSrsEntries(string srrFilePath)
    {
        var srr = SRRFile.Load(srrFilePath);
        var entries = new List<SrsEntryInfo>();

        string tempDir = _tempDir.CreateTempDirectory();

        try
        {
            foreach (SrrStoredFileBlock stored in srr.StoredFiles)
            {
                if (!stored.FileName.EndsWith(".srs", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                string srsName = stored.FileName;

                // Extract the SRS to a temp file to read its metadata
                string? extractedPath = srr.ExtractStoredFile(
                    srrFilePath, tempDir, name => name == srsName);

                if (extractedPath is null)
                {
                    continue;
                }

                try
                {
                    var srs = SRSFile.Load(extractedPath);
                    if (srs.FileData is { } fd)
                    {
                        entries.Add(new SrsEntryInfo
                        {
                            SrsFileName = srsName,
                            SampleFileName = fd.FileName,
                            SampleSize = fd.SampleSize,
                            ExpectedCrc = fd.Crc32
                        });
                    }
                }
                catch
                {
                    // Skip unreadable SRS files
                }
            }
        }
        finally
        {
            _tempDir.Cleanup(tempDir);
        }

        return entries;
    }

    public async Task<SrsReconstructionResult> RestoreSampleAsync(
        string srrFilePath, string srsFileName,
        string mediaFilePath, string outputPath, CancellationToken ct)
    {
        var srr = SRRFile.Load(srrFilePath);
        string tempDir = _tempDir.CreateTempDirectory();

        try
        {
            string? extractedPath = srr.ExtractStoredFile(
                srrFilePath, tempDir, name => name == srsFileName);

            if (extractedPath is null)
            {
                return new SrsReconstructionResult(
                    Success: false, CrcMatch: false,
                    ExpectedCrc: 0, ActualCrc: 0,
                    ExpectedSize: 0, ActualSize: 0,
                    ErrorMessage: $"Could not extract '{srsFileName}' from SRR");
            }

            return await _rebuilder.RebuildAsync(extractedPath, mediaFilePath, outputPath, ct);
        }
        finally
        {
            _tempDir.Cleanup(tempDir);
        }
    }
}
