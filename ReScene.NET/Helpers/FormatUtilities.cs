using System.Reflection;

namespace ReScene.NET.Helpers;

/// <summary>
/// Shared formatting utilities used across ViewModels.
/// </summary>
internal static class FormatUtilities
{
    private static readonly string[] _sizeSuffixes = ["B", "KB", "MB", "GB", "TB"];

    /// <summary>
    /// Formats a byte count as a human-readable file size using binary divisions (1024).
    /// </summary>
    public static string FormatSize(long bytes)
    {
        int i = 0;
        double size = bytes;

        while (size >= 1024 && i < _sizeSuffixes.Length - 1)
        {
            size /= 1024;
            i++;
        }

        return $"{size:0.##} {_sizeSuffixes[i]}";
    }

    /// <summary>
    /// Formats the processed/total and remaining-bytes texts for an ISO/scan progress display.
    /// </summary>
    /// <returns>
    /// The "processed / total" text and the remaining-bytes text.
    /// </returns>
    public static (string Processed, string Remaining) FormatScanStats(long processed, long total)
    {
        string processedText = $"{FormatSize(processed)} / {FormatSize(total)}";
        string remainingText = FormatSize(total - processed);
        return (processedText, remainingText);
    }

    /// <summary>
    /// Formats the speed and ETA texts for an ISO/scan progress display, or returns
    /// <see langword="null"/> when there is not yet enough data to estimate
    /// (less than half a second elapsed, or nothing processed).
    /// </summary>
    public static (string Speed, string Eta)? FormatSpeedEta(long processed, long total, double elapsedSeconds)
    {
        if (elapsedSeconds <= 0.5 || processed <= 0)
        {
            return null;
        }

        double bytesPerSec = processed / elapsedSeconds;
        string speedText = $"{FormatSize((long)bytesPerSec)}/s";

        double secondsRemaining = (total - processed) / bytesPerSec;
        string etaText = secondsRemaining < 60
            ? $"{secondsRemaining:F0}s"
            : $"{(int)(secondsRemaining / 60)}m {(int)(secondsRemaining % 60)}s";

        return (speedText, etaText);
    }

    /// <summary>
    /// Gets the default application name string including version info from the entry assembly.
    /// </summary>
    public static string GetDefaultAppName()
    {
        string? version = Assembly.GetEntryAssembly()?
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;

        if (version is null)
        {
            return "ReScene.NET";
        }

        int plus = version.IndexOf('+', StringComparison.Ordinal);
        return plus >= 0
            ? $"ReScene.NET v{version[..plus]} ({version[(plus + 1)..]})"
            : $"ReScene.NET v{version}";
    }

    /// <summary>
    /// Returns the effective default app name: the live <see cref="GetDefaultAppName"/> when the
    /// stored value is blank or an auto-generated "ReScene.NET v…" string (so it refreshes across
    /// upgrades), otherwise the user's custom value unchanged.
    /// </summary>
    public static string NormalizeAppName(string? stored)
    {
        if (string.IsNullOrWhiteSpace(stored)
            || stored.StartsWith("ReScene.NET v", StringComparison.Ordinal))
        {
            return GetDefaultAppName();
        }

        return stored;
    }
}
