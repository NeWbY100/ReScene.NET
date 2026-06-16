using ReScene.NET.Models;

namespace ReScene.NET.Services;

public class RecentFilesService(IAppSettingsService appSettingsService) : IRecentFilesService
{
    private static readonly string _filePath = JsonFileStore.GetPath("recent.json");

    public List<RecentFileEntry> LoadEntries()
    {
        try
        {
            if (!File.Exists(_filePath))
            {
                return [];
            }

            return JsonFileStore.Read<List<RecentFileEntry>>(_filePath) ?? [];
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

        int maxEntries = appSettingsService.Load().RecentFilesLimit;

        if (entries.Count > maxEntries)
        {
            entries.RemoveRange(maxEntries, entries.Count - maxEntries);
        }

        Save(entries);
    }

    public void RemoveEntry(string filePath)
    {
        List<RecentFileEntry> entries = LoadEntries();
        entries.RemoveAll(e => string.Equals(e.FilePath, filePath, StringComparison.OrdinalIgnoreCase));
        Save(entries);
    }

    public void Clear() => Save([]);

    private static void Save(List<RecentFileEntry> entries)
    {
        try
        {
            JsonFileStore.Write(_filePath, entries);
        }
        catch
        {
            // Silently ignore persistence errors
        }
    }
}
