using System.Text.Json;

namespace ReScene.NET.Services;

/// <summary>
/// Narrow scaffolding shared by the JSON-in-LocalAppData services
/// (<see cref="AppSettingsService"/>, <see cref="RecentFilesService"/>,
/// <see cref="WindowStateService"/>). Centralizes the app-data directory, the
/// indented serializer options, and the raw write / read-deserialize primitives.
/// Error handling (try/catch + logging) is intentionally left to each caller.
/// </summary>
internal static class JsonFileStore
{
    /// <summary>
    /// The <c>%LOCALAPPDATA%\ReScene.NET</c> directory used for all persisted JSON files.
    /// </summary>
    public static string AppDataDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ReScene.NET");

    private static readonly JsonSerializerOptions _serializerOptions = new() { WriteIndented = true };

    /// <summary>
    /// Returns the full path of <paramref name="fileName"/> within <see cref="AppDataDirectory"/>.
    /// </summary>
    public static string GetPath(string fileName) => Path.Combine(AppDataDirectory, fileName);

    /// <summary>
    /// Serializes <paramref name="value"/> as indented JSON and writes it to
    /// <paramref name="filePath"/>, creating <see cref="AppDataDirectory"/> if needed.
    /// Does not catch exceptions — callers own their error handling.
    /// </summary>
    public static void Write<T>(string filePath, T value)
    {
        Directory.CreateDirectory(AppDataDirectory);
        string json = JsonSerializer.Serialize(value, _serializerOptions);
        File.WriteAllText(filePath, json);
    }

    /// <summary>
    /// Reads and deserializes the JSON at <paramref name="filePath"/>. Performs no
    /// existence check (callers decide when to read) and does not catch exceptions.
    /// </summary>
    public static T? Read<T>(string filePath)
        => JsonSerializer.Deserialize<T>(File.ReadAllText(filePath));
}
