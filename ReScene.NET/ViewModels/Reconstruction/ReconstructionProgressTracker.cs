using System.Collections.ObjectModel;
using System.Diagnostics;
using ReScene.Core;
using ReScene.NET.Helpers;

namespace ReScene.NET.ViewModels.Reconstruction;

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
    Func<string, string, string, TVersionRow> createRow,
    Action<TVersionRow, string> setStatus,
    Action<TVersionRow, string> setResult,
    Func<TVersionRow, string> getFullCommandLine,
    Action<LogTarget, string> appendLog)
{
    private readonly ObservableCollection<TVersionRow> _versionEntries = versionEntries;
    private readonly Func<string, string, string, TVersionRow> _createRow = createRow;
    private readonly Action<TVersionRow, string> _setStatus = setStatus;
    private readonly Action<TVersionRow, string> _setResult = setResult;
    private readonly Func<TVersionRow, string> _getFullCommandLine = getFullCommandLine;
    private readonly Action<LogTarget, string> _appendLog = appendLog;

    // Main brute-force run timing.
    private readonly Stopwatch _stopwatch = new();

    // Copy / verify sub-operation timing.
    private readonly Stopwatch _copyStopwatch = new();
    private readonly Stopwatch _verifyStopwatch = new();

    private double _lastSecondsPerOperation; // cached rate from last progress event
    private long _lastOperationRemaining;    // cached remaining count from last progress event
    private long _lastOperationSize;         // cached total count from last progress event

    private string _lastPhaseDescription = "";
    private int _activeVersionIndex = -1;
    private string _activeVersionKey = "";

    public long LastOperationSize => _lastOperationSize;
    public TimeSpan Elapsed => _stopwatch.Elapsed;
    public bool HasActiveVersion => _activeVersionIndex >= 0 && _activeVersionIndex < _versionEntries.Count;

    /// <summary>Resets all per-run state and (re)starts the elapsed stopwatch. Clears the version table.</summary>
    public void StartRun()
    {
        _lastSecondsPerOperation = 0;
        _lastOperationRemaining = 0;
        _stopwatch.Restart();
        _versionEntries.Clear();
        _lastPhaseDescription = "";
        _activeVersionIndex = -1;
        _activeVersionKey = "";
    }

    /// <summary>Stops the elapsed stopwatch (run finished/cancelled/errored).</summary>
    public void StopRun()
    {
        _stopwatch.Stop();
    }

    /// <summary>Clears all bookkeeping (used by Reset before a fresh run is configured).</summary>
    public void Clear()
    {
        _stopwatch.Reset();
        _copyStopwatch.Reset();
        _verifyStopwatch.Reset();
        _lastSecondsPerOperation = 0;
        _lastOperationRemaining = 0;
        _lastOperationSize = 0;
        _lastPhaseDescription = "";
        _activeVersionIndex = -1;
        _activeVersionKey = "";
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

    /// <summary>Marks the active version row complete with the given match/no-match result.</summary>
    public void CompleteActiveVersion(string result)
    {
        if (HasActiveVersion)
        {
            _setStatus(_versionEntries[_activeVersionIndex], "Complete");
            _setResult(_versionEntries[_activeVersionIndex], result);
        }
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
            _lastSecondsPerOperation = e.TimeElapsed.TotalSeconds / e.OperationProgressed;
            _lastOperationRemaining = e.OperationRemaining;
            update = update with
            {
                HasTiming = true,
                RemainingText = ReconstructorFormatting.FormatTimeSpan(e.TimeRemaining),
                SpeedText = $"{e.OperationSpeed:N0} tests/s",
                EtaText = e.EstimatedFinishDateTime.ToString("HH:mm:ss"),
            };
        }

        // Version list tracking
        string phaseDesc = e.PhaseDescription ?? "";
        if (phaseDesc != _lastPhaseDescription)
        {
            _versionEntries.Clear();
            _activeVersionIndex = -1;
            _activeVersionKey = "";
            _lastPhaseDescription = phaseDesc;
        }

        string key = string.Concat(e.RARVersionDirectoryPath, "|", e.RARCommandLineArguments);
        if (key != _activeVersionKey)
        {
            if (_activeVersionIndex >= 0 && _activeVersionIndex < _versionEntries.Count)
            {
                _setStatus(_versionEntries[_activeVersionIndex], "Complete");
                _setResult(_versionEntries[_activeVersionIndex], "No Match");
            }

            TVersionRow entry = _createRow(versionLabel, e.RARCommandLineArguments, e.RARVersionDirectoryPath);
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
    /// Recomputes elapsed/remaining/ETA between progress events (driven by the 1-second timer),
    /// extrapolating from the last cached rate.
    /// </summary>
    public ElapsedTick Tick()
    {
        var tick = new ElapsedTick { ElapsedText = ReconstructorFormatting.FormatTimeSpan(_stopwatch.Elapsed) };

        if (_lastSecondsPerOperation > 0 && _lastOperationRemaining > 0)
        {
            var remaining = TimeSpan.FromSeconds(_lastSecondsPerOperation * _lastOperationRemaining);
            tick = tick with
            {
                HasTiming = true,
                RemainingText = ReconstructorFormatting.FormatTimeSpan(remaining),
                EtaText = DateTime.Now.Add(remaining).ToString("HH:mm:ss"),
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

/// <summary>Display text computed from a brute-force progress event.</summary>
internal sealed record BruteForceProgressUpdate
{
    public double ProgressPercent { get; init; }
    public string PhaseDescription { get; init; } = string.Empty;
    public string ProgressMessage { get; init; } = string.Empty;
    public string TestCountText { get; init; } = string.Empty;
    public string ProgressPercentText { get; init; } = string.Empty;
    public string CurrentDetailText { get; init; } = string.Empty;
    public string ElapsedText { get; init; } = string.Empty;

    /// <summary>True when timing fields below were computed (operation has progressed).</summary>
    public bool HasTiming { get; init; }
    public string RemainingText { get; init; } = string.Empty;
    public string SpeedText { get; init; } = string.Empty;
    public string EtaText { get; init; } = string.Empty;
}

/// <summary>Elapsed/remaining text extrapolated by the per-second timer tick.</summary>
internal sealed record ElapsedTick
{
    public string ElapsedText { get; init; } = string.Empty;
    public bool HasTiming { get; init; }
    public string RemainingText { get; init; } = string.Empty;
    public string EtaText { get; init; } = string.Empty;
}

/// <summary>Display text computed from a file-copy progress event.</summary>
internal sealed record CopyProgressUpdate
{
    public string HeadingText { get; init; } = string.Empty;
    public string SourceText { get; init; } = string.Empty;
    public string DestText { get; init; } = string.Empty;
    public double ProgressPercent { get; init; }
    public string ProgressPercentText { get; init; } = string.Empty;
    public string CurrentFileText { get; init; } = string.Empty;
    public string RemainingText { get; init; } = string.Empty;
    public string ElapsedText { get; init; } = string.Empty;

    public bool HasSpeed { get; init; }
    public string SpeedText { get; init; } = string.Empty;
    public bool HasEta { get; init; }
    public string TimeRemainingText { get; init; } = string.Empty;
    public string EtaText { get; init; } = string.Empty;

    public bool IsComplete { get; init; }
}

/// <summary>Display text computed from a CRC-validation progress event.</summary>
internal sealed record VerifyProgressUpdate
{
    public string HeadingText { get; init; } = string.Empty;
    public double ProgressPercent { get; init; }
    public string ProgressPercentText { get; init; } = string.Empty;
    public string CurrentFileText { get; init; } = string.Empty;
    public string RemainingText { get; init; } = string.Empty;
    public string ElapsedText { get; init; } = string.Empty;

    public bool HasSpeed { get; init; }
    public string SpeedText { get; init; } = string.Empty;
    public bool HasEta { get; init; }
    public string TimeRemainingText { get; init; } = string.Empty;
    public string EtaText { get; init; } = string.Empty;

    public bool IsComplete { get; init; }
}
