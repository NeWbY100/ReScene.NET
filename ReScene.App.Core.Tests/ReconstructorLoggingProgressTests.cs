using System.Collections.Concurrent;
using System.ComponentModel;
using ReScene.App.Core.Services;
using ReScene.App.Core.ViewModels;
using ReScene.App.Core.ViewModels.Reconstruction;
using ReScene.Core;
using ReScene.Core.IO;
using ReScene.SRR;

namespace ReScene.App.Core.Tests;

/// <summary>
/// Concurrency- and generation-focused coverage for the RAR Reconstructor's run finalisation:
/// the once-per-run timestamp summary raised from the run's <c>finally</c> (#19), the generation-safe
/// batched log flush (#20), and the set/attempt-labelled progress message (#24). These exercise the
/// thread-safety of <c>_timestampFailures</c>, the atomic flush flag + run-generation token on the log
/// queue, and the seed→full stage labelling that stops progress reading as a rewind within a set.
/// </summary>
public sealed class ReconstructorLoggingProgressTests : TempDirTestBase
{
    // ── Fakes ───────────────────────────────────────────────

    /// <summary>Brute-force service with real events the test can raise, plus a scriptable run handler.</summary>
    private sealed class RaisingBruteForceService : IBruteForceService
    {
        public event EventHandler<BruteForceProgressEventArgs>? Progress;
        public event EventHandler<BruteForceStatusChangedEventArgs>? StatusChanged;
        public event EventHandler<LogEventArgs>? LogMessage;
        /// <summary>
        /// A REAL event, not a discarding accessor: the busy-flag tests drive the copy handler
        /// through it. <see cref="CRCValidationProgress"/> below is real for the same reason. Neither
        /// fires unless a test calls its raiser, so the rest of this file is unaffected.
        /// </summary>
        public event EventHandler<FileCopyProgressEventArgs>? FileCopyProgress;
        public event EventHandler<CRCValidationProgressEventArgs>? CRCValidationProgress;
        public event EventHandler<TimestampPreservationFailedEventArgs>? TimestampPreservationFailed;

        public Func<BruteForceOptions, BruteForceRunResult>? OnRun { get; set; }

        public void RaiseProgress(BruteForceProgressEventArgs e) => Progress?.Invoke(this, e);
        public void RaiseFileCopyProgress(string fileName) =>
            FileCopyProgress?.Invoke(this, new FileCopyProgressEventArgs { FileName = fileName, FilesCopied = 1, TotalFiles = 2 });
        public void RaiseCrcProgress(string fileName) =>
            CRCValidationProgress?.Invoke(this, new CRCValidationProgressEventArgs
            {
                FileName = fileName,
                FilesVerified = 1,
                TotalFiles = 2,
                BytesVerified = 1,
                TotalBytes = 2,
            });
        public void RaiseStatusCompleted() => StatusChanged?.Invoke(this, new BruteForceStatusChangedEventArgs(OperationStatus.Completed));
        public void RaiseLog(LogTarget target, string message) => LogMessage?.Invoke(this, new LogEventArgs(message, target));
        public void RaiseTimestampFailure(string dest, string error) =>
            TimestampPreservationFailed?.Invoke(this, new TimestampPreservationFailedEventArgs { DestinationPath = dest, ErrorMessage = error });

        public Task<BruteForceRunResult> RunAsync(BruteForceOptions options, CancellationToken cancellationToken = default)
            => Task.FromResult(OnRun?.Invoke(options) ?? new BruteForceRunResult(true, new WinningCombo(500, [])));
    }

    /// <summary>Runs everything inline on the calling thread (both Invoke and Post).</summary>
    private sealed class InlineUiDispatcher : IUiDispatcher
    {
        public void Invoke(Action action) => action();
        public void Post(Action action) => action();
        public void Post(Action action, UiDispatcherPriority priority) => action();
        public bool CheckAccess() => true;
    }

    /// <summary>
    /// Counts UI-dispatch calls. <see cref="Invoke"/> always runs inline (and is counted); <see cref="Post(Action)"/>
    /// is counted and either runs inline or is deferred onto a queue drained by <see cref="Pump"/> — so a
    /// test can observe how many batched log flushes were scheduled and control exactly when they run.
    /// </summary>
    private sealed class CountingUiDispatcher(bool deferPosts) : IUiDispatcher
    {
        private readonly Queue<Action> _deferred = new();
        public int PostCount { get; private set; }
        public int InvokeCount { get; private set; }

