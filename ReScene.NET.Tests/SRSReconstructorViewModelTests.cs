using System.Windows.Threading;
using ReScene.NET.Services;
using ReScene.NET.ViewModels;
using ReScene.SRS;

namespace ReScene.NET.Tests;

/// <summary>
/// Unit tests for <see cref="SRSReconstructorViewModel"/>. The VM was previously untested; these
/// pin the <c>CanRebuild</c> command-gating branches and the success / failure / exception result
/// handling inside <c>RebuildAsync</c>. The reconstruction service, dialog, temp-dir and dispatcher
/// dependencies are all faked so the logic runs without a WPF UI thread or real disc I/O.
/// </summary>
public sealed class SRSReconstructorViewModelTests : TempDirTestBase
{
    // ── Fakes ───────────────────────────────────────────────

    /// <summary>
    /// Scriptable <see cref="ISrsReconstructionService"/>. Either returns a canned
    /// <see cref="SRSReconstructionResult"/> or throws, depending on how it is configured.
    /// Records the call count and the arguments it was invoked with.
    /// </summary>
    private sealed class FakeReconstructionService : ISrsReconstructionService
    {
        public event EventHandler<SRSReconstructionProgressEventArgs>? Progress { add { } remove { } }
        public event EventHandler<SRSScanProgressEventArgs>? ScanProgress { add { } remove { } }

        public SRSReconstructionResult? Result { get; set; }
        public Exception? ThrowOnRebuild { get; set; }
        public int RebuildCalls { get; private set; }

        public Task<SRSReconstructionResult> RebuildAsync(
            string srsFilePath, string mediaFilePath, string outputPath, CancellationToken ct)
        {
            RebuildCalls++;
            if (ThrowOnRebuild is not null)
            {
                throw ThrowOnRebuild;
            }

            return Task.FromResult(Result ?? throw new InvalidOperationException("No Result scripted."));
        }
    }

    /// <summary>Temp-dir double that hands out a real directory and records that Cleanup was called.</summary>
    private sealed class RecordingTempDirectoryService(string root) : ITempDirectoryService
    {
        public int CleanupCalls { get; private set; }
        public string? LastCleanupArg { get; private set; }

        public string CreateTempDirectory()
        {
            string dir = Path.Combine(root, "tmp_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            return dir;
        }

        public void Cleanup(string? tempDir)
        {
            CleanupCalls++;
            LastCleanupArg = tempDir;
        }
    }

    /// <summary>Runs queued/marshalled work inline on the calling thread; no real dispatcher.</summary>
    private sealed class SynchronousUiDispatcher : IUiDispatcher
    {
        public void Invoke(Action action) => action();
        public void Post(Action action) => action();
        public void Post(Action action, DispatcherPriority priority) => action();
        public bool CheckAccess() => true;
    }

    private static SRSReconstructorViewModel CreateVm(
        FakeReconstructionService service, ITempDirectoryService tempDir) =>
        new(service, new NoOpFileDialogService(), tempDir, new SynchronousUiDispatcher());

    // ── CanRebuild gating ───────────────────────────────────

    [Fact]
    // Blank SRS path must disable the command even when output is set (non-ISO).
    public void CanRebuild_False_WhenSrsPathBlank()
    {
        SRSReconstructorViewModel vm = CreateVm(new FakeReconstructionService(), new NoOpTempDirectoryService());
        vm.SRSFilePath = string.Empty;
        vm.MediaFilePath = @"C:\media.mkv";
        vm.OutputPath = @"C:\out.mkv";

        Assert.False(vm.RebuildCommand.CanExecute(null));
    }

    [Fact]
    // Blank output path must disable the command even when SRS and media are set.
    public void CanRebuild_False_WhenOutputPathBlank()
    {
        SRSReconstructorViewModel vm = CreateVm(new FakeReconstructionService(), new NoOpTempDirectoryService());
        vm.SRSFilePath = @"C:\sample.srs";
        vm.MediaFilePath = @"C:\media.mkv";
        vm.OutputPath = string.Empty;

        Assert.False(vm.RebuildCommand.CanExecute(null));
    }

    [Fact]
    // ISO source bypasses the media-file requirement: SRS + output set, blank media is still enabled.
    public void CanRebuild_True_ForIsoSource_WithBlankMedia()
    {
        SRSReconstructorViewModel vm = CreateVm(new FakeReconstructionService(), new NoOpTempDirectoryService());
        vm.SRSFilePath = @"C:\sample.srs";
        vm.OutputPath = @"C:\out.mkv";
        vm.IsISOSource = true;
        vm.MediaFilePath = string.Empty;

        Assert.True(vm.RebuildCommand.CanExecute(null));
    }

    [Fact]
    // Non-ISO source DOES require a media file: SRS + output set but blank media stays disabled.
    public void CanRebuild_False_ForNonIsoSource_WithBlankMedia()
    {
        SRSReconstructorViewModel vm = CreateVm(new FakeReconstructionService(), new NoOpTempDirectoryService());
        vm.SRSFilePath = @"C:\sample.srs";
        vm.OutputPath = @"C:\out.mkv";
        vm.IsISOSource = false;
        vm.MediaFilePath = string.Empty;

        Assert.False(vm.RebuildCommand.CanExecute(null));
    }

    // ── RebuildAsync result handling ────────────────────────

