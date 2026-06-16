using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using ReScene.NET.Helpers;
using ReScene.NET.Services;

namespace ReScene.NET.ViewModels;

/// <summary>
/// Base for the single-log "operation" ViewModels (SRR/SRS creation, SRS reconstruction,
/// sample restore). Centralizes the log collection, the progress display properties, the
/// log helper, the save-log dialog flow, and the cancellation-token lifecycle. Per-command
/// busy flags and the finally-cleanup remain in each derived ViewModel because they differ.
/// </summary>
public abstract partial class OperationViewModelBase : ViewModelBase
{
    /// <summary>
    /// Backing cancellation source for the current operation. Derived ViewModels assign a
    /// fresh source when starting and dispose it in their own finally block; they read
    /// <c>_cts.Token</c> directly. Use <see cref="Cancel"/> to request cancellation safely.
    /// </summary>
    protected CancellationTokenSource? _cts;

    // Progress
    [ObservableProperty]
    public partial bool ShowProgress { get; set; }

    [ObservableProperty]
    public partial int ProgressPercent { get; set; }

    [ObservableProperty]
    public partial string ProgressMessage { get; set; } = string.Empty;

    // Log
    public ObservableCollection<string> LogEntries { get; } = [];

    /// <summary>
    /// Appends a timestamped entry to <see cref="LogEntries"/>.
    /// </summary>
    protected void Log(string message) => AppendLogEntry(LogEntries, message);

    /// <summary>
    /// Requests cancellation of the running operation. Guarded against the
    /// Cancel-vs-dispose race: a token source already disposed by the operation's
    /// finally block is ignored rather than throwing.
    /// </summary>
    protected void Cancel()
    {
        try
        {
            _cts?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // The operation already completed and disposed its token source.
        }
    }

    /// <summary>
    /// Prompts for a path and writes <see cref="LogEntries"/> to it. No-op when the log is
    /// empty or the user cancels the dialog. Errors are logged rather than thrown.
    /// </summary>
    protected async Task SaveLogToFileAsync(IFileDialogService fileDialog)
    {
        if (LogEntries.Count == 0)
        {
            return;
        }

        string? path = await fileDialog.SaveFileAsync(
            "Save log", ".txt", ["Text Files|*.txt"], "log.txt");

        if (path is null)
        {
            return;
        }

        try
        {
            await LogExporter.SaveAsync(LogEntries, path);
            Log($"Log saved to {Path.GetFileName(path)}");
        }
        catch (Exception ex)
        {
            Log($"ERROR saving log: {ex.Message}");
        }
    }
}
