using System.Text.Json;
using ReScene.NET.Models;

namespace ReScene.NET.Services;

public class RecentFilesService : IRecentFilesService
{
    private const int MaxEntries = 10;
    private static readonly string AppDataDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ReScene.NET");
    private static readonly string FilePath = Path.Combine(AppDataDir, "recent.json");

    public List<RecentFileEntry> LoadEntries()
    {
        try
        {
            if (!File.Exists(FilePath))
                return [];

            string json = File.ReadAllText(FilePath);
            return JsonSerializer.Deserialize<List<RecentFileEntry>>(json) ?? [];
        }
        catch
        {
            return [];
        }
    }

    public void AddEntry(string filePath)
    {
        var entries = LoadEntries();

        entries.RemoveAll(e => string.Equals(e.FilePath, filePath, StringComparison.OrdinalIgnoreCase));

        entries.Insert(0, new RecentFileEntry
        {
            FilePath = filePath,
            FileName = Path.GetFileName(filePath),
            LastOpened = DateTime.Now
        });

        if (entries.Count > MaxEntries)
            entries.RemoveRange(MaxEntries, entries.Count - MaxEntries);

        Save(entries);
    }

    public void Clear()
    {
        Save([]);
    }

    private static void Save(List<RecentFileEntry> entries)
    {
        try
        {
            Directory.CreateDirectory(AppDataDir);
            string json = JsonSerializer.Serialize(entries, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(FilePath, json);
        }
        catch
        {
            // Silently ignore persistence errors
        }
    }
}
