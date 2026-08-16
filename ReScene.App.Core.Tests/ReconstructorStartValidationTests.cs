using ReScene.App.Core.Services;
using ReScene.App.Core.ViewModels;
using ReScene.App.Core.ViewModels.Reconstruction;
using ReScene.Core;

namespace ReScene.App.Core.Tests;

/// <summary>
/// Pins where the start gauntlet assigns <c>_verificationSnapshot</c>.
/// </summary>
/// <remarks>
/// The assignment happens at the PARSE point, before the rejections that follow it — missing imported
/// input files, a declined output-cleanup confirmation, and a cleanup failure. So a run rejected
/// after parsing still leaves the NEWLY parsed snapshot in place, not the previous run's. That is
/// current behaviour and it is easy to lose: moving the validation behind a result object that
/// returns the snapshot only on success would silently change it, with nothing objecting.
/// <para>
/// Whether that behaviour is desirable is a separate question. "Retain snapshots only from accepted
/// starts" would be a behaviour fix; these tests exist so that fix has to be a deliberate one.
/// </para>
/// </remarks>
public sealed class ReconstructorStartValidationTests : TempDirTestBase
{
    private sealed class InlineUiDispatcher : IUiDispatcher
    {
        public void Invoke(Action action) => action();
        public void Post(Action action) => action();
        public void Post(Action action, UiDispatcherPriority priority) => action();
        public bool CheckAccess() => true;
    }

    /// <summary>
    /// Declines every confirmation and records the titles it was asked, so a test can prove WHICH
    /// prompt rejected the run rather than merely that one did.
    /// </summary>
    private sealed class DecliningDialog : NoOpFileDialogService
    {
        public List<string> ConfirmTitles { get; } = [];

        public override Task<bool> ShowConfirmAsync(string title, string message)
        {
            ConfirmTitles.Add(title);
            return Task.FromResult(false);
        }
    }

    private sealed class CountingBruteForceService : IBruteForceService
    {
        public event EventHandler<BruteForceProgressEventArgs>? Progress { add { } remove { } }
        public event EventHandler<BruteForceStatusChangedEventArgs>? StatusChanged { add { } remove { } }
        public event EventHandler<LogEventArgs>? LogMessage { add { } remove { } }
        public event EventHandler<FileCopyProgressEventArgs>? FileCopyProgress { add { } remove { } }
        public event EventHandler<CRCValidationProgressEventArgs>? CRCValidationProgress { add { } remove { } }
        public event EventHandler<TimestampPreservationFailedEventArgs>? TimestampPreservationFailed { add { } remove { } }

        public int RunCalls { get; private set; }

        public Task<BruteForceRunResult> RunAsync(BruteForceOptions options, CancellationToken cancellationToken = default)
        {
            RunCalls++;
            return Task.FromResult(new BruteForceRunResult(true, null));
        }
    }

    private static ReconstructorViewModel CreateVm(IFileDialogService dialog, out CountingBruteForceService brute)
    {
        brute = new CountingBruteForceService();
        return new ReconstructorViewModel(brute, dialog, new InlineUiDispatcher(), new TestUiTimerFactory(), settingsService: null);
    }

    /// <summary>Writes an SFV naming one volume, and returns its path.</summary>
    private string WriteSfv(string fileName, string volumeName)
    {
        string path = Path.Combine(TempDir, fileName);
        File.WriteAllText(path, $"{volumeName} 00000000\n");
        return path;
    }

    private async Task SetWinRARWithVersionAsync(ReconstructorViewModel vm)
    {
        string dir = Path.Combine(TempDir, "winrar");
        Directory.CreateDirectory(Path.Combine(dir, "winrar-500"));
        string exe = Path.Combine(dir, "winrar-500", OperatingSystem.IsWindows() ? "rar.exe" : "rar");
        File.WriteAllText(exe, string.Empty);
        vm.WinRARPath = dir;
        if (vm.LastVersionScan is { } scan)
        {
            await scan;
        }
    }

