namespace ReScene.NET.Models;

/// <summary>
/// User-editable app defaults persisted to settings.json.
/// </summary>
public sealed class AppSettings
{
    /// <summary>
    /// Gets or sets the default app name used when creating SRR or SRS files.
    /// </summary>
    public string DefaultAppName { get; set; } = "ReScene.NET";

    /// <summary>
    /// Gets or sets the default output directory pre-filled into Output paths.
    /// </summary>
    public string DefaultOutputDirectory { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the maximum number of entries kept in the recent files list.
    /// </summary>
    public int RecentFilesLimit { get; set; } = 10;
}
