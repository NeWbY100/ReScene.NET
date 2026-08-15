using System.Collections.ObjectModel;
using System.Diagnostics;
using ReScene.App.Core.Helpers;
using ReScene.Core;

namespace ReScene.App.Core.ViewModels.Reconstruction;

/// <summary>
/// Owns the per-run progress bookkeeping for the RAR Reconstructor: elapsed/remaining timing,
/// the brute-force version table, and the copy/verify sub-operation timing. It mutates the
/// view-model-owned <see cref="ObservableCollection{T}"/> of version rows and returns computed
/// display text via result records; the view-model assigns those to its bound properties and owns
/// all UI-thread marshalling. The tracker holds no WPF binding concerns of its own.
/// </summary>
/// <typeparam name="TVersionRow">
/// The view-model's bound version-row type. The tracker mutates its status/result/etc. through the
/// supplied accessors so the concrete (bound) type stays on the view-model.
/// </typeparam>
internal sealed class ReconstructionProgressTracker<TVersionRow>(
    ObservableCollection<TVersionRow> versionEntries,
    Func<string, string, string, string, string, string, string, TVersionRow> createRow,
    Action<TVersionRow, string> setStatus,
    Action<TVersionRow, string> setResult,
    Action<TVersionRow, string> setSetText,
    Func<TVersionRow, string> getFullCommandLine,
    Action<LogTarget, string> appendLog,
    TimeProvider? timeProvider = null)
{
    private readonly ObservableCollection<TVersionRow> _versionEntries = versionEntries;
    // (label, displayArguments, versionDirectory, inputDirectory, outputFilePath, executedArguments,
    // inputFileArguments) → bound row. The middle three are the invocation's working dir, output
    // archive, and ACTUAL argument string (engine-added switches included) — empty for Phase-1 rows —
    // carried so the row's copied command line can be the full runnable invocation while the grid/log
    // keep the display form. inputFileArguments is the explicit SRR-ordered input file list, when the
    // run used one, rendered the same way as executedArguments; empty means rar's own input mask.
    private readonly Func<string, string, string, string, string, string, string, TVersionRow> _createRow = createRow;
    private readonly Action<TVersionRow, string> _setStatus = setStatus;
    private readonly Action<TVersionRow, string> _setResult = setResult;
    private readonly Action<TVersionRow, string> _setSetText = setSetText;
    private readonly Func<TVersionRow, string> _getFullCommandLine = getFullCommandLine;
    private readonly Action<LogTarget, string> _appendLog = appendLog;
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    // Main brute-force run timing.
    private readonly Stopwatch _stopwatch = new();

    // Copy / verify sub-operation timing.
    private readonly Stopwatch _copyStopwatch = new();
    private readonly Stopwatch _verifyStopwatch = new();

    // Fixed target completion instant cached from the last progress event; Tick() counts down
    // against this (via _timeProvider) rather than recomputing a flat estimate every second.
    private DateTimeOffset? _cachedCompletionInstant;
    private long _lastOperationSize;         // cached total count from last progress event

    private string _lastPhaseDescription = "";
    private int _activeVersionIndex = -1;
    private string _activeVersionKey = "";
    private string _activeSetLabel = "";

    // Row index where the active set's own rows begin (snapshotted in SetActiveSet) and whether a
    // SetActiveSet call is still "pending" — i.e. happened since the last progress event finished.
    // Together these let a phase change tell an intra-set transition (e.g. Phase 1 -> Phase 2 of the
    // SAME set) apart from a cross-set boundary: see ApplyProgress.
    private int _activeSetStartIndex;
    private bool _setBoundaryPending;

    public long LastOperationSize => _lastOperationSize;
    public TimeSpan Elapsed => _stopwatch.Elapsed;
    public bool HasActiveVersion => _activeVersionIndex >= 0 && _activeVersionIndex < _versionEntries.Count;

    /// <summary>Resets all per-run state and (re)starts the elapsed stopwatch. Clears the version table.</summary>
    public void StartRun()
    {
        _cachedCompletionInstant = null;
        _stopwatch.Restart();
        _versionEntries.Clear();
        _lastPhaseDescription = "";
        _activeVersionIndex = -1;
        _activeVersionKey = "";
        _activeSetLabel = "";
        _activeSetStartIndex = 0;
        _setBoundaryPending = false;
    }

    /// <summary>Stops the elapsed stopwatch (run finished/cancelled/errored).</summary>
    public void StopRun() => _stopwatch.Stop();

    /// <summary>
    /// Sets the archive-set label that will be stamped onto new version rows. The view-model calls
    /// this before each set's <c>RunAsync</c>; an empty label is used for single-set releases.
    /// Also snapshots where this set's own rows will begin and flags the upcoming progress event as
    /// a potential set boundary (see <see cref="ApplyProgress"/>).
    /// </summary>
    public void SetActiveSet(string label)
    {
        _activeSetLabel = label;
        _activeSetStartIndex = _versionEntries.Count;
        _setBoundaryPending = true;
    }

    /// <summary>Clears all bookkeeping (used by Reset before a fresh run is configured).</summary>
    public void Clear()
    {
        _stopwatch.Reset();
        _copyStopwatch.Reset();
        _verifyStopwatch.Reset();
        _cachedCompletionInstant = null;
        _lastOperationSize = 0;
        _lastPhaseDescription = "";
        _activeVersionIndex = -1;
        _activeVersionKey = "";
        _activeSetLabel = "";
        _activeSetStartIndex = 0;
        _setBoundaryPending = false;
        _versionEntries.Clear();
    }

    /// <summary>Sets the active version row's status, if there is one.</summary>
    public void SetActiveVersionStatus(string status)
    {
        if (HasActiveVersion)
        {
            _setStatus(_versionEntries[_activeVersionIndex], status);
        }
    }

    /// <summary>
    /// Marks the active version row complete with the given match/no-match result and releases the
    /// active-row pointer. The view-model calls this once per archive set, right when that set's own
    /// outcome is known (#23) — releasing the pointer here means the next set's first progress event
    /// (a "key changed" transition in <see cref="ApplyProgress"/>) never re-labels this now-finalized
    /// row, even though its key no longer matches.
    /// </summary>
    public void CompleteActiveVersion(string result)
    {
        if (HasActiveVersion)
        {
            _setStatus(_versionEntries[_activeVersionIndex], "Complete");
            _setResult(_versionEntries[_activeVersionIndex], result);
        }

        _activeVersionIndex = -1;
        _activeVersionKey = "";
    }

    /// <summary>
    /// Marks the active row "Error" with the given result and releases the active-row pointer — used
    /// when the engine could not run RAR for that combination (e.g. the console binary is not
    /// executable). Releasing the pointer means the next combination's progress event never
    /// re-finalizes this row as a clean "No Match" (see <see cref="FinalizeActiveRowAsNoMatch"/>).
    /// </summary>
    private void ErrorActiveVersion(string result)
    {
        if (HasActiveVersion)
        {
            _setStatus(_versionEntries[_activeVersionIndex], "Error");
            _setResult(_versionEntries[_activeVersionIndex], result);
        }

        _activeVersionIndex = -1;
        _activeVersionKey = "";
    }

    /// <summary>
    /// Applies a brute-force progress event: updates timing/version bookkeeping (mutating the
    /// version collection) and returns the display text the view-model assigns to its bound props.
    /// </summary>
    public BruteForceProgressUpdate ApplyProgress(BruteForceProgressEventArgs e)
    {
        string version = Path.GetFileName(e.RARVersionDirectoryPath);

        _lastOperationSize = e.OperationSize;
        string versionLabel = ReconstructorFormatting.FormatVersionLabel(version);

        var update = new BruteForceProgressUpdate
        {
            ProgressPercent = e.Progress,
            PhaseDescription = e.PhaseDescription,
            ProgressMessage = $"{e.PhaseDescription} | {version} | {e.RARCommandLineArguments} | {e.OperationProgressed}/{e.OperationSize}",
            TestCountText = $"Test {e.OperationProgressed:N0} of {e.OperationSize:N0}",
            ProgressPercentText = $"{e.Progress:F1}%",
            CurrentDetailText = $"{versionLabel}  —  {e.RARCommandLineArguments}",
            ElapsedText = ReconstructorFormatting.FormatTimeSpan(_stopwatch.Elapsed),
        };

        if (e.OperationProgressed > 0)
        {
            _cachedCompletionInstant = _timeProvider.GetLocalNow() + e.TimeRemaining;
            update = update with
            {
                HasTiming = true,
                RemainingText = ReconstructorFormatting.FormatTimeSpan(e.TimeRemaining),
                SpeedText = $"{e.OperationSpeed:N0} tests/s",
                EtaText = e.EstimatedFinishDateTime.ToString("HH:mm:ss"),
            };
        }

        // The engine could not run RAR for the active combination (most often the console binary is
        // not executable). Mark its row Error and release the pointer here — the timing/ETA above still
        // advance (the combination counted), but we skip the normal row-creation/finalize logic so the
        // NEXT combination's event does not re-finalize this row as a clean "No Match".
        if (e.CombinationFailed)
        {
            // "Run failed" (not "Could not run RAR"): honest for both a launch failure and the rarer
            // mid-run exception the generic catch also covers, and it fits the narrow Result column —
            // the specific reason (e.g. "Permission denied") is in the Phase 2 log.
            ErrorActiveVersion("Run failed");
            _setBoundaryPending = false;
            return update;
        }

        // Version list tracking. A phase change resets which row is "active". WITHIN a single
        // archive set (e.g. CommentPhaseBruteForcer's "Phase 1: Comment Block Filtering" giving way
        // to Manager's "Phase 2: Full RAR Creation") this must clear that set's own intermediate
        // rows, restoring the old clean-table-per-phase behavior. ACROSS a set boundary, though, the
        // prior set's already-finalized row must survive (#23) — wiping it the moment the next
        // set's first phase text differs from the previous set's last phase would lose it.
        // _setBoundaryPending (raised by SetActiveSet, consumed below regardless of outcome) tells
        // the two cases apart: it's still true only for the very first progress event of a set.
        string phaseDesc = e.PhaseDescription ?? "";
        if (phaseDesc != _lastPhaseDescription)
        {
            if (_setBoundaryPending)
            {
                // Cross-set boundary: preserve every row added so far, including prior sets'.
                FinalizeActiveRowAsNoMatch();
            }
            else
            {
                // Intra-set phase change: drop only this set's own rows (added since SetActiveSet).
                while (_versionEntries.Count > _activeSetStartIndex)
                {
                    _versionEntries.RemoveAt(_versionEntries.Count - 1);
                }
            }

            _activeVersionIndex = -1;
            _activeVersionKey = "";
            _lastPhaseDescription = phaseDesc;
        }

        // Consumed after this event regardless of whether a phase change was seen above: a
        // SetActiveSet call only marks the NEXT event as a possible set boundary, not every event
        // until the next one that happens to differ in phase text (see field comment).
        _setBoundaryPending = false;

        string key = string.Concat(e.RARVersionDirectoryPath, "|", e.RARCommandLineArguments);
        if (key != _activeVersionKey)
        {
            FinalizeActiveRowAsNoMatch();

            TVersionRow entry = _createRow(versionLabel, e.RARCommandLineArguments, e.RARVersionDirectoryPath, e.InputDirectoryPath, e.OutputFilePath, e.ExecutedArguments, e.InputFileArguments);
            _setSetText(entry, _activeSetLabel);
            _versionEntries.Add(entry);
            _activeVersionIndex = _versionEntries.Count - 1;
            _activeVersionKey = key;

            // Surface the exact invocation in the details log as well.
            LogTarget logTarget = phaseDesc.StartsWith("Phase 1", StringComparison.OrdinalIgnoreCase)
                ? LogTarget.Phase1
                : LogTarget.Phase2;
            _appendLog(logTarget, $"Testing {versionLabel}: {_getFullCommandLine(entry)}");
        }

        return update;
    }

    /// <summary>
    /// Marks the currently active row (if any) "Complete"/"No Match" — used when the engine has
    /// moved on from it without a match, whether because a new version/args combo began testing or
    /// because the phase changed. Shared by both transitions in <see cref="ApplyProgress"/>.
    /// </summary>
    private void FinalizeActiveRowAsNoMatch()
    {
        if (_activeVersionIndex >= 0 && _activeVersionIndex < _versionEntries.Count)
        {
            _setStatus(_versionEntries[_activeVersionIndex], "Complete");
            _setResult(_versionEntries[_activeVersionIndex], "No Match");
        }
    }

    /// <summary>
    /// Recomputes elapsed/remaining/ETA between progress events (driven by the 1-second timer).
    /// Remaining counts down against the fixed completion instant cached at the last progress event
    /// (#25) — <c>cached instant − now</c> — rather than re-deriving a flat estimate from the last
    /// cached rate every tick, which never decayed between events.
    /// </summary>
    public ElapsedTick Tick()
    {
        var tick = new ElapsedTick { ElapsedText = ReconstructorFormatting.FormatTimeSpan(_stopwatch.Elapsed) };

        if (_cachedCompletionInstant is { } completion)
        {
            TimeSpan remaining = completion - _timeProvider.GetLocalNow();
            if (remaining < TimeSpan.Zero)
            {
                remaining = TimeSpan.Zero;
            }

            tick = tick with
            {
                HasTiming = true,
                RemainingText = ReconstructorFormatting.FormatTimeSpan(remaining),
                EtaText = completion.ToString("HH:mm:ss"),
            };
        }

        return tick;
    }

    /// <summary>Final elapsed text after the run stops.</summary>
    public string FinalElapsedText() => ReconstructorFormatting.FormatTimeSpan(_stopwatch.Elapsed);

    // ── File copy sub-operation ──

    public void StartCopy() => _copyStopwatch.Restart();
    public void StopCopy() => _copyStopwatch.Stop();

    public CopyProgressUpdate ApplyCopyProgress(FileCopyProgressEventArgs e)
    {
        double percent = e.TotalBytes > 0 ? (double)e.BytesCopied / e.TotalBytes * 100.0 : 0;
        int remaining = e.TotalFiles - e.FilesCopied;
        long remainingBytes = e.TotalBytes - e.BytesCopied;

        var update = new CopyProgressUpdate
        {
            HeadingText = $"Copying {e.TotalFiles} items ({FormatUtilities.FormatSize(e.TotalBytes)})",
            SourceText = e.SourceDirectory,
            DestText = e.DestinationDirectory,
            ProgressPercent = percent,
            ProgressPercentText = $"{percent:F0}%",
            CurrentFileText = e.FileName,
            RemainingText = $"Items remaining: {remaining} ({FormatUtilities.FormatSize(remainingBytes)})",
        };

        TimeSpan elapsed = _copyStopwatch.Elapsed;
        update = update with { ElapsedText = ReconstructorFormatting.FormatTimeSpan(elapsed) };

        if (e.BytesCopied > 0 && elapsed.TotalSeconds >= 0.5)
        {
            double bytesPerSec = e.BytesCopied / elapsed.TotalSeconds;
            update = update with { HasSpeed = true, SpeedText = ReconstructorFormatting.FormatSpeed(bytesPerSec) };
            if (bytesPerSec > 0 && remainingBytes > 0)
            {
                var timeRemaining = TimeSpan.FromSeconds(remainingBytes / bytesPerSec);
                update = update with
                {
                    HasEta = true,
                    TimeRemainingText = ReconstructorFormatting.FormatTimeSpan(timeRemaining),
                    EtaText = DateTime.Now.Add(timeRemaining).ToString("HH:mm:ss"),
                };
            }
        }

        update = update with { IsComplete = e.FilesCopied >= e.TotalFiles };
        if (update.IsComplete)
        {
            _copyStopwatch.Stop();
        }

        return update;
    }

    // ── CRC validation sub-operation ──

    public void StartVerify() => _verifyStopwatch.Restart();
    public void StopVerify() => _verifyStopwatch.Stop();

    public VerifyProgressUpdate ApplyVerifyProgress(CRCValidationProgressEventArgs e)
    {
        double percent = e.TotalBytes > 0 ? (double)e.BytesVerified / e.TotalBytes * 100.0 : 0;
        int remaining = e.TotalFiles - e.FilesVerified;
        long remainingBytes = e.TotalBytes - e.BytesVerified;

        var update = new VerifyProgressUpdate
        {
            HeadingText = $"Verifying {e.TotalFiles} items ({FormatUtilities.FormatSize(e.TotalBytes)})",
            ProgressPercent = percent,
            ProgressPercentText = $"{percent:F0}%",
            CurrentFileText = e.FileName,
            RemainingText = $"Items remaining: {remaining} ({FormatUtilities.FormatSize(remainingBytes)})",
        };

        TimeSpan elapsed = _verifyStopwatch.Elapsed;
        update = update with { ElapsedText = ReconstructorFormatting.FormatTimeSpan(elapsed) };

        if (e.BytesVerified > 0 && elapsed.TotalSeconds >= 0.5)
        {
            double bytesPerSec = e.BytesVerified / elapsed.TotalSeconds;
            update = update with { HasSpeed = true, SpeedText = ReconstructorFormatting.FormatSpeed(bytesPerSec) };
            if (bytesPerSec > 0 && remainingBytes > 0)
            {
                var timeRemaining = TimeSpan.FromSeconds(remainingBytes / bytesPerSec);
                update = update with
                {
                    HasEta = true,
                    TimeRemainingText = ReconstructorFormatting.FormatTimeSpan(timeRemaining),
                    EtaText = DateTime.Now.Add(timeRemaining).ToString("HH:mm:ss"),
                };
            }
        }

        update = update with { IsComplete = e.FilesVerified >= e.TotalFiles };
        if (update.IsComplete)
        {
            _verifyStopwatch.Stop();
        }

        return update;
    }
}
