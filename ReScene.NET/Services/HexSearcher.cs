using ReScene.NET.Models;

namespace ReScene.NET.Services;

/// <summary>
/// Provides forward and backward byte-pattern search over an <see cref="IHexDataSource"/>.
/// Reads the source in chunks so that arbitrarily large files are supported without
/// loading them entirely into memory.
/// </summary>
public static class HexSearcher
{
    private const int ChunkSize = 64 * 1024;
    private const int DefaultMaxAllMatches = 100_000;

    /// <summary>
    /// Returns the offset of every (non-overlapping) match of <paramref name="pattern"/>
    /// in <paramref name="source"/>, capped at <paramref name="maxResults"/>.
    /// </summary>
    public static IReadOnlyList<long> FindAll(IHexDataSource source, HexSearchPattern pattern, int maxResults = DefaultMaxAllMatches)
    {
        if (source is null || pattern is null || pattern.Bytes.Length == 0 || maxResults <= 0)
        {
            return [];
        }

        List<long> matches = [];
        long pos = 0;

        while (pos < source.Length && matches.Count < maxResults)
        {
            long match = FindForward(source, pattern, pos);
            if (match < 0)
            {
                break;
            }

            matches.Add(match);
            pos = match + pattern.Bytes.Length;
        }

        return matches;
    }

    /// <summary>
    /// Searches forward from <paramref name="startOffset"/> for the first occurrence
    /// of <paramref name="pattern"/> in <paramref name="source"/>.
    /// </summary>
    /// <param name="source">
    /// The data source to search.
    /// </param>
    /// <param name="pattern">
    /// The pattern to find.
    /// </param>
    /// <param name="startOffset">
    /// The byte offset at which to begin searching. Values below zero are clamped to zero.
    /// </param>
    /// <returns>
    /// The zero-based offset of the first match, or <c>-1</c> if not found.
    /// </returns>
    public static long FindForward(IHexDataSource source, HexSearchPattern pattern, long startOffset)
    {
        if (source is null || pattern is null || pattern.Bytes.Length == 0)
        {
            return -1;
        }

        byte[] needle = pattern.Bytes;
        int overlap = needle.Length - 1;
        int take = ChunkSize + overlap;
        byte[] buffer = new byte[take];
        long position = Math.Max(0, startOffset);
        long length = source.Length;

        while (position < length)
        {
            int read = source.Read(position, buffer, 0, take);

            if (read <= 0)
            {
                break;
            }

            int matchIndex = IndexOf(buffer, read, needle);

            if (matchIndex >= 0)
            {
                return position + matchIndex;
            }

            long advance = read - overlap;

            if (advance <= 0)
            {
                break;
            }

            position += advance;
        }

        return -1;
    }

    /// <summary>
    /// Searches backward from <paramref name="beforeOffset"/> for the last occurrence
    /// of <paramref name="pattern"/> in <paramref name="source"/> that ends strictly
    /// before <paramref name="beforeOffset"/>.
    /// </summary>
    /// <param name="source">
    /// The data source to search.
    /// </param>
    /// <param name="pattern">
    /// The pattern to find.
    /// </param>
    /// <param name="beforeOffset">
    /// Only matches whose start position is less than this value are returned.
    /// </param>
    /// <returns>
    /// The zero-based offset of the last match before <paramref name="beforeOffset"/>,
    /// or <c>-1</c> if not found.
    /// </returns>
    public static long FindBackward(IHexDataSource source, HexSearchPattern pattern, long beforeOffset)
    {
        if (source is null || pattern is null || pattern.Bytes.Length == 0)
        {
            return -1;
        }

        byte[] needle = pattern.Bytes;
        int overlap = needle.Length - 1;
        int take = ChunkSize + overlap;
        byte[] buffer = new byte[take];
        long upperBound = Math.Min(beforeOffset, source.Length);

        long chunkEnd = upperBound;

        while (chunkEnd > 0)
        {
            long chunkStart = Math.Max(0, chunkEnd - take);
            int toRead = (int)(chunkEnd - chunkStart);
            int read = source.Read(chunkStart, buffer, 0, toRead);

            if (read <= 0)
            {
                break;
            }

            int matchIndex = LastIndexOf(buffer, read, needle);

            if (matchIndex >= 0)
            {
                long matchOffset = chunkStart + matchIndex;

                if (matchOffset + needle.Length <= upperBound)
                {
                    return matchOffset;
                }
            }

            // When we've already scanned from the start, every preceding byte is covered;
            // continuing would re-read the same overlap region forever.
            if (chunkStart == 0)
            {
                break;
            }

            chunkEnd = chunkStart + overlap;
        }

        return -1;
    }

    private static int IndexOf(byte[] haystack, int haystackLength, byte[] needle)
    {
        int limit = haystackLength - needle.Length;

        for (int i = 0; i <= limit; i++)
        {
            bool found = true;

            for (int j = 0; j < needle.Length; j++)
            {
                if (haystack[i + j] != needle[j])
                {
                    found = false;
                    break;
                }
            }

            if (found)
            {
                return i;
            }
        }

        return -1;
    }

    private static int LastIndexOf(byte[] haystack, int haystackLength, byte[] needle)
    {
        int limit = haystackLength - needle.Length;

        for (int i = limit; i >= 0; i--)
        {
            bool found = true;

            for (int j = 0; j < needle.Length; j++)
            {
                if (haystack[i + j] != needle[j])
                {
                    found = false;
                    break;
                }
            }

            if (found)
            {
                return i;
            }
        }

        return -1;
    }
}
