using System.Reflection;

namespace ReScene.App.Core.Helpers;

/// <summary>
/// Shared formatting utilities used across ViewModels.
/// </summary>
public static class FormatUtilities
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

    /// <summary>Git's default abbreviated-hash length.</summary>
    private const int ShortCommitLength = 7;

    /// <summary>
    /// Abbreviates SemVer build metadata to a short commit hash when the metadata IS a commit
    /// hash, and returns it unchanged otherwise.
    /// </summary>
    /// <remarks>
    /// <para>
    /// SourceLink puts the FULL 40-character SHA after the <c>+</c> in
    /// <see cref="AssemblyInformationalVersionAttribute"/>, which made the stamped app name read
    /// <c>ReScene Manager v1.0.0 (4f12c881199b52a1e5594f5761cd8ee10f1e5788)</c> — accurate but
    /// unreadable, and it is written into every SRR this app produces.
    /// </para>
    /// <para>
    /// Deliberately narrow: build metadata is free-form in SemVer, so <c>+build.42</c> or a
    /// date-stamped <c>+20260817</c> are equally legal and truncating those would corrupt them.
    /// Only a complete SHA-1 (40) or SHA-256 (64) hex string is abbreviated. Note a purely numeric
    /// build counter IS valid hex, which is why the length test is exact rather than a minimum.
    /// Anything carrying a suffix such as <c>-dirty</c> fails the hex test and is left whole.
    /// </para>
    /// </remarks>
    internal static string ShortenBuildMetadata(string metadata) =>
        metadata.Length is 40 or 64 && metadata.All(char.IsAsciiHexDigit)
            ? metadata[..ShortCommitLength]
            : metadata;

    /// <summary>
    /// Gets the default application name string including version info from the entry assembly.
    /// The commit hash is abbreviated — see <see cref="ShortenBuildMetadata"/>.
    /// </summary>
    public static string GetDefaultAppName()
    {
        string? version = Assembly.GetEntryAssembly()?
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;

        if (version is null)
        {
            return "ReScene Manager";
        }

        int plus = version.IndexOf('+', StringComparison.Ordinal);
        return plus >= 0
            ? $"ReScene Manager v{version[..plus]} ({ShortenBuildMetadata(version[(plus + 1)..])})"
            : $"ReScene Manager v{version}";
    }

    /// <summary>
    /// Returns the effective default app name: the live <see cref="GetDefaultAppName"/> when the
    /// stored value is blank or an auto-generated "…v…" string (so it refreshes across upgrades),
    /// otherwise the user's custom value unchanged. The legacy "ReScene.NET v" prefix MUST stay
    /// matched: settings written by the WPF era AND by v2.0.0 (which still stamped the old name)
    /// carry it — dropping the match would freeze those installs on the old stamp forever.
    /// </summary>
    public static string NormalizeAppName(string? stored)
    {
        if (string.IsNullOrWhiteSpace(stored)
            || stored.StartsWith("ReScene Manager v", StringComparison.Ordinal)
            || stored.StartsWith("ReScene.NET v", StringComparison.Ordinal))
        {
            return GetDefaultAppName();
        }

        return stored;
    }
}
