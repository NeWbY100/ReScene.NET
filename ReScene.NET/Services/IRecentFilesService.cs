using ReScene.NET.Models;

namespace ReScene.NET.Services;

public interface IRecentFilesService
{
    List<RecentFileEntry> LoadEntries();
    void AddEntry(string filePath);
    void Clear();
}
