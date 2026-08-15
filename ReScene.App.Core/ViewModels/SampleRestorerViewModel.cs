using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ReScene.App.Core.Helpers;
using ReScene.App.Core.Models;
using ReScene.App.Core.Services;
using ReScene.SRS;
namespace ReScene.App.Core.ViewModels;

public partial class SampleRestorerViewModel : OperationViewModelBase
{
    private readonly ISampleRestorerService _service;
    private readonly IFileDialogService _fileDialog;
    private readonly IUiDispatcher _uiDispatcher;

    // Monotonic counter identifying the latest SRR-entry load. Each LoadSRSEntriesAsync bumps it
    // and applies its off-thread result only if still latest, so two overlapping loads (rapid
    // SRRFilePath changes) can't interleave-append entries from two different SRRs.
    private int _srsLoadGeneration;

    // Same latest-wins guard for the media-directory scan (see _srsLoadGeneration).
    private int _matchGeneration;

    public SampleRestorerViewModel(ISampleRestorerService service, IFileDialogService fileDialog, IUiDispatcher uiDispatcher)
    {
        _service = service;
        _fileDialog = fileDialog;
        _uiDispatcher = uiDispatcher;

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
    public partial string OverallProgressText { get; set; } = string.Empty;

    // Entries
    public ObservableCollection<SRSFileEntry> SRSEntries { get; } = [];

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
            FileDialogFilters.SRRFiles, SRRFilePath);

        if (path is null)
        {
            return;
        }

        SRRFilePath = path;
    }

    [RelayCommand]
    private async Task BrowseMediaDirectoryAsync()
    {
        string? path = await _fileDialog.OpenFolderAsync("Select Media Directory",
            string.IsNullOrWhiteSpace(MediaDirectoryPath) ? SRRFilePath : MediaDirectoryPath); // media usually sits near the SRR

        if (path is null)
        {
            return;
        }

        MediaDirectoryPath = path;
        await MatchMediaFilesAsync();
    }

    [RelayCommand]
    private async Task BrowseOutputDirectoryAsync()
    {
        string? path = await _fileDialog.OpenFolderAsync("Select Output Directory", OutputDirectoryPath);

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
        Cancel();
        Log("Cancellation requested...");
    }

    [RelayCommand]
    private Task SaveLogAsync() => SaveLogToFileAsync(_fileDialog);

    internal async Task LoadSRSEntriesAsync()
    {
        int loadGeneration = ++_srsLoadGeneration;

        foreach (SRSFileEntry old in SRSEntries)
        {
            old.PropertyChanged -= OnEntryPropertyChanged;
        }

        SRSEntries.Clear();

        // Capture the path so a later SRRFilePath change can't retarget this in-flight read.
        string srrPath = SRRFilePath;

        try
        {
            // Parse the SRR off the UI thread so a large SRR doesn't freeze it; the continuation
            // resumes on the UI thread (captured context) to populate the bound collection.
            List<SRSEntryInfo> entries = await Task.Run(() => _service.GetSRSEntries(srrPath));

            // A newer load started while we were parsing — discard this stale result so the
            // collection isn't populated with entries from a superseded SRR.
            if (loadGeneration != _srsLoadGeneration)
            {
                return;
            }

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
            if (loadGeneration != _srsLoadGeneration)
            {
                return;
            }

            Log($"Error reading SRR: {ex.Message}");
            SRRStatus = FieldStatus.Error($"Could not read this SRR: {ex.Message}");
        }
    }

    private async Task MatchMediaFilesAsync()
    {
        // Bump first, before the early-return, so clearing/invalidating the media directory while
        // a scan is in flight still supersedes it (its continuation then discards its stale result).
        int matchGeneration = ++_matchGeneration;

        if (string.IsNullOrWhiteSpace(MediaDirectoryPath) || !Directory.Exists(MediaDirectoryPath))
        {
            return;
        }

        string mediaDir = MediaDirectoryPath;

        Dictionary<string, string> byName;
        try
        {
            // Enumerate the media tree off the UI thread — a large recursive folder would otherwise
            // freeze it. Directory.Exists above is only a hint; GetFiles can still throw partway
            // through (permissions, a path removed mid-scan), so this is guarded.
            byName = await Task.Run(() =>
            {
                string[] mediaFiles = Directory.GetFiles(mediaDir, "*.*", SearchOption.AllDirectories);

                // Build lookup: filename → full path
                var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (string file in mediaFiles)
                {
                    map.TryAdd(Path.GetFileName(file), file);
                }

                return map;
            });
        }
        catch (Exception ex)
        {
            if (matchGeneration != _matchGeneration)
            {
                return;
            }

            Log($"Error scanning media directory: {ex.Message}");
            MatchStatus = FieldStatus.Error($"Could not scan this folder: {ex.Message}");
            return;
        }

        // A newer scan (or a superseding directory change) started while we enumerated — discard.
        if (matchGeneration != _matchGeneration)
        {
            return;
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

        _ = LoadSRREntriesAndMatchAsync();
    }

    private async Task LoadSRREntriesAndMatchAsync()
    {
        await LoadSRSEntriesAsync();

        if (!string.IsNullOrWhiteSpace(MediaDirectoryPath))
        {
            await MatchMediaFilesAsync();
        }
    }

    partial void OnMediaDirectoryPathChanged(string value)
    {
        if (!string.IsNullOrWhiteSpace(value) && SRSEntries.Count > 0)
        {
            // Fire-and-forget: MatchMediaFilesAsync has its own try/catch, so no exception escapes
            // to become unobserved. Its latest-wins guard handles overlap with an in-flight scan.
            _ = MatchMediaFilesAsync();
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
