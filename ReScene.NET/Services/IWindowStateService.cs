using ReScene.NET.Models;

namespace ReScene.NET.Services;

/// <summary>
/// Loads and saves the main window position, size, and UI state.
/// </summary>
public interface IWindowStateService
{
    public WindowStateModel? Load();
    public void Save(WindowStateModel state);
}
