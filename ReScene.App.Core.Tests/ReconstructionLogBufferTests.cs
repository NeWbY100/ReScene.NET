using System.Collections.ObjectModel;
using System.Collections.Specialized;
using ReScene.App.Core.Services;
using ReScene.App.Core.ViewModels.Reconstruction;
using ReScene.Core;

namespace ReScene.App.Core.Tests;

/// <summary>
/// Pins the two orderings inside <see cref="ReconstructionLogBuffer"/> that nothing else caught.
/// </summary>
/// <remarks>
/// Probing the extraction by mutating each of the buffer's three documented orderings found only one
/// of them guarded: stamping the timestamp at drain instead of at enqueue fails
/// <c>BatchedFlush_PreservesChronologicalOrder_AndTagsPhaseLines</c>. Reordering
/// <c>BeginNewGeneration</c>'s clear and increment, and moving the flush-flag release to after the
/// drain, both left all 780 tests passing.
/// <para>
/// Both surviving orderings matter only for a line enqueued DURING the operation, so both tests reach
/// that window the same way: <see cref="ObservableCollection{T}"/> raises
/// <see cref="INotifyCollectionChanged.CollectionChanged"/> SYNCHRONOUSLY, so a handler on the bound
/// log collection runs re-entrantly in the middle of the very operation under test. No threads, no
/// timing.
/// </para>
/// </remarks>
public class ReconstructionLogBufferTests
{
    /// <summary>Defers posted work so a flush can be run deliberately, and counts the dispatches.</summary>
    private sealed class DeferringUiDispatcher : IUiDispatcher
    {
        private readonly Queue<Action> _deferred = new();

        public int PostCount { get; private set; }

        public void Invoke(Action action) => action();

        public void Post(Action action)
        {
            PostCount++;
            _deferred.Enqueue(action);
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

    [Fact]
    public void BeginNewGeneration_ClearsBeforeIncrementing_SoAnAppendDuringTheTransitionIsDropped()
    {
        // BeginNewGeneration clears the visible log, THEN increments the generation. A line enqueued
        // in between therefore carries the OLD generation and is dropped by the next drain.
        //
        // Increment first and that line carries the NEW generation instead, so it survives the drain
        // and survives into the next run's log. That is the whole point of the generation token, and
        // nothing tested it. (The fixture pins the transition itself, not a full previous run.)
        const string MidClear = "enqueued while the log was being cleared";
        var dispatcher = new DeferringUiDispatcher();
        ObservableCollection<string> log = [];
        var buffer = new ReconstructionLogBuffer(dispatcher, log);

        bool reentered = false;
        log.CollectionChanged += (_, e) =>
        {
            // Clear() raises Reset synchronously, landing us between the clear and the increment.
            if (e.Action == NotifyCollectionChangedAction.Reset && !reentered)
            {
                reentered = true;
                buffer.Append(LogTarget.System, MidClear);
            }
        };

        buffer.BeginNewGeneration();

        // Without this the test would pass vacuously if Clear ever stopped raising Reset.
        Assert.True(reentered, "the re-entrant append never ran, so this test proves nothing");

        buffer.Drain();

        Assert.DoesNotContain(log, l => l.Contains(MidClear, StringComparison.Ordinal));
    }

    [Fact]
    public void Flush_ReleasesTheScheduleFlagBeforeDraining_SoAReentrantAppendSchedulesAnotherFlush()
    {
        // The flush flag coalesces a burst of appends into one dispatch. Flush releases it BEFORE
        // draining, so a line enqueued during the drain sees a free flag and schedules the next
        // dispatch for itself.
        //
        // Release it after the drain and that line finds the flag still raised and schedules nothing.
        //
        // Note what this does NOT prove: with the flag released late, the same TryDequeue loop still
        // consumes the re-entrant line, so it lands in the log either way. Only the DISPATCH COUNT
        // separates the two. The Assert.Contains below is a control, not the proof.
        const string First = "first line";
        const string Second = "enqueued while the queue was being drained";
        var dispatcher = new DeferringUiDispatcher();
        ObservableCollection<string> log = [];
        var buffer = new ReconstructionLogBuffer(dispatcher, log);

        bool reentered = false;
        int postCountRightAfterTheReentrantAppend = -1;
        log.CollectionChanged += (_, e) =>
        {
            // The drain Adds the first line, synchronously, from inside the flush.
            if (e.Action == NotifyCollectionChangedAction.Add && !reentered)
            {
                reentered = true;
                buffer.Append(LogTarget.System, Second);

                // Sampled HERE, not at the end: this ties the extra dispatch causally to the
                // re-entrant append rather than to whatever the run happened to total.
                postCountRightAfterTheReentrantAppend = dispatcher.PostCount;
            }
        };

        buffer.Append(LogTarget.System, First);
        Assert.Equal(1, dispatcher.PostCount);   // the burst's single scheduled flush

        dispatcher.Pump();

        Assert.True(reentered, "the re-entrant append never ran, so this test proves nothing");
        Assert.True(postCountRightAfterTheReentrantAppend >= 2,
            $"the re-entrant append scheduled no flush of its own (PostCount was {postCountRightAfterTheReentrantAppend})");
        Assert.Contains(log, l => l.Contains(Second, StringComparison.Ordinal));   // control
    }
}
