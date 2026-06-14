using ReScene.NET.Helpers;

namespace ReScene.NET.Models;

/// <summary>
/// User-editable app defaults persisted to settings.json.
/// </summary>
public sealed class AppSettings
{
    /// <summary>
    /// Gets or sets the default app name used when creating SRR or SRS files.
    /// </summary>
    public string DefaultAppName { get; set; } = FormatUtilities.GetDefaultAppName();

    /// <summary>
    /// Gets or sets the default output directory pre-filled into Output paths.
    /// </summary>
    public string DefaultOutputDirectory { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the maximum number of entries kept in the recent files list.
    /// </summary>
    public int RecentFilesLimit { get; set; } = 10;

    /// <summary>
    /// Gets or sets the maximum number of EBML elements parsed when opening an MKV/WebM file in
    /// the Inspector or Compare views. Higher values show more of huge files but load slower.
    /// </summary>
    public int MkvMaxElements { get; set; } = ReScene.Core.Comparison.MKVFileData.DefaultMaxElements;

    /// <summary>
    /// Gets or sets the default WinRAR versions folder pre-filled into the RAR Reconstructor.
    /// </summary>
    public string ReconstructWinRarPath { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the default output folder pre-filled into the RAR Reconstructor. Deliberately
    /// separate from <see cref="DefaultOutputDirectory"/>: reconstruction wipes this folder's
    /// contents before starting.
    /// </summary>
    public string ReconstructOutputPath { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the persisted UI mode. Null means "not yet chosen" — resolved at load time.
    /// </summary>
    public UserMode? Mode { get; set; }
}
