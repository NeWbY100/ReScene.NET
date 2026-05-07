using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ReScene.NET.Helpers;
using ReScene.NET.Services;
using ReScene.SRS;

namespace ReScene.NET.ViewModels;

public partial class SampleRestorerViewModel : ViewModelBase
{
    private readonly ISampleRestorerService _service;
    private readonly IFileDialogService _fileDialog;
    private CancellationTokenSource? _cts;

    public SampleRestorerViewModel(ISampleRestorerService service, IFileDialogService fileDialog)
    {
        _service = service;
        _fileDialog = fileDialog;

        _service.Progress += OnProgress;
    }

    // SRR file
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RestoreCommand))]
    private string _sRRFilePath = string.Empty;

    // Media directory
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RestoreCommand))]
    private string _mediaDirectoryPath = string.Empty;

    // Output directory
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RestoreCommand))]
    private string _outputDirectoryPath = string.Empty;

    // Progress
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RestoreCommand))]
    private bool _isRestoring;

    [ObservableProperty]
    private bool _showProgress;

    [ObservableProperty]
    private int _progressPercent;

    [ObservableProperty]
    private string _progressMessage = string.Empty;

    [ObservableProperty]
    private string _overallProgressText = string.Empty;

    // Entries
    public ObservableCollection<SRSFileEntry> SRSEntries { get; } = [];

    // Log
    public ObservableCollection<string> LogEntries { get; } = [];

    [RelayCommand]
    private async Task BrowseSrrAsync()
    {
        string? path = await _fileDialog.OpenFileAsync("Select SRR File",
            FileDialogFilters.SRRFiles);

        if (path is null)
        {
            return;
        }

        SRRFilePath = path;
        LoadSrsEntries();

        if (!string.IsNullOrWhiteSpace(MediaDirectoryPath))
        {
            MatchMediaFiles();
        }
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
                        entry.Status = $"OK ({result.ActualCrc:X8})";
                        Log($"    CRC match: {result.ActualCrc:X8}");
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

    private void LoadSrsEntries()
    {
        foreach (SRSFileEntry old in SRSEntries)
        {
            old.PropertyChanged -= OnEntryPropertyChanged;
        }

        SRSEntries.Clear();

        try
        {
            List<SRSEntryInfo> entries = _service.GetSrsEntries(SRRFilePath);

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
        }
        catch (Exception ex)
        {
            Log($"Error reading SRR: {ex.Message}");
        }
    }

    private void MatchMediaFiles()
    {
        if (string.IsNullOrWhiteSpace(MediaDirectoryPath) || !Directory.Exists(MediaDirectoryPath))
        {
            return;
        }

        var mediaFiles = Directory.GetFiles(MediaDirectoryPath, "*.*", SearchOption.AllDirectories);

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

        RestoreCommand.NotifyCanExecuteChanged();
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
        Application.Current.Dispatcher.BeginInvoke(() =>
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
        private string _sRSFileName = string.Empty;

        [ObservableProperty]
        private string _sampleFileName = string.Empty;

        [ObservableProperty]
        private string _mediaFilePath = string.Empty;

        [ObservableProperty]
        private string _status = "Pending";

        [ObservableProperty]
        private bool _isSelected = true;
    }
}
