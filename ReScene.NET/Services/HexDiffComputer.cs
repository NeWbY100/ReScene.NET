using ReScene.Hex;

namespace ReScene.NET.Services;

/// <summary>
/// Position-aligned chunked byte-diff implementation. See <see cref="IHexDiffComputer"/>.
/// </summary>
public class HexDiffComputer : IHexDiffComputer
{
    private const int ChunkSize = 64 * 1024;
    private const int CoalesceGap = 4;
    private const int MaxRangeLength = int.MaxValue;
    private static readonly TimeSpan _progressInterval = TimeSpan.FromMilliseconds(100);

    public Task<HexDiffResult> ComputeAsync(
        IHexDataSource leftSource, long leftOffset, long leftLength,
        IHexDataSource rightSource, long rightOffset, long rightLength,
        IProgress<HexDiffProgress>? progress,
        CancellationToken ct)
    {
        return Task.Run(() => ComputeCore(
            leftSource, leftOffset, leftLength,
            rightSource, rightOffset, rightLength,
            progress, ct), ct);
    }

    private static HexDiffResult ComputeCore(
        IHexDataSource leftSource, long leftOffset, long leftLength,
        IHexDataSource rightSource, long rightOffset, long rightLength,
        IProgress<HexDiffProgress>? progress,
        CancellationToken ct)
    {
        var leftRanges = new List<HexMatchRange>();
        var rightRanges = new List<HexMatchRange>();

        long commonLen = Math.Min(leftLength, rightLength);
        long pos = 0;
        long openStart = -1;
        long openEndExclusive = -1;
        int matchSinceOpen = 0;

        byte[] leftBuf = new byte[ChunkSize];
        byte[] rightBuf = new byte[ChunkSize];

        DateTime lastReport = DateTime.UtcNow;

        while (pos < commonLen)
        {
            ct.ThrowIfCancellationRequested();

            int chunk = (int)Math.Min(ChunkSize, commonLen - pos);
            int lr = leftSource.Read(leftOffset + pos, leftBuf, 0, chunk);
            int rr = rightSource.Read(rightOffset + pos, rightBuf, 0, chunk);
            int n = Math.Min(lr, rr);
            if (n <= 0)
            {
                break;
            }

            for (int i = 0; i < n; i++)
            {
                long abs = pos + i;
                if (leftBuf[i] != rightBuf[i])
                {
                    if (openStart < 0)
                    {
                        openStart = abs;
                    }

                    openEndExclusive = abs + 1;
                    matchSinceOpen = 0;
                }
                else if (openStart >= 0)
                {
                    matchSinceOpen++;
                    if (matchSinceOpen > CoalesceGap)
                    {
                        EmitRange(leftRanges, leftOffset + openStart, openEndExclusive - openStart);
                        EmitRange(rightRanges, rightOffset + openStart, openEndExclusive - openStart);
                        openStart = -1;
                        openEndExclusive = -1;
                        matchSinceOpen = 0;
                    }
                }
            }

            pos += n;

            if (progress is not null && DateTime.UtcNow - lastReport >= _progressInterval)
            {
                double pct = commonLen == 0 ? 99.0 : Math.Min(99.0, pos * 99.0 / commonLen);
                IReadOnlyList<HexMatchRange> ls = SnapshotWithOpen(leftRanges, leftOffset, openStart, openEndExclusive);
                IReadOnlyList<HexMatchRange> rs = SnapshotWithOpen(rightRanges, rightOffset, openStart, openEndExclusive);
                progress.Report(new HexDiffProgress(pct, ls, rs));
                lastReport = DateTime.UtcNow;
            }
        }

        if (openStart >= 0)
        {
            EmitRange(leftRanges, leftOffset + openStart, openEndExclusive - openStart);
            EmitRange(rightRanges, rightOffset + openStart, openEndExclusive - openStart);
        }

        if (leftLength > commonLen)
        {
            EmitRange(leftRanges, leftOffset + commonLen, leftLength - commonLen);
        }
        else if (rightLength > commonLen)
        {
            EmitRange(rightRanges, rightOffset + commonLen, rightLength - commonLen);
        }

        var leftFinal = leftRanges.ToArray();
        var rightFinal = rightRanges.ToArray();
        progress?.Report(new HexDiffProgress(100.0, leftFinal, rightFinal));
        return new HexDiffResult(leftFinal, rightFinal);
    }

    private static void EmitRange(List<HexMatchRange> list, long offset, long length)
    {
        while (length > 0)
        {
            int chunk = length > MaxRangeLength ? MaxRangeLength : (int)length;
            list.Add(new HexMatchRange(offset, chunk));
            offset += chunk;
            length -= chunk;
        }
    }

    private static IReadOnlyList<HexMatchRange> SnapshotWithOpen(
        List<HexMatchRange> baseList, long baseOffset,
        long openStart, long openEndExclusive)
    {
        if (openStart < 0 || openEndExclusive <= openStart)
        {
            return baseList.ToArray();
        }

        var copy = new List<HexMatchRange>(baseList.Count + 1);
        copy.AddRange(baseList);
        long offset = baseOffset + openStart;
        long length = openEndExclusive - openStart;
        while (length > 0)
        {
            int chunk = length > MaxRangeLength ? MaxRangeLength : (int)length;
            copy.Add(new HexMatchRange(offset, chunk));
            offset += chunk;
            length -= chunk;
        }

        return copy;
    }
}