    private async Task<(ReconstructorViewModel Vm, CountingBruteForceService Brute)> ArrangeStartableVmAsync(
        IFileDialogService dialog, string verificationPath)
    {
        ReconstructorViewModel vm = CreateVm(dialog, out CountingBruteForceService brute);
        await SetWinRARWithVersionAsync(vm);
        vm.ReleasePath = Path.Combine(TempDir, "release");
        Directory.CreateDirectory(vm.ReleasePath);
        vm.OutputPath = TempDir;
        vm.VerificationPath = verificationPath;
        return (vm, brute);
    }

    [Fact]
    public async Task Start_ParsesTheVerificationFile_AndTheRunnerReadsThatSnapshot()
    {
        // The successful half: the parsed snapshot must reach the RUNNER, not merely be stored.
        // Asserting only the stored field would still pass if BuildSharedSettingsAsync stopped
        // consuming it, which is precisely the read this task has to protect.
        (ReconstructorViewModel vm, CountingBruteForceService brute) = await ArrangeStartableVmAsync(
            new NoOpFileDialogService(), WriteSfv("first.sfv", "first.rar"));

        await vm.StartCommand.ExecuteAsync(null);

        Assert.True(brute.RunCalls > 0, "the run must actually have started for this to mean anything");
        Assert.Equal(["first.rar"], vm.VerificationSnapshotForTest!.VolumeNames);

        // The handoff itself, at the point the runner reads it.
        SharedReconstructionSettings settings = await vm.BuildSharedSettingsAsync(CancellationToken.None);
        Assert.Equal(["first.rar"], settings.Verification.VolumeNames);
    }

    [Fact]
    public async Task Start_RejectedAfterParsing_StillReplacesTheSnapshotWithTheNewlyParsedOne()
    {
        // The surprising half, on ONE view-model so the claim is genuinely "the previous run's
        // snapshot was REPLACED" rather than "a fresh instance parsed its own file".
        //
        // The snapshot is assigned at the parse point, BEFORE the output-cleanup confirmation - so
        // declining that confirmation rejects the run while leaving the second file's snapshot
        // installed. Assigning it only on an accepted start would leave the first file's snapshot in
        // place, which is the change this test exists to catch.
        var dialog = new DecliningDialog();
        (ReconstructorViewModel vm, CountingBruteForceService brute) = await ArrangeStartableVmAsync(
            dialog, WriteSfv("first.sfv", "first.rar"));

        await vm.StartCommand.ExecuteAsync(null);

        // Run one needed no confirmation - the output directory held no artifacts yet.
        Assert.Empty(dialog.ConfirmTitles);
        Assert.Equal(["first.rar"], vm.VerificationSnapshotForTest!.VolumeNames);
        int runCallsAfterTheAcceptedStart = brute.RunCalls;
        Assert.True(runCallsAfterTheAcceptedStart > 0);

        // Reconstruction artifacts in the output directory make the next start prompt.
        Directory.CreateDirectory(Path.Combine(TempDir, "output"));
        File.WriteAllText(Path.Combine(TempDir, "output", "prior.rar"), "keep me");
        Assert.True(vm.OutputHasReconstructionArtifacts(), "the fixture must reach the cleanup prompt");

        vm.VerificationPath = WriteSfv("second.sfv", "second.rar");

        await vm.StartCommand.ExecuteAsync(null);

        // WHICH prompt rejected it, and that no run followed. IsRunning would be false either way -
        // an accepted, completed run ends false too - so it proves nothing here and is not asserted.
        Assert.Equal(["Output Directory Not Empty"], dialog.ConfirmTitles);
        Assert.Equal(runCallsAfterTheAcceptedStart, brute.RunCalls);
        Assert.Contains(vm.LogEntries, l => l.Contains("Cancelled: output directory not empty", StringComparison.Ordinal));

        // Rejected - and yet the first run's snapshot is gone, replaced by the rejected run's.
        Assert.Equal(["second.rar"], vm.VerificationSnapshotForTest!.VolumeNames);
    }
}
