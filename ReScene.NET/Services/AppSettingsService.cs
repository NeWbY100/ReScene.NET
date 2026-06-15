using System.Diagnostics;
using System.Text.Json;
using ReScene.NET.Models;

namespace ReScene.NET.Services;

public class AppSettingsService : IAppSettingsService
{
    private static readonly JsonSerializerOptions _serializerOptions = new() { WriteIndented = true };
    private static readonly string _appDataDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ReScene.NET");
    private static readonly string _filePath = Path.Combine(_appDataDir, "settings.json");

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
                ? JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(_filePath)) ?? new AppSettings()
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
            Directory.CreateDirectory(_appDataDir);
            string json = JsonSerializer.Serialize(settings, _serializerOptions);
            File.WriteAllText(_filePath, json);
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
