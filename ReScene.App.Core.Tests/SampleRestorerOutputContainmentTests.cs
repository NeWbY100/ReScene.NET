using ReScene.App.Core.Services;
using ReScene.App.Core.ViewModels;
using ReScene.SRS;

namespace ReScene.App.Core.Tests;

/// <summary>
/// Pins the CALL SITE, not just the validator: the bulk restore must never hand the reconstruction
/// service a destination outside the chosen output directory.
/// </summary>
/// <remarks>
/// <c>MetadataOutputPathTests</c> covers the validator in isolation, and would stay green if
/// someone put the original <c>Path.Combine(OutputDirectoryPath, entry.SampleFileName)</c> back at
/// the call site — the exact mutation this class exists to catch. Restoring is driven end to end
/// through the command, and the fake service records every output path it is asked to write.
/// </remarks>
public class SampleRestorerOutputContainmentTests : TempDirTestBase
{
    private sealed class RecordingRestorerService : ISampleRestorerService
    {
        public List<SRSEntryInfo> Entries { get; init; } = [];

        /// <summary>Every destination the view-model asked to write, in call order.</summary>
        public List<string> RequestedOutputs { get; } = [];

        public event EventHandler<SRSReconstructionProgressEventArgs>? Progress { add { } remove { } }

        public List<SRSEntryInfo> GetSRSEntries(string srrFilePath) => Entries;

        public Task<SRSReconstructionResult> RestoreSampleAsync(
            string srrFilePath, string srsFileName, string mediaFilePath, string outputPath, CancellationToken ct)
        {
            RequestedOutputs.Add(outputPath);
            return Task.FromResult(new SRSReconstructionResult(
                Success: true, CRCMatch: true,
                ExpectedCRC: 0, ActualCRC: 0,
                ExpectedSize: 0, ActualSize: 0,
                ErrorMessage: null));
        }
    }

    [Theory]
    [InlineData("../escaped.mkv")]
    [InlineData("..\\escaped.mkv")]
    [InlineData("sub/../../escaped.mkv")]
    [InlineData(@"C:\Windows\Temp\escaped.mkv")]
    [InlineData("/tmp/escaped.mkv")]
    public async Task RestoreAll_MetadataNameEscapingTheOutputFolder_IsNeverPassedToTheService(string sampleName)
    {
        string outputDir = Path.Combine(TempDir, "chosen");
        Directory.CreateDirectory(outputDir);

        string media = Path.Combine(TempDir, "media.mkv");
        await File.WriteAllTextAsync(media, "media");

        var service = new RecordingRestorerService
        {
            Entries = [new SRSEntryInfo { SRSFileName = "a.srs", SampleFileName = sampleName }],
        };

        var vm = new SampleRestorerViewModel(service, new NoOpFileDialogService(), new TestUiDispatcher())
        {
            SRRFilePath = Path.Combine(TempDir, "release.srr"),
            OutputDirectoryPath = outputDir,
        };

        await vm.LoadSRSEntriesAsync();
        vm.SRSEntries[0].MediaFilePath = media;
        vm.SRSEntries[0].IsSelected = true;

        await vm.RestoreCommand.ExecuteAsync(null);

        // The entry must have been refused outright rather than written anywhere.
        Assert.Empty(service.RequestedOutputs);
        Assert.Contains("Failed", vm.SRSEntries[0].Status, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RestoreAll_OrdinaryMetadataName_WritesInsideTheChosenFolder()
    {
        string outputDir = Path.Combine(TempDir, "chosen");
        Directory.CreateDirectory(outputDir);

        string media = Path.Combine(TempDir, "media.mkv");
        await File.WriteAllTextAsync(media, "media");

        var service = new RecordingRestorerService
        {
            Entries = [new SRSEntryInfo { SRSFileName = "a.srs", SampleFileName = "sample.mkv" }],
        };

        var vm = new SampleRestorerViewModel(service, new NoOpFileDialogService(), new TestUiDispatcher())
        {
            SRRFilePath = Path.Combine(TempDir, "release.srr"),
            OutputDirectoryPath = outputDir,
        };

        await vm.LoadSRSEntriesAsync();
        vm.SRSEntries[0].MediaFilePath = media;
        vm.SRSEntries[0].IsSelected = true;

        await vm.RestoreCommand.ExecuteAsync(null);

        string requested = Assert.Single(service.RequestedOutputs);
        Assert.Equal(Path.Combine(Path.GetFullPath(outputDir), "sample.mkv"), requested);
    }
}
