using System.Text.Json;
using ReScene.NET.Models;

namespace ReScene.NET.Services;

public class RecentFilesService : IRecentFilesService
{
    private const int MaxEntries = 10;
    private static readonly JsonSerializerOptions _serializerOptions = new() { WriteIndented = true };
    private static readonly string _appDataDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ReScene.NET");
    private static readonly string _filePath = Path.Combine(_appDataDir, "recent.json");

    public List<RecentFileEntry> LoadEntries()
    {
        try
        {
            if (!File.Exists(_filePath))
            {
                return [];
            }

            string json = File.ReadAllText(_filePath);
            return JsonSerializer.Deserialize<List<RecentFileEntry>>(json) ?? [];
        }
        catch
        {
            return [];
        }
    }

    public void AddEntry(string filePath)
    {
        List<RecentFileEntry> entries = LoadEntries();

        entries.RemoveAll(e => string.Equals(e.FilePath, filePath, StringComparison.OrdinalIgnoreCase));

        entries.Insert(0, new RecentFileEntry
        {
            FilePath = filePath,
            FileName = Path.GetFileName(filePath),
            LastOpened = DateTime.Now
        });

        if (entries.Count > MaxEntries)
        {
            entries.RemoveRange(MaxEntries, entries.Count - MaxEntries);
        }

        Save(entries);
    }

    public void RemoveEntry(string filePath)
    {
        List<RecentFileEntry> entries = LoadEntries();
        entries.RemoveAll(e => string.Equals(e.FilePath, filePath, StringComparison.OrdinalIgnoreCase));
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
            Directory.CreateDirectory(_appDataDir);
            string json = JsonSerializer.Serialize(entries, _serializerOptions);
            File.WriteAllText(_filePath, json);
        }
        catch
        {
            // Silently ignore persistence errors
        }
    }
}
