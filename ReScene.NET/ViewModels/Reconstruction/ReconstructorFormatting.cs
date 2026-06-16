using System.Text.RegularExpressions;

namespace ReScene.NET.ViewModels.Reconstruction;

/// <summary>
/// Pure formatting helpers shared by the RAR Reconstructor view-model and its collaborators:
/// version labels, elapsed/remaining times, and transfer speeds. Output strings are identical to
/// the values the view-model previously produced inline.
/// </summary>
internal static partial class ReconstructorFormatting
{
    [GeneratedRegex(@"(?:win)?(?:rar|wr)(?:-x64|-x32)?-?(\d+)(b\d+)?", RegexOptions.IgnoreCase)]
    private static partial Regex VersionLabelRegex();

    /// <summary>Turns a WinRAR version directory name into a friendly "WinRAR x.yy" label.</summary>
    public static string FormatVersionLabel(string dirName)
    {
        Match m = VersionLabelRegex().Match(dirName);
        if (!m.Success)
        {
            return dirName;
        }

        string digits = m.Groups[1].Value;
        string beta = m.Groups[2].Value;
        string versionStr = digits.Length >= 3
            ? $"{digits[..^2]}.{digits[^2..]}"
            : $"{digits[..^1]}.{digits[^1..]}";

        return $"WinRAR {versionStr}{beta}";
    }

    /// <summary>Formats a duration as either "h:mm:ss" (≥1h) or "mm:ss".</summary>
    public static string FormatTimeSpan(TimeSpan ts)
    {
        if (ts.TotalHours >= 1)
        {
            return $"{(int)ts.TotalHours}:{ts.Minutes:D2}:{ts.Seconds:D2}";
        }

        return $"{ts.Minutes:D2}:{ts.Seconds:D2}";
    }

    /// <summary>Formats a transfer rate as MB/s (≥1 MiB/s) or KB/s.</summary>
    public static string FormatSpeed(double bytesPerSec)
    {
        if (bytesPerSec >= 1024 * 1024)
        {
            return $"{bytesPerSec / (1024 * 1024):F1} MB/s";
        }

        return $"{bytesPerSec / 1024:F1} KB/s";
    }
}
