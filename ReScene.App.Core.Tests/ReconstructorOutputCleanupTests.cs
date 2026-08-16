using System.Collections.Specialized;
using ReScene.App.Core.Services;
using ReScene.App.Core.ViewModels;
using ReScene.App.Core.ViewModels.Reconstruction;
using ReScene.Core;
using ReScene.SRR;

namespace ReScene.App.Core.Tests;

/// <summary>
/// Covers the scoped pre-run cleanup and the plan-before-mutate ordering: the confirm text names the
/// two reserved subtrees; clearing preserves unrelated root files; and a run rejected by the preflight
/// (multi-set custom packer) never reaches the cleanup, so prior output survives (cases i, j).
/// </summary>
public sealed class ReconstructorOutputCleanupTests : TempDirTestBase
{
    private sealed class InlineUiDispatcher : IUiDispatcher
    {
        public void Invoke(Action action) => action();
        public void Post(Action action) => action();
        public void Post(Action action, UiDispatcherPriority priority) => action();
        public bool CheckAccess() => true;
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

    private sealed class RecordingDialog : NoOpFileDialogService
    {
        public List<(string Title, string Message)> Errors { get; } = [];

        /// <summary>Optional side-channel so a test can order this against other effects.</summary>
        public Action? OnShowError { get; set; }

        public override void ShowError(string title, string message)
        {
            Errors.Add((title, message));
            OnShowError?.Invoke();
        }
    }

    private static ReconstructorViewModel CreateVm(IBruteForceService? brute = null, IFileDialogService? dialog = null) =>
        new(brute ?? new CountingBruteForceService(), dialog ?? new NoOpFileDialogService(),
            new InlineUiDispatcher(), new TestUiTimerFactory(), settingsService: null);

    private async Task SetWinRARWithVersionAsync(ReconstructorViewModel vm)
    {
        string dir = Path.Combine(TempDir, "winrar");
        Directory.CreateDirectory(Path.Combine(dir, "winrar-500"));
        // The scanner looks for the platform's console binary (rar.exe on Windows, rar elsewhere),
        // so stub whichever name this OS resolves to — otherwise the folder scans as version-less
        // and StartAsync fails on the "no WinRAR versions" guard before reaching the branch under test.
        File.WriteAllText(Path.Combine(dir, "winrar-500", RarExecutable.FileName), "stub");
        vm.WinRARPath = dir;
        if (vm.LastVersionScan is { } scan)
        {
            await scan;
        }
    }

    // ── (j) confirm text + preservation ────────────────────────

    [Fact]
    public void OutputCleanupConfirmText_NamesBothReservedSubtrees()
    {
        string text = ReconstructorViewModel.OutputCleanupConfirmText(@"C:\out");

        Assert.Contains("output", text, StringComparison.Ordinal);
        Assert.Contains(".rescene-work", text, StringComparison.Ordinal);
    }

    [Fact]
    public void ClearReservedSubtrees_ClearsReservedTrees_PreservesUnrelatedRootFiles()
    {
        string keep = Path.Combine(TempDir, "keep.txt");
        File.WriteAllText(keep, "user file");
        Directory.CreateDirectory(Path.Combine(TempDir, "output"));
        File.WriteAllText(Path.Combine(TempDir, "output", "old.rar"), "stale");
        Directory.CreateDirectory(Path.Combine(TempDir, ".rescene-work", "junk"));
        File.WriteAllText(Path.Combine(TempDir, ".rescene-work", "junk", "x"), "stale");

        ReconstructorViewModel vm = CreateVm();
        vm.OutputPath = TempDir;

        Assert.True(vm.OutputHasReconstructionArtifacts());
        Assert.True(vm.ClearReservedSubtrees());

        Assert.False(Directory.Exists(Path.Combine(TempDir, "output")));
        Assert.False(Directory.Exists(Path.Combine(TempDir, ".rescene-work")));
        Assert.True(File.Exists(keep)); // unrelated root file survives
    }

    [Fact]
    public void ClearReservedSubtrees_WhenCleanupFails_LogsThenShowsAnError_AndReturnsFalse()
    {
        // The failure path does THREE things, in this order: it logs, it shows an error dialog, and
        // it returns false. Only the success path was tested, so an extraction could have dropped the
        // dialog - collapsing the log and the error into one callback, say - with nothing objecting.
        //
        // The failure is forced by an EMPTY output path, so ResolveReservedRoots throws
        // ArgumentException out of Path.GetFullPath. That is deterministic on every platform, unlike
        // holding an open handle to make the delete itself fail: POSIX happily unlinks an open file,
        // so that fixture would pass on Windows and fail on Linux and macOS. The catch does not
        // distinguish a resolution failure from a delete failure, so either exercises the same path.
        List<string> effects = [];
        var dialog = new RecordingDialog { OnShowError = () => effects.Add("dialog") };
        ReconstructorViewModel vm = CreateVm(dialog: dialog);
        vm.LogEntries.CollectionChanged += (_, e) =>
        {
            if (e.Action == NotifyCollectionChangedAction.Add)
            {
                effects.Add("log");
            }
        };

        vm.OutputPath = string.Empty;

        bool result = vm.ClearReservedSubtrees();

        Assert.False(result);
        Assert.Contains(vm.LogEntries, l => l.Contains("Failed to clean output directory", StringComparison.Ordinal));
        Assert.Contains(dialog.Errors, e => e.Title == "Error"
            && e.Message.Contains("Failed to clean output directory", StringComparison.Ordinal));

        // The ORDER, not merely that both happened: swapping the two production calls passes every
        // assertion above. The dispatcher runs posted work inline, so the log line lands
        // synchronously and the two effects interleave in real call order.
        Assert.Equal(["log", "dialog"], effects);
    }

    [Fact]
    public void OutputHasReconstructionArtifacts_OnlyUnrelatedRootFile_IsFalse()
    {
        File.WriteAllText(Path.Combine(TempDir, "keep.txt"), "user file");

        ReconstructorViewModel vm = CreateVm();
        vm.OutputPath = TempDir;

        Assert.False(vm.OutputHasReconstructionArtifacts());
    }

    // ── (i) multi-set custom packer is rejected before any cleanup ──

    [Fact]
    public async Task Start_MultiSetCustomPacker_RejectedBeforeCleanup_PriorOutputSurvives()
    {
        // Prior reconstruction output that a rejected run must NOT erase.
        Directory.CreateDirectory(Path.Combine(TempDir, "output"));
        string prior = Path.Combine(TempDir, "output", "prior.rar");
        File.WriteAllText(prior, "keep me");

        var dialog = new RecordingDialog();
        var brute = new CountingBruteForceService();
        ReconstructorViewModel vm = CreateVm(brute, dialog);
        await SetWinRARWithVersionAsync(vm);
        vm.ReleasePath = Path.Combine(TempDir, "release");
        Directory.CreateDirectory(vm.ReleasePath);
        vm.OutputPath = TempDir;
        vm.SetImportStateForTest(new ReconstructionImportState
        {
            ArchiveSets = [MakeSet("a"), MakeSet("b")],
            CustomPackerType = CustomPackerType.AllOnesWithLargeFlag,
        });

        await vm.StartCommand.ExecuteAsync(null);

        Assert.Contains(dialog.Errors, e => e.Message.Contains("custom packer", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(0, brute.RunCalls);       // the run never started
        Assert.True(File.Exists(prior));        // prior output untouched by the rejected run
    }

    private static SRRArchiveSet MakeSet(string key)
    {
        var set = new SRRArchiveSet { Key = key, Directory = "" };
        set.VolumeNames.Add(key + ".rar");
        return set;
    }
}
