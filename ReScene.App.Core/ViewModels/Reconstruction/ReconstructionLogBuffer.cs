using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using ReScene.App.Core.Services;
using ReScene.Core;

namespace ReScene.App.Core.ViewModels.Reconstruction;

/// <summary>
/// The reconstructor's generation-safe batched log (#20). Lines are enqueued (thread-safe) and
/// applied to the bound log collection in batches on the UI thread. An atomic flush flag coalesces
/// many enqueues into at most one pending UI dispatch, and a run-generation token stamped on each
/// line lets a stale flush from a prior run discard its batch rather than repopulate a log the next
/// run already cleared.
/// </summary>
/// <remarks>
/// Three orderings are load-bearing, and each has a named failure:
/// <list type="bullet">
/// <item><see cref="BeginNewGeneration"/> clears, then increments, then resets the flag — in that
/// order. Increment before the clear and a line enqueued in between survives into the next run's
/// log.</item>
/// <item><see cref="Flush"/> releases the flag BEFORE draining. Release it after and an append made
/// once the drain has already seen the queue empty finds the flag still raised, so it schedules no
/// dispatch of its own and waits for an unrelated later append to carry it.</item>
/// <item><see cref="Append"/> stamps the timestamp at ENQUEUE, not at drain. Stamp it at drain and
/// every line in a batch gets the flush's time instead of its own.</item>
/// </list>
/// <para>
/// <see cref="Drain"/> is also called SYNCHRONOUSLY from the run's finally as the final drain — it is
/// not only a dispatcher callback.
/// </para>
/// <para>
/// <paramref name="logEntries"/> is held by reference: it is the view-model's own bound collection.
/// </para>
/// </remarks>
internal sealed class ReconstructionLogBuffer(IUiDispatcher uiDispatcher, ObservableCollection<string> logEntries)
{
    private readonly ConcurrentQueue<PendingLogLine> _queue = new();

    // Accessed only through Interlocked/Volatile helpers (not declared volatile, which would conflict
    // with passing it by ref and emit CS0420) — those calls carry the needed memory semantics.
    private int _generation;
    private int _flushScheduled;

    /// <summary>Enqueues a log line, stamped with the current time and run generation.</summary>
    public void Append(LogTarget target, string message)
    {
        string tag = target switch
        {
            LogTarget.Phase1 => "[P1] ",
            LogTarget.Phase2 => "[P2] ",
            _ => string.Empty,
        };
        string line = $"{DateTime.Now:HH:mm:ss} {tag}{message}";
        _queue.Enqueue(new PendingLogLine(line, Volatile.Read(ref _generation)));
        ScheduleFlush();
    }

    /// <summary>
    /// Schedules exactly one UI-thread flush per pending batch: the atomic flag flips 0→1 only for the
    /// first enqueue after a drain, so a burst of log events collapses into a single dispatch (#20).
    /// </summary>
    private void ScheduleFlush()
    {
        if (Interlocked.Exchange(ref _flushScheduled, 1) == 0)
        {
            uiDispatcher.Post(Flush);
        }
    }

    /// <summary>
    /// Runs on the UI thread. Releases the flush flag first (so lines enqueued during the drain
    /// schedule the next flush), then applies the queued batch.
    /// </summary>
    private void Flush()
    {
        Interlocked.Exchange(ref _flushScheduled, 0);
        Drain();
    }

    /// <summary>
    /// Drains the queue onto the bound log collection, dropping any line whose generation is not the
    /// current one — so a stale flush queued by a prior run cannot repopulate a log the next run has
    /// already cleared (#20). Also called synchronously from the run's finally as the final drain.
    /// </summary>
    public void Drain()
    {
        int generation = Volatile.Read(ref _generation);
        while (_queue.TryDequeue(out PendingLogLine entry))
        {
            if (entry.Generation != generation)
            {
                continue;
            }

            logEntries.Add(entry.Line);
        }
    }

    /// <summary>
    /// Clears the visible log and starts a new log generation for a run. Bumping the generation makes
    /// any lines still queued from a prior run drop on their (stale) flush, and resetting the flush flag
    /// ensures this run's first line schedules a fresh dispatch (#20).
    /// </summary>
    public void BeginNewGeneration()
    {
        logEntries.Clear();
        Interlocked.Increment(ref _generation);
        Interlocked.Exchange(ref _flushScheduled, 0);
    }

    /// <summary>One queued log line, tagged with the run generation it belongs to (#20).</summary>
    private readonly record struct PendingLogLine(string Line, int Generation);
}
