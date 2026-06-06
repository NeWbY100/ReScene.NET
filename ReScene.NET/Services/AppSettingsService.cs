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
        catch
        {
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
        catch
        {
        }
    }
}
