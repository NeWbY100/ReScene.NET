namespace ReScene.App.Core.ViewModels.Creation;

/// <summary>
/// Owns a folder scan's generation counter and in-flight cancellation source, so the claim/cancel
/// discipline lives in one place instead of being spread across the view-model's input-path hook,
/// its reset, and the scan's own completion callbacks.
/// </summary>
/// <remarks>
/// <para>
/// Every member is called from UI-thread-invoked code — a property-changed hook, a reset, or the
/// dispatcher callback a completed scan posts. That serialization is what makes the identity check
/// in <see cref="TryComplete"/> race-free: <c>Cancel()</c> and <c>Dispose()</c> on one
/// <see cref="CancellationTokenSource"/> never run concurrently (which would throw), and a live
/// newer source can never be null'd out by an older scan's cleanup.
/// </para>
/// <para>
/// The rules this type exists to keep:
/// a scan's result is applied only when BOTH its generation and its source reference are still
/// current, and a source is disposed exactly once, by whichever of the two paths claims it first.
/// </para>
/// </remarks>
internal sealed class FolderScanSession
{
    private int _generation;
    private CancellationTokenSource? _cts;

    /// <summary>
    /// Invalidates any in-flight scan without starting one. Bumped on EVERY input change — file,
    /// blank, nonexistent or folder — so a stale completion that finished its work before observing
    /// cancellation is still discarded rather than overwriting newer state.
    /// </summary>
    public void BumpGeneration() => _generation++;

    /// <summary>
    /// Cancels and disposes any in-flight scan's source synchronously on the calling thread, and
    /// clears it before returning, so <see cref="TryComplete"/> can never resurrect a reference this
    /// already tore down.
    /// </summary>
    public void CancelInFlight()
    {
        if (_cts is not { } cts)
        {
            return;
        }

        _cts = null;
        try
        {
            cts.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Already disposed by an earlier call — nothing left to do (defensive; the ownership
            // rule above should make this unreachable in practice).
        }
        finally
        {
            cts.Dispose();
        }
    }

    /// <summary>
    /// Starts a scan: bumps the generation and installs a fresh source, returning both plus the
    /// token.
    /// </summary>
    /// <remarks>
    /// The token is captured as a value HERE, while the source is certainly not yet disposed, and
    /// the caller must poll that value rather than reading <c>Cts.Token</c> again later.
    /// <c>CancellationTokenSource.Token</c>'s GETTER throws <see cref="ObjectDisposedException"/>
    /// once the source is disposed, whereas a <see cref="CancellationToken"/> struct obtained
    /// beforehand stays safe to poll afterwards. Re-reading the property lazily inside a background
    /// delegate — evaluated whenever the thread pool actually gets to it — races a later
    /// <see cref="CancelInFlight"/> disposing that exact source first.
    /// </remarks>
    public (int Generation, CancellationTokenSource Cts, CancellationToken Token) Begin()
    {
        var cts = new CancellationTokenSource();
        CancellationToken token = cts.Token;
        _generation++;
        _cts = cts;
        return (_generation, cts, token);
    }

    /// <summary>
    /// Claims a finished scan: returns <see langword="true"/> and disposes its source when the scan
    /// is still current, or <see langword="false"/> when it has been superseded — in which case the
    /// newer input change already cancelled, disposed and cleared that source, and the caller must
    /// hard-bail rather than clean up again.
    /// </summary>
    /// <remarks>
    /// The identity check and the cleanup are deliberately one operation: they must not be
    /// separable, or a caller could test currency and then fail to release the source (or release
    /// one it does not own).
    /// </remarks>
    public bool TryComplete(int generation, CancellationTokenSource cts)
    {
        if (generation != _generation || !ReferenceEquals(_cts, cts))
        {
            return false;
        }

        _cts = null;
        cts.Dispose();
        return true;
    }
}
