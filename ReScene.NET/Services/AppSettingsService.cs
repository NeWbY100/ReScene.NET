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

    public AppSettings Load()
    {
        try
        {
            if (!File.Exists(_filePath))
            {
                return new AppSettings();
            }

            string json = File.ReadAllText(_filePath);
            return JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();
        }
        catch
        {
            return new AppSettings();
        }
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
