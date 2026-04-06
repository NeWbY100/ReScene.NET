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
}
