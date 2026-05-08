using ReScene.Hex;

namespace ReScene.NET.Services;

/// <summary>
/// Outcome of a byte-level diff between two hex data slices.
/// </summary>
/// <param name="Left">
/// Coalesced ranges of bytes on the left side that differ from the right side
/// (or that have no counterpart because the left slice is longer).
/// </param>
/// <param name="Right">
/// Coalesced ranges of bytes on the right side that differ from the left side
/// (or that have no counterpart because the right slice is longer).
/// </param>
public sealed record HexDiffResult(
    IReadOnlyList<HexMatchRange> Left,
    IReadOnlyList<HexMatchRange> Right);

/// <summary>
/// Periodic update emitted while a diff computation is running.
/// </summary>
/// <param name="Percent">
/// Approximate completion percentage (0 to 100).
/// </param>
/// <param name="Left">
/// Snapshot of left-side diff ranges produced so far.
/// </param>
/// <param name="Right">
/// Snapshot of right-side diff ranges produced so far.
/// </param>
public sealed record HexDiffProgress(
    double Percent,
    IReadOnlyList<HexMatchRange> Left,
    IReadOnlyList<HexMatchRange> Right);

/// <summary>
/// Computes byte-level differences between two slices of hex data, position-aligned.
/// </summary>
public interface IHexDiffComputer
{
    /// <summary>
    /// Compares two byte ranges position-aligned and produces coalesced diff ranges
    /// for each side. Bytes past the shorter slice's length on the longer side are
    /// emitted as a trailing diff range. Computation runs on a background task and
    /// can be cancelled at chunk boundaries.
    /// </summary>
    /// <param name="leftSource">
    /// Data source backing the left slice.
    /// </param>
    /// <param name="leftOffset">
    /// Absolute offset within <paramref name="leftSource"/> where the slice starts.
    /// Emitted ranges use this as their coordinate base.
    /// </param>
    /// <param name="leftLength">
    /// Length of the left slice in bytes.
    /// </param>
    /// <param name="rightSource">
    /// Data source backing the right slice.
    /// </param>
    /// <param name="rightOffset">
    /// Absolute offset within <paramref name="rightSource"/> where the slice starts.
    /// </param>
    /// <param name="rightLength">
    /// Length of the right slice in bytes.
    /// </param>
    /// <param name="progress">
    /// Optional progress reporter; debounced at roughly 10 updates per second.
    /// </param>
    /// <param name="ct">
    /// Cancellation token; honored at chunk boundaries.
    /// </param>
    public Task<HexDiffResult> ComputeAsync(
        IHexDataSource leftSource, long leftOffset, long leftLength,
        IHexDataSource rightSource, long rightOffset, long rightLength,
        IProgress<HexDiffProgress>? progress,
        CancellationToken ct);
}
