using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ReScene.NET.Helpers;
using ReScene.NET.Models;
using ReScene.NET.Services;
using ReScene.SRS;

namespace ReScene.NET.ViewModels;

public partial class SampleRestorerViewModel : ViewModelBase
{
    private readonly ISampleRestorerService _service;
    private readonly IFileDialogService _fileDialog;
    private readonly IUiDispatcher _uiDispatcher;
    private CancellationTokenSource? _cts;

    public SampleRestorerViewModel(ISampleRestorerService service, IFileDialogService fileDialog, IUiDispatcher? uiDispatcher = null)
    {
        _service = service;
        _fileDialog = fileDialog;
        _uiDispatcher = uiDispatcher ?? new WpfDispatcher();

        _service.Progress += OnProgress;
    }

    // SRR file
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RestoreCommand))]
    public partial string SRRFilePath { get; set; } = string.Empty;

    // Media directory
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RestoreCommand))]
    public partial string MediaDirectoryPath { get; set; } = string.Empty;

    // Output directory
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RestoreCommand))]
    public partial string OutputDirectoryPath { get; set; } = string.Empty;

    // Status indicators
    [ObservableProperty]
    public partial FieldStatus SRRStatus { get; set; } = FieldStatus.None;

    [ObservableProperty]
    public partial FieldStatus MatchStatus { get; set; } = FieldStatus.None;

    // Progress
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RestoreCommand))]
    public partial bool IsRestoring { get; set; }

    [ObservableProperty]
    public partial bool ShowProgress { get; set; }

    [ObservableProperty]
    public partial int ProgressPercent { get; set; }

    [ObservableProperty]
    public partial string ProgressMessage { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string OverallProgressText { get; set; } = string.Empty;

    // Entries
    public ObservableCollection<SRSFileEntry> SRSEntries { get; } = [];

    // Log
    public ObservableCollection<string> LogEntries { get; } = [];

    /// <summary>
    /// Clears all user-entered state back to a freshly-constructed default so a Beginner
    /// wizard opens clean. No-op while a restore is in progress (e.g. started from the
    /// Advanced tab) so an active run isn't disrupted.
    /// </summary>
    public void Reset()
    {
        if (IsRestoring)
        {
            return;
        }

        SRRFilePath = string.Empty;
        MediaDirectoryPath = string.Empty;
        OutputDirectoryPath = string.Empty;
        SRRStatus = FieldStatus.None;
        MatchStatus = FieldStatus.None;

        // Unsubscribe entry handlers before clearing (mirrors LoadSRSEntries).
        foreach (SRSFileEntry old in SRSEntries)
        {
            old.PropertyChanged -= OnEntryPropertyChanged;
        }

        SRSEntries.Clear();

        ProgressPercent = 0;
        ProgressMessage = string.Empty;
        ShowProgress = false;
        OverallProgressText = string.Empty;
        LogEntries.Clear();
    }

    [RelayCommand]
    private async Task BrowseSRRAsync()
    {
        string? path = await _fileDialog.OpenFileAsync("Select SRR File",
            FileDialogFilters.SRRFiles);

        if (path is null)
        {
            return;
        }

        SRRFilePath = path;
    }

    [RelayCommand]
    private async Task BrowseMediaDirectoryAsync()
    {
        string? path = await _fileDialog.OpenFolderAsync("Select Media Directory");

        if (path is null)
        {
            return;
        }

        MediaDirectoryPath = path;
        MatchMediaFiles();
    }

    [RelayCommand]
    private async Task BrowseOutputDirectoryAsync()
    {
        string? path = await _fileDialog.OpenFolderAsync("Select Output Directory");

        if (path is null)
        {
            return;
        }

        OutputDirectoryPath = path;
    }

    private bool CanRestore() => !IsRestoring
        && !string.IsNullOrWhiteSpace(SRRFilePath)
        && !string.IsNullOrWhiteSpace(MediaDirectoryPath)
        && !string.IsNullOrWhiteSpace(OutputDirectoryPath)
        && SRSEntries.Any(e => e.IsSelected);

    [RelayCommand(CanExecute = nameof(CanRestore))]
    private async Task RestoreAsync()
    {
        IsRestoring = true;
        ShowProgress = true;
        ProgressPercent = 0;
        ProgressMessage = "Starting...";
        LogEntries.Clear();

        _cts = new CancellationTokenSource();
        var sw = Stopwatch.StartNew();

        try
        {
            var selected = SRSEntries.Where(e => e.IsSelected).ToList();
            int total = selected.Count;
            int current = 0;

            Log($"Restoring {total} sample(s)...");

            foreach (SRSFileEntry? entry in selected)
            {
                if (_cts.Token.IsCancellationRequested)
                {
                    break;
                }

                current++;
                OverallProgressText = $"Restoring {current} of {total}...";
                entry.Status = "Restoring...";

                if (string.IsNullOrWhiteSpace(entry.MediaFilePath))
                {
                    entry.Status = "Failed: No media file matched";
                    Log($"  [{current}/{total}] {entry.SRSFileName} — no media file matched");
                    continue;
                }

                string outputPath = Path.Combine(OutputDirectoryPath, entry.SampleFileName);
                Log($"  [{current}/{total}] {entry.SRSFileName} → {entry.SampleFileName}");

                try
                {
                    SRSReconstructionResult result = await _service.RestoreSampleAsync(
                        SRRFilePath, entry.SRSFileName,
                        entry.MediaFilePath, outputPath, _cts.Token);

                    if (result.Success)
                    {
                        entry.Status = $"OK ({result.ActualCRC:X8})";
                        Log($"    CRC match: {result.ActualCRC:X8}");
                    }
                    else
                    {
                        entry.Status = $"Failed: {result.ErrorMessage}";
                        Log($"    Failed: {result.ErrorMessage}");
                    }
                }
                catch (OperationCanceledException)
                {
                    entry.Status = "Cancelled";
                    Log("Cancelled.");
                    break;
                }
                catch (Exception ex)
                {
                    entry.Status = $"Failed: {ex.Message}";
                    Log($"    Error: {ex.Message}");
                }
            }

            sw.Stop();
            ProgressPercent = 100;

            int succeeded = selected.Count(e => e.Status.StartsWith("OK", StringComparison.Ordinal));
            int failed = selected.Count(e => e.Status.StartsWith("Failed", StringComparison.Ordinal));
            OverallProgressText = $"Done — {succeeded} succeeded, {failed} failed";
            ProgressMessage = $"Completed in {sw.Elapsed.TotalSeconds:F1}s";
            Log($"Completed in {sw.Elapsed.TotalSeconds:F1}s — {succeeded} succeeded, {failed} failed");
        }
        catch (Exception ex)
        {
            ProgressMessage = "Error.";
            Log($"ERROR: {ex.Message}");
        }
        finally
        {
            IsRestoring = false;
            _cts?.Dispose();
            _cts = null;
        }
    }

    [RelayCommand]
    private void CancelRestore()
    {
        _cts?.Cancel();
        Log("Cancellation requested...");
    }

    [RelayCommand]
    private async Task SaveLogAsync()
    {
        if (LogEntries.Count == 0)
        {
            return;
        }

        string? path = await _fileDialog.SaveFileAsync(
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

    private void LoadSRSEntries()
    {
        foreach (SRSFileEntry old in SRSEntries)
        {
            old.PropertyChanged -= OnEntryPropertyChanged;
        }

        SRSEntries.Clear();

        try
        {
            List<SRSEntryInfo> entries = _service.GetSRSEntries(SRRFilePath);

            foreach (SRSEntryInfo info in entries)
            {
                var entry = new SRSFileEntry
                {
                    SRSFileName = info.SRSFileName,
                    SampleFileName = info.SampleFileName,
                    IsSelected = true
                };
                entry.PropertyChanged += OnEntryPropertyChanged;
                SRSEntries.Add(entry);
            }

            Log($"Found {entries.Count} SRS file(s) in SRR");
            SRRStatus = entries.Count > 0
                ? FieldStatus.Ok($"{entries.Count} embedded SRS sample(s) found.")
                : FieldStatus.Warning("No embedded SRS samples found in this SRR.");
        }
        catch (Exception ex)
        {
            Log($"Error reading SRR: {ex.Message}");
            SRRStatus = FieldStatus.Error($"Could not read this SRR: {ex.Message}");
        }
    }

    private void MatchMediaFiles()
    {
        if (string.IsNullOrWhiteSpace(MediaDirectoryPath) || !Directory.Exists(MediaDirectoryPath))
        {
            return;
        }

        string[] mediaFiles = Directory.GetFiles(MediaDirectoryPath, "*.*", SearchOption.AllDirectories);

        // Build lookup: filename → full path
        var byName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (string file in mediaFiles)
        {
            byName.TryAdd(Path.GetFileName(file), file);
        }

        int found = 0;
        foreach (SRSFileEntry entry in SRSEntries)
        {
            if (byName.TryGetValue(entry.SampleFileName, out string? match))
            {
                entry.MediaFilePath = match;
                entry.Status = "Found";
                found++;
            }
            else
            {
                entry.MediaFilePath = string.Empty;
                entry.Status = "Not found";
            }
        }

        Log($"Matched {found} of {SRSEntries.Count} file(s) in media directory");

        MatchStatus = found == SRSEntries.Count && found > 0
            ? FieldStatus.Ok($"Matched all {found} sample(s) to media files.")
            : found > 0
                ? FieldStatus.Warning($"Matched {found} of {SRSEntries.Count} sample(s); the rest need a media file.")
                : FieldStatus.Warning("No samples matched a file in this folder.");

        if (string.IsNullOrWhiteSpace(OutputDirectoryPath))
        {
            OutputDirectoryPath = MediaDirectoryPath;
        }

        RestoreCommand.NotifyCanExecuteChanged();
    }

    partial void OnSRRFilePathChanged(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            SRRStatus = FieldStatus.None;
            return;
        }

        LoadSRSEntries();

        if (!string.IsNullOrWhiteSpace(MediaDirectoryPath))
        {
            MatchMediaFiles();
        }
    }

    partial void OnMediaDirectoryPathChanged(string value)
    {
        if (!string.IsNullOrWhiteSpace(value) && SRSEntries.Count > 0)
        {
            MatchMediaFiles();
        }
    }

    private void OnEntryPropertyChanged(object? _, PropertyChangedEventArgs e) => RestoreCommand.NotifyCanExecuteChanged();

    private void OnProgress(object? _, SRSReconstructionProgressEventArgs e)
    {
        _uiDispatcher.Post(() =>
        {
            ProgressPercent = (int)e.ProgressPercent;
            string msg = e.TotalTracks > 0
                ? $"{e.Phase} (track {e.TrackNumber}/{e.TotalTracks})"
                : e.Phase;
            ProgressMessage = msg;
        });
    }

    private void Log(string message) => AppendLogEntry(LogEntries, message);

    public partial class SRSFileEntry : ObservableObject
    {
        [ObservableProperty]
        public partial string SRSFileName { get; set; } = string.Empty;

        [ObservableProperty]
        public partial string SampleFileName { get; set; } = string.Empty;

        [ObservableProperty]
        public partial string MediaFilePath { get; set; } = string.Empty;

        [ObservableProperty]
        public partial string Status { get; set; } = "Pending";

        [ObservableProperty]
        public partial bool IsSelected { get; set; } = true;
    }
}
