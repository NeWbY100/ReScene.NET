using ReScene.App.Core.Services;
using ReScene.App.Core.ViewModels;
using ReScene.SRS;
namespace ReScene.App.Core.Tests;

/// <summary>
/// Tests that SampleRestorerViewModel loads embedded SRS entries off the UI thread while still
/// populating the bound collection correctly.
/// </summary>
public class SampleRestorerViewModelTests
{
    private sealed class FakeSampleRestorerService : ISampleRestorerService
    {
        public List<SRSEntryInfo> Entries { get; init; } = [];

        // Custom accessors: no backing field, so no CS0067 for an event the fake never raises.
        public event EventHandler<SRSReconstructionProgressEventArgs>? Progress { add { } remove { } }

        public List<SRSEntryInfo> GetSRSEntries(string srrFilePath) => Entries;

        public Task<SRSReconstructionResult> RestoreSampleAsync(
            string srrFilePath, string srsFileName, string mediaFilePath, string outputPath, CancellationToken ct)
            => throw new NotSupportedException();
    }

    [Fact]
    public async Task LoadSRSEntriesAsync_PopulatesBoundEntriesFromService()
    {
        var service = new FakeSampleRestorerService
        {
            Entries =
            [
                new SRSEntryInfo { SRSFileName = "a.srs", SampleFileName = "a.mkv" },
                new SRSEntryInfo { SRSFileName = "b.srs", SampleFileName = "b.mkv" },
            ],
        };
        var vm = new SampleRestorerViewModel(service, new NoOpFileDialogService(), new TestUiDispatcher());

        await vm.LoadSRSEntriesAsync();

        Assert.Equal(2, vm.SRSEntries.Count);
        Assert.Equal("a.srs", vm.SRSEntries[0].SRSFileName);
        Assert.Equal("b.mkv", vm.SRSEntries[1].SampleFileName);
    }

    [Fact]
    public async Task LoadSRSEntriesAsync_CalledTwice_DoesNotAccumulateEntries()
    {
        var service = new FakeSampleRestorerService
        {
            Entries = [new SRSEntryInfo { SRSFileName = "a.srs", SampleFileName = "a.mkv" }],
        };
        var vm = new SampleRestorerViewModel(service, new NoOpFileDialogService(), new TestUiDispatcher());

        await vm.LoadSRSEntriesAsync();
        await vm.LoadSRSEntriesAsync();

        // Each load clears the previous entries first, so a reload replaces rather than appends.
        Assert.Single(vm.SRSEntries);
    }

    // A service whose FIRST GetSRSEntries call blocks until released (returning First), while any
    // later call returns Second immediately — lets a test deterministically overlap two loads.
    private sealed class GatedSampleRestorerService : ISampleRestorerService
    {
        private readonly ManualResetEventSlim _firstCallGate = new(initialState: false);
        private readonly ManualResetEventSlim _firstCallStarted = new(initialState: false);
        private int _calls;

        public List<SRSEntryInfo> First { get; init; } = [];
        public List<SRSEntryInfo> Second { get; init; } = [];

        public event EventHandler<SRSReconstructionProgressEventArgs>? Progress { add { } remove { } }

        public void WaitForFirstCall() => _firstCallStarted.Wait();
        public void ReleaseFirstCall() => _firstCallGate.Set();

        public List<SRSEntryInfo> GetSRSEntries(string srrFilePath)
        {
            int call = Interlocked.Increment(ref _calls);
            if (call == 1)
            {
                _firstCallStarted.Set();
                _firstCallGate.Wait();
                return First;
            }

            return Second;
        }

        public Task<SRSReconstructionResult> RestoreSampleAsync(
            string srrFilePath, string srsFileName, string mediaFilePath, string outputPath, CancellationToken ct)
            => throw new NotSupportedException();
    }

    [Fact]
    public async Task LoadSRSEntriesAsync_OverlappingLoads_KeepsOnlyLatest()
    {
        var service = new GatedSampleRestorerService
        {
            First = [new SRSEntryInfo { SRSFileName = "old.srs", SampleFileName = "old.mkv" }],
            Second = [new SRSEntryInfo { SRSFileName = "new.srs", SampleFileName = "new.mkv" }],
        };
        var vm = new SampleRestorerViewModel(service, new NoOpFileDialogService(), new TestUiDispatcher());

        // Load #1 starts and blocks inside GetSRSEntries (still "parsing" the first SRR).
        Task first = vm.LoadSRSEntriesAsync();
        service.WaitForFirstCall();

        // Load #2 starts while #1 is in flight; it completes immediately and populates the latest.
        await vm.LoadSRSEntriesAsync();

        // Release #1: its continuation must see it was superseded and discard its stale result,
        // rather than appending "old.srs" onto the already-loaded "new.srs".
        service.ReleaseFirstCall();
        await first;

        SampleRestorerViewModel.SRSFileEntry entry = Assert.Single(vm.SRSEntries);
        Assert.Equal("new.srs", entry.SRSFileName);
    }
}