        public void Invoke(Action action)
        {
            InvokeCount++;
            action();
        }

        public void Post(Action action)
        {
            PostCount++;
            if (deferPosts)
            {
                _deferred.Enqueue(action);
            }
            else
            {
                action();
            }
        }

        public void Post(Action action, UiDispatcherPriority priority) => Post(action);
        public bool CheckAccess() => true;

        public void Pump()
        {
            while (_deferred.Count > 0)
            {
                _deferred.Dequeue()();
            }
        }
    }

    /// <summary>Records warning dialogs so a test can assert the timestamp summary fires exactly once.</summary>
    private sealed class RecordingFileDialogService : NoOpFileDialogService
    {
        public List<(string Title, string Message)> Warnings { get; } = [];
        public override void ShowWarning(string title, string message) => Warnings.Add((title, message));
    }

    // ── Helpers ─────────────────────────────────────────────

    private static ReconstructorViewModel CreateVm(RaisingBruteForceService brute, IUiDispatcher dispatcher, IFileDialogService? dialog = null) =>
        new(brute, dialog ?? new NoOpFileDialogService(), dispatcher, new TestUiTimerFactory(), settingsService: null);

    // ── Busy-flag clear order and the staleness gate ─────────
    //
    // Two halves of one invariant, and neither was tested. ModalProgressWindowController is
    // constructed per busy flag and only ever sees its own IsCopying/IsVerifying - never IsRunning.
    // So clearing IsRunning FIRST is what makes the `if (!IsRunning) return;` gates in the queued
    // progress handlers reject a late event that would otherwise re-raise IsCopying and re-open a
    // window the user just watched close.
    //
    // That means the gates must read the LIVE flag, never a snapshot captured before the clear, and
    // the finally's clears must stay in their order. Task 9b moves the run loop out from under this,
    // which is why these are pinned first.

    [Fact]
    public async Task RunFinally_WritesElapsedThenClearsIsRunning_BeforeBothGuardedBusyFlags()
    {
        // Records the property VALUE at each notification, not just the name, so the assertions are
        // about "the clear" rather than "the last time this name appeared". IsRunning is set true
        // BEFORE subscribing so its setup notification never enters the sequence.
        //
        // Both busy flags are raised during the run and both are asserted: covering only IsCopying
        // would let an IsVerifying = false inserted above IsRunning = false survive untouched.
        var brute = new RaisingBruteForceService();
        ReconstructorViewModel vm = CreateVm(brute, new InlineUiDispatcher());
        ConfigureRunnablePaths(vm);
        vm.SetImportStateForTest(ImportWith(MakeSet("a", "a.rar")));

        brute.OnRun = o =>
        {
            brute.RaiseFileCopyProgress("a.rar");
            brute.RaiseCrcProgress("a.rar");
            return WriteBruteSuccess(o, "a.rar");
        };

        vm.IsRunning = true;

        List<string> order = [];
        vm.PropertyChanged += (_, e) =>
        {
            switch (e.PropertyName)
            {
                case nameof(vm.ElapsedText):
                    order.Add("ElapsedText");
                    break;
                case nameof(vm.IsRunning):
                    order.Add($"IsRunning={vm.IsRunning}");
                    break;
                case nameof(vm.IsCopying):
                    order.Add($"IsCopying={vm.IsCopying}");
                    break;
                case nameof(vm.IsVerifying):
                    order.Add($"IsVerifying={vm.IsVerifying}");
                    break;
                default:
                    break;
            }
        };

        await vm.ExecuteReconstructionForTestAsync(CancellationToken.None);

        string seq = string.Join(", ", order);

        // Both guarded clears must actually have run, or the ordering assertions are vacuous.
        Assert.False(vm.IsCopying, seq);
        Assert.False(vm.IsVerifying, seq);

        int elapsed = order.LastIndexOf("ElapsedText");
        int runningCleared = order.IndexOf("IsRunning=False");
        int copyingCleared = order.IndexOf("IsCopying=False");
        int verifyingCleared = order.IndexOf("IsVerifying=False");
        Assert.True(elapsed >= 0 && runningCleared >= 0 && copyingCleared >= 0 && verifyingCleared >= 0,
            $"missing notifications: [{seq}]");

        // ElapsedText is written before IsRunning clears...
        Assert.True(elapsed < runningCleared, $"ElapsedText must precede the IsRunning clear: [{seq}]");

        // ...and IsRunning clears before EITHER guarded busy flag, which is what lets the staleness
        // gates reject a late queued event instead of re-opening a closed progress window.
        Assert.True(runningCleared < copyingCleared, $"IsRunning must clear before IsCopying: [{seq}]");
        Assert.True(runningCleared < verifyingCleared, $"IsRunning must clear before IsVerifying: [{seq}]");
    }

