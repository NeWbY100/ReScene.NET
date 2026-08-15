using ReScene.App.Core.ViewModels.Creation;

namespace ReScene.App.Core.Tests;

/// <summary>
/// Direct tests for the folder scan's claim/cancel discipline. These are deterministic — no thread
/// pool, no timing — which is the point: the race they describe cannot be reproduced reliably from
/// the view-model level, so the properties that make the discipline correct are pinned here
/// instead.
/// </summary>
public class FolderScanSessionTests
{
    [Fact]
    public void Begin_CapturedTokenStaysPollable_AfterTheSourceIsDisposed()
    {
        // This is WHY Begin captures the token as a value rather than letting callers read
        // Cts.Token later: the property's getter throws once the source is disposed, while a
        // CancellationToken struct obtained beforehand stays safe to poll forever. A background
        // delegate that re-read the property would race a supersede that disposed the source first.
        var session = new FolderScanSession();
        (_, CancellationTokenSource cts, CancellationToken token) = session.Begin();

        session.CancelInFlight(); // cancels AND disposes

        Assert.True(token.IsCancellationRequested);
        Assert.Throws<ObjectDisposedException>(() => cts.Token);
    }

    [Fact]
    public void TryComplete_AfterCancelInFlight_IsFalse_AndDoesNotDisposeTwice()
    {
        // CancelInFlight already claimed and disposed the source. The superseded scan must hard-bail
        // rather than clean up again — a second Dispose would be harmless, but clearing the field
        // again could null out a NEWER source that has since been installed.
        var session = new FolderScanSession();
        (int generation, CancellationTokenSource cts, _) = session.Begin();

        session.CancelInFlight();

        Assert.False(session.TryComplete(generation, cts));
    }

    [Fact]
    public void TryComplete_ForTheCurrentScan_IsTrue_AndDisposesTheSource()
    {
        var session = new FolderScanSession();
        (int generation, CancellationTokenSource cts, _) = session.Begin();

        Assert.True(session.TryComplete(generation, cts));
        Assert.Throws<ObjectDisposedException>(() => cts.Token); // it really was disposed
        Assert.False(session.TryComplete(generation, cts));      // and cannot be claimed twice
    }

    [Fact]
    public void TryComplete_ForASupersededScan_IsFalse_AndLeavesTheNewerSourceAlone()
    {
        // The ownership backstop: an older scan completing late must not disturb the scan that
        // replaced it.
        var session = new FolderScanSession();
        (int oldGeneration, CancellationTokenSource oldCts, _) = session.Begin();
        (int newGeneration, CancellationTokenSource newCts, _) = session.Begin();

        Assert.False(session.TryComplete(oldGeneration, oldCts));
        Assert.True(session.TryComplete(newGeneration, newCts), "the newer scan must still be claimable");
    }

    [Fact]
    public void BumpGeneration_AloneInvalidatesAnInFlightScan()
    {
        // Every input change bumps, so a scan that finished its work before observing cancellation
        // is still discarded rather than overwriting newer state.
        var session = new FolderScanSession();
        (int generation, CancellationTokenSource cts, _) = session.Begin();

        session.BumpGeneration();

        Assert.False(session.TryComplete(generation, cts));
    }

    [Fact]
    public void CancelInFlight_WithNoScan_IsANoOp()
    {
        var session = new FolderScanSession();
        session.CancelInFlight();
        session.CancelInFlight();
    }
}
