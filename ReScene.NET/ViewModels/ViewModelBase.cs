using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace ReScene.NET.ViewModels;

public abstract class ViewModelBase : ObservableObject
{
    /// <summary>
    /// Appends a timestamped log entry to the specified collection.
    /// </summary>
    protected static void AppendLogEntry(ObservableCollection<string> entries, string message)
    {
        entries.Add($"{DateTime.Now:HH:mm:ss} {message}");
    }
}
