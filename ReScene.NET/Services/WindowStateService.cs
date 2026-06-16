using ReScene.NET.Models;

namespace ReScene.NET.Services;

/// <summary>
/// Persists window state to a JSON file in local app data.
/// </summary>
public class WindowStateService : IWindowStateService
{
    private static readonly string _filePath = JsonFileStore.GetPath("window-state.json");

    public WindowStateModel? Load()
    {
        try
        {
            if (!File.Exists(_filePath))
            {
                return null;
            }

            return JsonFileStore.Read<WindowStateModel>(_filePath);
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
            JsonFileStore.Write(_filePath, state);
        }
        catch
        {
            // Silently ignore persistence errors
        }
    }
}