    [Fact]
    // Success result => ResultSuccess true, IsRebuilding reset, and the summary carries the
    // hex CRC (X8) and the byte size (N0). Uses the non-ISO path so no disc I/O is touched.
    public async Task RebuildAsync_Success_SetsSummaryWithCrcAndSize()
    {
        var service = new FakeReconstructionService
        {
            // ExpectedCRC/ActualCRC match; ActualCRC 0x0ABCDEF1 formats as "0ABCDEF1".
            Result = new SRSReconstructionResult(
                Success: true, CRCMatch: true,
                ExpectedCRC: 0x0ABCDEF1u, ActualCRC: 0x0ABCDEF1u,
                ExpectedSize: 1234567L, ActualSize: 1234567L, ErrorMessage: null),
        };
        SRSReconstructorViewModel vm = CreateVm(service, new NoOpTempDirectoryService());
        vm.SRSFilePath = Path.Combine(TempDir, "sample.srs");
        vm.MediaFilePath = Path.Combine(TempDir, "media.mkv");
        vm.OutputPath = Path.Combine(TempDir, "out.mkv");

        await vm.RebuildCommand.ExecuteAsync(null);

        Assert.Equal(1, service.RebuildCalls);
        Assert.True(vm.ResultSuccess);
        Assert.True(vm.ShowResult);
        Assert.False(vm.IsRebuilding);
        // Summary format: "CRC32 match: {ActualCRC:X8} ({ActualSize:N0} bytes)".
        Assert.Contains("0ABCDEF1", vm.ResultSummary);
        Assert.Contains(1234567L.ToString("N0"), vm.ResultSummary);
    }

    [Fact]
    // Failure result => ResultSuccess false and the summary is exactly the service's ErrorMessage.
    public async Task RebuildAsync_Failure_SetsSummaryToErrorMessage()
    {
        const string error = "CRC mismatch: sample does not match SRS.";
        var service = new FakeReconstructionService
        {
            Result = new SRSReconstructionResult(
                Success: false, CRCMatch: false,
                ExpectedCRC: 0u, ActualCRC: 0u,
                ExpectedSize: 0L, ActualSize: 0L, ErrorMessage: error),
        };
        SRSReconstructorViewModel vm = CreateVm(service, new NoOpTempDirectoryService());
        vm.SRSFilePath = Path.Combine(TempDir, "sample.srs");
        vm.MediaFilePath = Path.Combine(TempDir, "media.mkv");
        vm.OutputPath = Path.Combine(TempDir, "out.mkv");

        await vm.RebuildCommand.ExecuteAsync(null);

        Assert.Equal(1, service.RebuildCalls);
        Assert.False(vm.ResultSuccess);
        Assert.True(vm.ShowResult);
        Assert.False(vm.IsRebuilding);
        Assert.Equal(error, vm.ResultSummary);
    }

    [Fact]
    // Service throws => caught, ResultSuccess false, summary carries the exception message,
    // and IsRebuilding is reset in the finally block. Non-ISO path (no temp dir involved).
    public async Task RebuildAsync_ServiceThrows_SetsFailureAndResetsBusyFlag()
    {
        var service = new FakeReconstructionService
        {
            ThrowOnRebuild = new InvalidOperationException("rebuild blew up"),
        };
        SRSReconstructorViewModel vm = CreateVm(service, new NoOpTempDirectoryService());
        vm.SRSFilePath = Path.Combine(TempDir, "sample.srs");
        vm.MediaFilePath = Path.Combine(TempDir, "media.mkv");
        vm.OutputPath = Path.Combine(TempDir, "out.mkv");

        await vm.RebuildCommand.ExecuteAsync(null);

        Assert.Equal(1, service.RebuildCalls);
        Assert.False(vm.ResultSuccess);
        Assert.True(vm.ShowResult);
        Assert.False(vm.IsRebuilding);
        Assert.Contains("rebuild blew up", vm.ResultSummary);
    }

    [Fact]
    // ISO path: a temp dir is created and _extractedTempFile is set before extraction begins.
    // Pointing at a bogus SRS makes the real extractor throw, exercising the catch + finally so we
    // can verify the cleanup service is invoked and busy state is reset on the ISO failure path.
    public async Task RebuildAsync_IsoSourceFailure_InvokesTempDirCleanup_AndResetsBusyFlag()
    {
        var service = new FakeReconstructionService(); // never reached on this path
        var tempDir = new RecordingTempDirectoryService(TempDir);
        SRSReconstructorViewModel vm = CreateVm(service, tempDir);

        // Bogus (non-SRS) file so ISOMediaExtractor.ExtractMatchingVobSetAsync throws on load.
        string bogusSrs = Path.Combine(TempDir, "bogus.srs");
        File.WriteAllText(bogusSrs, "not an srs file");
        string bogusIso = Path.Combine(TempDir, "image.iso");
        File.WriteAllText(bogusIso, "not an iso");

        vm.IsISOSource = true;
        vm.ISOFilePath = bogusIso;
        vm.SRSFilePath = bogusSrs;
        vm.OutputPath = Path.Combine(TempDir, "out.vob");

        await vm.RebuildCommand.ExecuteAsync(null);

        // Extraction failed before the service was ever called.
        Assert.Equal(0, service.RebuildCalls);
        // finally -> CleanupTempFile -> _tempDir.Cleanup(...) for the dir created in the ISO branch.
        Assert.Equal(1, tempDir.CleanupCalls);
        Assert.False(vm.ResultSuccess);
        Assert.True(vm.ShowResult);
        Assert.False(vm.IsRebuilding);
    }
}
