using System.Text.Json;
using ReScene.NET.Models;

namespace ReScene.NET.Services;

/// <summary>
/// Persists window state to a JSON file in local app data.
/// </summary>
public class WindowStateService : IWindowStateService
{
    private static readonly JsonSerializerOptions _serializerOptions = new() { WriteIndented = true };
    private static readonly string _appDataDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ReScene.NET");
    private static readonly string _filePath = Path.Combine(_appDataDir, "window-state.json");

    public WindowStateModel? Load()
    {
        try
        {
            if (!File.Exists(_filePath))
            {
                return null;
            }

            string json = File.ReadAllText(_filePath);
            return JsonSerializer.Deserialize<WindowStateModel>(json);
        }
        catch
        {
            return null;
        }
    }

    public void Save(WindowStateModel state)
    {
        try
        {
            Directory.CreateDirectory(_appDataDir);
            string json = JsonSerializer.Serialize(state, _serializerOptions);
            File.WriteAllText(_filePath, json);
        }
        catch
        {
            // Silently ignore persistence errors
        }
    }
}
