using ReScene.NET.Models;

namespace ReScene.NET.Services;

/// <summary>
/// Loads and saves <see cref="AppSettings"/> from disk.
/// </summary>
public interface IAppSettingsService
{
    /// <summary>
    /// Loads settings from disk; returns defaults if no file exists or load fails.
    /// </summary>
    public AppSettings Load();

    /// <summary>
    /// Persists settings to disk.
    /// </summary>
    public void Save(AppSettings settings);

    /// <summary>
    /// Raised after a successful <see cref="Save"/>.
    /// </summary>
    public event EventHandler? Changed;
}
