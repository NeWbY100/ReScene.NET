using System.Diagnostics;
using ReScene.NET.Models;

namespace ReScene.NET.Services;

public class AppSettingsService : IAppSettingsService
{
    private static readonly string _filePath = JsonFileStore.GetPath("settings.json");

    public event EventHandler? Changed;

    /// <summary>
    /// Resolves the effective startup mode: a brand-new install (no file) starts in Beginner;
    /// an existing user whose settings predate this feature (file present, no Mode) stays in Advanced.
    /// </summary>
    public static UserMode ResolveStartupMode(bool settingsFileExisted, UserMode? persistedMode)
        => persistedMode ?? (settingsFileExisted ? UserMode.Advanced : UserMode.Beginner);

    public AppSettings Load()
    {
        bool fileExisted = File.Exists(_filePath);
        AppSettings settings;
        try
        {
            settings = fileExisted
                ? JsonFileStore.Read<AppSettings>(_filePath) ?? new AppSettings()
                : new AppSettings();
        }
        catch (Exception ex)
        {
            // Load runs on the startup path, so nothing here may crash the app: any corrupt or
            // unreadable settings fall back to defaults. The catch is broad on purpose (a custom
            // converter or unsupported member added to AppSettings later could throw other types),
            // but it records why so the failure isn't completely invisible.
            Trace.TraceError($"Failed to load settings from '{_filePath}': {ex.Message}");
            settings = new AppSettings();
        }

        settings.Mode = ResolveStartupMode(fileExisted, settings.Mode);
        return settings;
    }

    public void Save(AppSettings settings)
    {
        try
        {
            JsonFileStore.Write(_filePath, settings);
            Changed?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A failed settings write (disk full, permissions) must not crash whatever UI
            // action triggered it, but it shouldn't vanish silently either.
            Trace.TraceError($"Failed to save settings to '{_filePath}': {ex.Message}");
        }
    }
}