    [Fact]
    public async Task LateQueuedCopyProgress_IsRejectedByTheStalenessGate_AndCannotReopenTheWindow()
    {
        // The queued handler lands AFTER the run's finally cleared IsRunning. The gate must reject
        // it: re-raising IsCopying here re-opens a progress window with no run behind it.
        var brute = new RaisingBruteForceService();
        var dispatcher = new CountingUiDispatcher(deferPosts: true);
        ReconstructorViewModel vm = CreateVm(brute, dispatcher);
        ConfigureRunnablePaths(vm);
        vm.SetImportStateForTest(ImportWith(MakeSet("a", "a.rar")));

        brute.OnRun = o =>
        {
            brute.RaiseFileCopyProgress("a.rar");   // deferred, not run
            return WriteBruteSuccess(o, "a.rar");
        };

        vm.IsRunning = true;   // ExecuteReconstructionForTestAsync enters below StartAsync's IsRunning = true
        await vm.ExecuteReconstructionForTestAsync(CancellationToken.None);

        Assert.False(vm.IsRunning);
        Assert.False(vm.IsCopying);

        dispatcher.Pump();   // the copy handler finally runs, after the run is over

        Assert.False(vm.IsCopying, "a late queued copy event must not re-raise IsCopying");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ProgressDuringALiveRun_DoesRaiseTheBusyFlag(bool copyNotVerify)
    {
        // The POSITIVE CONTROL for the two staleness-gate tests below. Without it, asserting that a
        // late event leaves the flag false would pass even if the gate were deleted and the handler
        // could never raise the flag at all - the assertion would be true for the wrong reason.
        var brute = new RaisingBruteForceService();
        ReconstructorViewModel vm = CreateVm(brute, new InlineUiDispatcher());
        ConfigureRunnablePaths(vm);
        vm.SetImportStateForTest(ImportWith(MakeSet("a", "a.rar")));

        bool sawFlagRaised = false;
        brute.OnRun = o =>
        {
            if (copyNotVerify)
            {
                brute.RaiseFileCopyProgress("a.rar");
                sawFlagRaised = vm.IsCopying;
            }
            else
            {
                brute.RaiseCrcProgress("a.rar");
                sawFlagRaised = vm.IsVerifying;
            }

            return WriteBruteSuccess(o, "a.rar");
        };

        vm.IsRunning = true;
        await vm.ExecuteReconstructionForTestAsync(CancellationToken.None);

        Assert.True(sawFlagRaised, "a progress event during a LIVE run must raise its busy flag");
    }

    [Fact]
    public async Task LateQueuedCrcProgress_IsRejectedByTheStalenessGate_AndCannotReopenTheWindow()
    {
        // The verify gate is the copy gate's twin and is asserted separately: they are two distinct
        // `if (!IsRunning) return;` sites, and a change to one does not touch the other.
        var brute = new RaisingBruteForceService();
        var dispatcher = new CountingUiDispatcher(deferPosts: true);
        ReconstructorViewModel vm = CreateVm(brute, dispatcher);
        ConfigureRunnablePaths(vm);
        vm.SetImportStateForTest(ImportWith(MakeSet("a", "a.rar")));

        brute.OnRun = o =>
        {
            brute.RaiseCrcProgress("a.rar");   // deferred, not run
            return WriteBruteSuccess(o, "a.rar");
        };

        vm.IsRunning = true;
        await vm.ExecuteReconstructionForTestAsync(CancellationToken.None);

        Assert.False(vm.IsRunning);
        Assert.False(vm.IsVerifying);

        dispatcher.Pump();

        Assert.False(vm.IsVerifying, "a late queued CRC event must not re-raise IsVerifying");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void CopyAndCrcProgress_AreDeferredUntilTheUiQueueRuns(bool copyNotVerify)
    {
        // Both handlers marshal onto the UI thread; neither runs on the engine's callback thread.
        // What differs is whether the ENGINE CALLBACK WAITS. Copy and CRC use Post, so a burst of
        // per-file events returns immediately instead of stalling the engine on the UI thread.
        //
        // Nothing caught a Post -> Invoke change: through a whole run, an Invoke'd handler raises the
        // busy flag and the finally clears it again, so every other assertion still holds. Only the
        // TIMING differs, so this drives the handler directly rather than through a run.
        var brute = new RaisingBruteForceService();
        var dispatcher = new CountingUiDispatcher(deferPosts: true);
        ReconstructorViewModel vm = CreateVm(brute, dispatcher);
        vm.IsRunning = true;   // past the staleness gate

        if (copyNotVerify)
        {
            brute.RaiseFileCopyProgress("a.rar");
            Assert.False(vm.IsCopying, "the callback returned before the UI work ran, so it was Post'ed");
            dispatcher.Pump();
            Assert.True(vm.IsCopying, "control: the queued handler really existed and applied");
        }
        else
        {
            brute.RaiseCrcProgress("a.rar");
            Assert.False(vm.IsVerifying, "the callback returned before the UI work ran, so it was Post'ed");
            dispatcher.Pump();
            Assert.True(vm.IsVerifying, "control: the queued handler really existed and applied");
        }
    }

    [Fact]
    public void MainProgress_IsAppliedBeforeTheEngineCallbackReturns()
    {
        // The other half of the mix, and equally unguarded before this test: OnProgress uses Invoke,
        // which blocks the engine callback until the UI thread has applied the update - so the main
        // progress figures are current the moment the raise returns. Switching it to Post left all
        // 792 tests passing.
        var brute = new RaisingBruteForceService();
        var dispatcher = new CountingUiDispatcher(deferPosts: true);
        ReconstructorViewModel vm = CreateVm(brute, dispatcher);
        vm.IsRunning = true;

        brute.RaiseProgress(new BruteForceProgressEventArgs(
            releaseDirectoryPath: "rel", rarVersionDirectoryPath: "winrar-500",
            rarCommandLineArguments: "-m0", operationSize: 100, operationProgressed: 42,
            startDateTime: DateTime.UtcNow));

        // Asserted on the rendered text rather than a number, so the assertion is unambiguous about
        // WHICH event landed. Under Post this is still empty when the raise returns.
        Assert.Equal("Test 42 of 100", vm.TestCountText);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RunCompletion_SettlesTheProgressReadoutTo100Percent(bool succeed)
    {
        // The run's completion summary writes six pieces of bound state. Probing the runner's sink
        // found four of them - the percentage, its text, the test count and the message - guarded by
        // nothing at all, so an extraction could have dropped any of them and left the progress
        // readout frozen wherever the last engine event happened to leave it.
        var brute = new RaisingBruteForceService();
        ReconstructorViewModel vm = CreateVm(brute, new InlineUiDispatcher());
        ConfigureRunnablePaths(vm);
        vm.SetImportStateForTest(ImportWith(MakeSet("a", "a.rar")));

        string? midRunTestCount = null;
        brute.OnRun = o =>
        {
            // A mid-run event so the readout starts somewhere OTHER than the completion state.
            brute.RaiseProgress(new BruteForceProgressEventArgs(
                releaseDirectoryPath: "rel", rarVersionDirectoryPath: "winrar-500",
                rarCommandLineArguments: "-m0", operationSize: 100, operationProgressed: 7,
                startDateTime: DateTime.UtcNow));
            midRunTestCount = vm.TestCountText;
            return succeed ? WriteBruteSuccess(o, "a.rar") : new BruteForceRunResult(false, null);
        };

        vm.IsRunning = true;
        await vm.ExecuteReconstructionForTestAsync(CancellationToken.None);

        // Control: the readout really was somewhere else mid-run, so settling to 100% is a change.
        Assert.Equal("Test 7 of 100", midRunTestCount);

        Assert.Equal(100, vm.ProgressPercent);
        Assert.Equal("100%", vm.ProgressPercentText);
        Assert.Equal("Test 100 of 100", vm.TestCountText);
        Assert.Equal(succeed ? "Match found!" : "No match found.", vm.ProgressMessage);
    }

    /// <summary>The merged log flattened to one string — for contains/order asserts and diagnostics.</summary>
    private static string JoinedLog(ReconstructorViewModel vm) => string.Join(Environment.NewLine, vm.LogEntries);

    private static SRRArchiveSet MakeSet(string key, params string[] volumes)
    {
        var set = new SRRArchiveSet { Key = key, Directory = "" };
        foreach (string v in volumes)
        {
            set.VolumeNames.Add(v);
        }

        return set;
    }

    private static ReconstructionImportState ImportWith(params SRRArchiveSet[] sets) => new()
    {
        ArchiveSets = sets,
        OriginalRARFileNames = [.. sets.SelectMany(s => s.VolumeNames)],
    };

    private static BruteForceRunResult WriteBruteSuccess(BruteForceOptions options, string volumeName)
    {
        string dir = Path.Combine(options.OutputDirectoryPath, "output");
        Directory.CreateDirectory(dir);
        string file = Path.Combine(dir, volumeName);
        File.WriteAllText(file, "vol");
        var combo = new WinningCombo(500, []);
        return new BruteForceRunResult(true, combo) { Matches = [new CommittedMatch(combo, [file])] };
    }

    private void ConfigureRunnablePaths(ReconstructorViewModel vm)
    {
        vm.WinRARPath = TempDir;
        vm.ReleasePath = TempDir;
        vm.OutputPath = TempDir;
        vm.CompleteAllVolumes = false;
    }

    private static BruteForceProgressEventArgs MakeProgress(long progressed) =>
        new("release", "winrar-500", "a c -m0", operationSize: 100, operationProgressed: progressed, startDateTime: DateTime.Now)
        {
            PhaseDescription = "Phase 2: Full RAR Creation",
        };

    // Same combo key as MakeProgress, flagged CombinationFailed — mirrors the event the engine fires
    // when it could not run rar (e.g. binary not executable), which marks that row "Error".
    private static BruteForceProgressEventArgs MakeFailedProgress(long progressed) =>
        new("release", "winrar-500", "a c -m0", operationSize: 100, operationProgressed: progressed, startDateTime: DateTime.Now)
        {
            PhaseDescription = "Phase 2: Full RAR Creation",
            CombinationFailed = true,
        };

    // ── #19 — timestamp summary once, from the run's finally, thread-safe ──

    [Fact]
    public async Task TwoSetsEachFailTimestamp_ShowsExactlyOneSummary_FromFinally()
    {
        var brute = new RaisingBruteForceService();
        var dialog = new RecordingFileDialogService();
        ReconstructorViewModel vm = CreateVm(brute, new InlineUiDispatcher(), dialog);
        ConfigureRunnablePaths(vm);
        vm.SetImportStateForTest(ImportWith(MakeSet("a", "a.rar"), MakeSet("b", "b.rar")));

        brute.OnRun = o =>
        {
            string volume = o.RAROptions.OriginalRARFileNames[0];
            // Each underlying engine attempt reports a timestamp failure and a Completed status — the
            // pre-fix code showed the summary once per Completed (per attempt); the fix shows it once.
            brute.RaiseTimestampFailure(volume, "could not set mtime");
            brute.RaiseStatusCompleted();
            return WriteBruteSuccess(o, volume);
        };

        await vm.ExecuteReconstructionForTestAsync(CancellationToken.None);

        Assert.True(vm.LastRunSucceeded, JoinedLog(vm));
        (string Title, string Message) = Assert.Single(dialog.Warnings);
        Assert.Equal("Timestamp Preservation Failed", Title);
    }

    [Fact]
    public async Task Cancelled_WithTimestampFailures_StillShowsSummaryOnceFromFinally()
    {
        var brute = new RaisingBruteForceService();
        var dialog = new RecordingFileDialogService();
        ReconstructorViewModel vm = CreateVm(brute, new InlineUiDispatcher(), dialog);
        ConfigureRunnablePaths(vm);
        vm.SetImportStateForTest(ImportWith(MakeSet("a", "a.rar")));

        using var cts = new CancellationTokenSource();
        brute.OnRun = o =>
        {
            brute.RaiseTimestampFailure(o.RAROptions.OriginalRARFileNames[0], "could not set mtime");
            cts.Cancel();
            return new BruteForceRunResult(true, new WinningCombo(500, []));
        };

        await vm.ExecuteReconstructionForTestAsync(cts.Token);

        // The summary must fire from the finally on cancellation too, exactly once.
        (string Title, string Message) = Assert.Single(dialog.Warnings);
        Assert.Equal("Timestamp Preservation Failed", Title);
        Assert.Equal("Cancelled", vm.PhaseDescription);
    }

    // ── Completion heading carries the run-wide "N could not run" error aggregate (WCAG 4.1.3) ──

    [Fact]
    public async Task Completion_WithErroredCombo_HeadingIncludesCouldNotRunCount()
    {
        var brute = new RaisingBruteForceService();
        ReconstructorViewModel vm = CreateVm(brute, new InlineUiDispatcher());
        ConfigureRunnablePaths(vm);
        vm.SetImportStateForTest(ImportWith(MakeSet("a", "a.rar")));

        brute.OnRun = _ =>
        {
            // A combination begins testing, then the engine reports it could not run rar → its row is
            // marked "Error"; the set produces no match.
            brute.RaiseProgress(MakeProgress(1));
            brute.RaiseProgress(MakeFailedProgress(1));
            return new BruteForceRunResult(false, null);
        };

        await vm.ExecuteReconstructionForTestAsync(CancellationToken.None);

        Assert.Equal(1, vm.VersionEntries.Count(v => v.Status == "Error"));
        // The completion status (a Polite live region) names the aggregate, not just per-cell "Run failed".
        Assert.StartsWith("Complete — No Match", vm.PhaseDescription, StringComparison.Ordinal);
        Assert.Contains("(1 could not run)", vm.PhaseDescription, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Completion_WithErroredCombo_LogCarriesCouldNotRunAggregate()
    {
        // The scannable "did anything fail?" marker at the end of the merged log, matching the
        // completion heading's "(N could not run)"; the per-failure [P2] WARNINGs sit earlier in the
        // same log, so the line points up rather than at a separate pane.
        var brute = new RaisingBruteForceService();
        ReconstructorViewModel vm = CreateVm(brute, new InlineUiDispatcher());
        ConfigureRunnablePaths(vm);
        vm.SetImportStateForTest(ImportWith(MakeSet("a", "a.rar")));

        brute.OnRun = _ =>
        {
            brute.RaiseProgress(MakeProgress(1));
            brute.RaiseProgress(MakeFailedProgress(1));
            return new BruteForceRunResult(false, null);
        };

        await vm.ExecuteReconstructionForTestAsync(CancellationToken.None);

        Assert.Contains(vm.LogEntries, l => l.Contains("1 combination(s) could not run", StringComparison.Ordinal));
        Assert.Contains(vm.LogEntries, l => l.Contains("each failure is logged above", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RunStart_LogsTagLegend_BeforeAnyPhaseLine()
    {
        // The [P1]/[P2] provenance tags need a live, in-log legend (a11y: a reader of the on-screen log
        // never sees a saved-file header) — logged at the start of the run block, before any engine
        // phase line can appear.
        var brute = new RaisingBruteForceService();
        ReconstructorViewModel vm = CreateVm(brute, new InlineUiDispatcher());
        ConfigureRunnablePaths(vm);
        vm.SetImportStateForTest(ImportWith(MakeSet("a", "a.rar")));
        brute.OnRun = o => WriteBruteSuccess(o, o.RAROptions.OriginalRARFileNames[0]);

        await vm.ExecuteReconstructionForTestAsync(CancellationToken.None);

        int legendIndex = IndexOfContains(vm, "[P1] = Phase 1 (comment filtering), [P2] = Phase 2 (RAR creation)");
        int startIndex = IndexOfContains(vm, "Starting brute-force...");
        Assert.True(legendIndex >= 0, "legend line missing:" + Environment.NewLine + JoinedLog(vm));
        Assert.True(legendIndex < startIndex, "legend must precede the run narrative");

        static int IndexOfContains(ReconstructorViewModel vm, string fragment)
        {
            for (int i = 0; i < vm.LogEntries.Count; i++)
            {
                if (vm.LogEntries[i].Contains(fragment, StringComparison.Ordinal))
                {
                    return i;
                }
            }

            return -1;
        }
    }

    [Fact]
    public async Task TimestampFailures_ConcurrentAddWhileSummarising_IsLockGuarded()
    {
        var brute = new RaisingBruteForceService();
        ReconstructorViewModel vm = CreateVm(brute, new InlineUiDispatcher());

        const int count = 2000;
        using var start = new ManualResetEventSlim(false);

        var adder = Task.Run(() =>
        {
            start.Wait();
            for (int i = 0; i < count; i++)
            {
                brute.RaiseTimestampFailure($"file-{i}", "err");
            }
        });

        var summariser = Task.Run(() =>
        {
            start.Wait();
            // Concurrently snapshot-and-render the summary. An unguarded List<T> would throw
            // "Collection was modified" here; the lock + snapshot-under-lock keeps it safe.
            for (int i = 0; i < count; i++)
            {
                vm.ShowTimestampSummaryForTest();
            }
        });

        start.Set();
        await Task.WhenAll(adder, summariser).WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(count, vm.TimestampFailureCountForTest);
    }

    // ── #20 — generation-safe batched log ──

    [Fact]
    public void ManyLogEvents_CoalesceIntoAtMostOneDispatch_ThenFlushWritesEveryLine()
    {
        var brute = new RaisingBruteForceService();
        var dispatcher = new CountingUiDispatcher(deferPosts: true);
        ReconstructorViewModel vm = CreateVm(brute, dispatcher);

        const int n = 300;
        int postsBefore = dispatcher.PostCount;
        for (int i = 0; i < n; i++)
        {
            brute.RaiseLog(LogTarget.System, $"line {i}");
        }

        // N log events schedule at most one pending flush (the atomic flag coalesces the rest) — far
        // below N — and nothing is applied to the bound log until that flush runs.
        Assert.True(dispatcher.PostCount - postsBefore <= 2,
            $"expected <=2 flush dispatches for {n} events, got {dispatcher.PostCount - postsBefore}");
        Assert.DoesNotContain(vm.LogEntries, l => l.Contains("line 0", StringComparison.Ordinal));

        dispatcher.Pump();

        for (int i = 0; i < n; i++)
        {
            string expected = $"line {i}";
            Assert.Contains(vm.LogEntries, l => l.EndsWith(expected, StringComparison.Ordinal));
        }
    }

    [Fact]
    public void StaleFlushFromPriorGeneration_DoesNotRepopulate_AfterStartClearsAndBumps()
    {
        var brute = new RaisingBruteForceService();
        var dispatcher = new CountingUiDispatcher(deferPosts: true);
        ReconstructorViewModel vm = CreateVm(brute, dispatcher);

        // Prior run enqueues a line and schedules a flush that has not run yet.
        brute.RaiseLog(LogTarget.System, "stale line");

        // A new run begins: it clears the visible log and bumps the run generation.
        vm.BeginNewLogGenerationForTest();
        Assert.Empty(vm.LogEntries);

        // A fresh line of the NEW generation is enqueued.
        brute.RaiseLog(LogTarget.System, "fresh line");

        // Draining now runs the stale flush (captured the old generation) and the fresh one: the stale
        // batch must be discarded so it cannot repopulate the cleared log; the fresh line lands once.
        dispatcher.Pump();

        Assert.DoesNotContain(vm.LogEntries, l => l.Contains("stale line", StringComparison.Ordinal));
        Assert.Contains(vm.LogEntries, l => l.Contains("fresh line", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RunEnd_SynchronousFinalDrain_LeavesNoQueuedLineUnwritten()
    {
        var brute = new RaisingBruteForceService();
        var dispatcher = new CountingUiDispatcher(deferPosts: true);
        ReconstructorViewModel vm = CreateVm(brute, dispatcher);
        ConfigureRunnablePaths(vm);
        vm.SetImportStateForTest(ImportWith(MakeSet("a", "a.rar")));
        brute.OnRun = o => WriteBruteSuccess(o, o.RAROptions.OriginalRARFileNames[0]);

        await vm.ExecuteReconstructionForTestAsync(CancellationToken.None);

        // Every batched flush was deferred (never pumped); the run's finally must drain the queue
        // synchronously so the run's log lines are present without the dispatcher ever running a flush.
        Assert.Contains(vm.LogEntries, l => l.Contains("Starting brute-force...", StringComparison.Ordinal));
        Assert.Contains(vm.LogEntries, l => l.Contains("Brute-force completed", StringComparison.Ordinal));
    }

    [Fact]
    public void BatchedFlush_PreservesChronologicalOrder_AndTagsPhaseLines()
    {
        var brute = new RaisingBruteForceService();
        var dispatcher = new CountingUiDispatcher(deferPosts: true);
        ReconstructorViewModel vm = CreateVm(brute, dispatcher);

        // Interleave targets: the merged log must keep the exact global enqueue order.
        brute.RaiseLog(LogTarget.System, "sys 1");
        brute.RaiseLog(LogTarget.Phase1, "p1 1");
        brute.RaiseLog(LogTarget.System, "sys 2");
        brute.RaiseLog(LogTarget.Phase2, "p2 1");
        brute.RaiseLog(LogTarget.Phase1, "p1 2");
        brute.RaiseLog(LogTarget.System, "sys 3");

        dispatcher.Pump();

        // One merged log in exact enqueue (chronological) order; phase lines carry their [P1]/[P2]
        // provenance tag, System lines are untagged. Tails strip the "HH:mm:ss " timestamp (9 chars).
        Assert.Equal(6, vm.LogEntries.Count);
        string[] tails = [.. vm.LogEntries.Select(l => l[9..])];
        Assert.Equal(["sys 1", "[P1] p1 1", "sys 2", "[P2] p2 1", "[P1] p1 2", "sys 3"], tails);
    }

    // ── #24 — set/attempt progress label (seed vs full) ──

    [Fact]
    public async Task SeedThenFullAttempt_ProgressLabelledPerSetAndStage_SoRewindIsExplained()
    {
        var brute = new RaisingBruteForceService();
        ReconstructorViewModel vm = CreateVm(brute, new InlineUiDispatcher());
        ConfigureRunnablePaths(vm);
        vm.SetImportStateForTest(ImportWith(MakeSet("a", "a.rar"), MakeSet("b", "b.rar")));

        var messages = new ConcurrentQueue<string>();
        void handler(object? _, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(ReconstructorViewModel.ProgressMessage))
            {
                messages.Enqueue(vm.ProgressMessage);
            }
        }
        vm.PropertyChanged += handler;

        int call = 0;
        brute.OnRun = o =>
        {
            int n = Interlocked.Increment(ref call);
            if (n == 1)
            {
                // Set 1 succeeds → its winning combo seeds set 2.
                return WriteBruteSuccess(o, "a.rar");
            }

            if (n == 2)
            {
                // Set 2, seeded attempt: progress races to 90%, then the seed misses.
                brute.RaiseProgress(MakeProgress(90));
                return new BruteForceRunResult(false, null);
            }

            // Set 2, full-matrix attempt: progress restarts at 10% — this is the apparent "rewind".
            brute.RaiseProgress(MakeProgress(10));
            return WriteBruteSuccess(o, "b.rar");
        };

        await vm.ExecuteReconstructionForTestAsync(CancellationToken.None);
        vm.PropertyChanged -= handler;

        string[] captured = [.. messages];
        string seedMessage = Assert.Single(captured, m => m.Contains("90/100", StringComparison.Ordinal));
        string fullMessage = Assert.Single(captured, m => m.Contains("10/100", StringComparison.Ordinal));

        // The high-% seed attempt and the low-% full attempt carry distinct Set X/N · <stage> labels,
        // so the % reset reads as a labelled stage change rather than an unexplained rewind.
        Assert.Contains("Set 2/2", seedMessage, StringComparison.Ordinal);
        Assert.Contains("seed", seedMessage, StringComparison.Ordinal);
        Assert.Contains("Set 2/2", fullMessage, StringComparison.Ordinal);
        Assert.Contains("full", fullMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SingleSet_ProgressLabelledSet1Of1()
    {
        var brute = new RaisingBruteForceService();
        ReconstructorViewModel vm = CreateVm(brute, new InlineUiDispatcher());
        ConfigureRunnablePaths(vm);
        vm.SetImportStateForTest(ImportWith(MakeSet("a", "a.rar")));

        var messages = new ConcurrentQueue<string>();
        void handler(object? _, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(ReconstructorViewModel.ProgressMessage))
            {
                messages.Enqueue(vm.ProgressMessage);
            }
        }
        vm.PropertyChanged += handler;

        brute.OnRun = o =>
        {
            brute.RaiseProgress(MakeProgress(50));
            return WriteBruteSuccess(o, "a.rar");
        };

        await vm.ExecuteReconstructionForTestAsync(CancellationToken.None);
        vm.PropertyChanged -= handler;

        string[] captured = [.. messages];
        Assert.Contains(captured, m => m.Contains("Set 1/1", StringComparison.Ordinal));
    }
}
