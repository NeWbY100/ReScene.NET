using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ReScene.NET.Services;
using ReScene.SRS;

namespace ReScene.NET.ViewModels;

public partial class SrsReconstructorViewModel : ViewModelBase
{
    private readonly ISrsReconstructionService _service;
    private readonly IFileDialogService _fileDialog;
    private CancellationTokenSource? _cts;
    private string? _extractedTempFile;

    public SrsReconstructorViewModel(ISrsReconstructionService service, IFileDialogService fileDialog)
    {
        _service = service;
        _fileDialog = fileDialog;

        _service.Progress += OnProgress;
    }

    // SRS file
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RebuildCommand))]
    private string _srsFilePath = string.Empty;

    // Media file
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RebuildCommand))]
    private string _mediaFilePath = string.Empty;

    // ISO support
    [ObservableProperty]
    private bool _isIsoSource;

    [ObservableProperty]
    private string _isoFilePath = string.Empty;

    public ObservableCollection<string> IsoMediaFiles { get; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RebuildCommand))]
    private string? _selectedIsoMediaFile;

    // Output
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RebuildCommand))]
    private string _outputPath = string.Empty;

    // Progress
    [ObservableProperty]
    private int _progressPercent;

    [ObservableProperty]
    private string _progressMessage = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RebuildCommand))]
    private bool _isRebuilding;

    [ObservableProperty]
    private bool _showProgress;

    // Result
    [ObservableProperty]
    private string _resultSummary = string.Empty;

    [ObservableProperty]
    private bool _showResult;

    [ObservableProperty]
    private bool _resultSuccess;

    // Log
    public ObservableCollection<string> LogEntries { get; } = [];

    [RelayCommand]
    private async Task BrowseSrsAsync()
    {
        string? path = await _fileDialog.OpenFileAsync("Select SRS File",
            ["SRS Files|*.srs", "All Files|*.*"]);

        if (path is not null)
        {
            SrsFilePath = path;
            AutoSetOutputPath(path);
        }
    }

    [RelayCommand]
    private async Task BrowseMediaAsync()
    {
        string? path = await _fileDialog.OpenFileAsync("Select Media File",
        [
            "Video Files|*.avi;*.mkv;*.mp4;*.wmv;*.m4v;*.mov",
            "Audio Files|*.flac;*.mp3",
            "Stream Files|*.vob;*.m2ts;*.ts;*.mpg;*.mpeg;*.evo;*.m2v",
            "ISO Images|*.iso;*.img",
            "All Files|*.*"
        ]);

        if (path is null)
        {
            return;
        }

        if (IsoMediaExtractor.IsIsoFile(path))
        {
            LoadIsoFile(path);
        }
        else
        {
            IsIsoSource = false;
            IsoFilePath = string.Empty;
            IsoMediaFiles.Clear();
            SelectedIsoMediaFile = null;
            MediaFilePath = path;
        }
    }

    [RelayCommand]
    private async Task BrowseOutputAsync()
    {
        string? path = await _fileDialog.SaveFileAsync(
            "Save Reconstructed Sample", ".*",
            ["All Files|*.*"],
            string.IsNullOrWhiteSpace(OutputPath) ? null : Path.GetFileName(OutputPath));

        if (path is not null)
        {
            OutputPath = path;
        }
    }

    private bool CanRebuild()
    {
        if (IsRebuilding || string.IsNullOrWhiteSpace(SrsFilePath) || string.IsNullOrWhiteSpace(OutputPath))
        {
            return false;
        }

        if (IsIsoSource)
        {
            return !string.IsNullOrWhiteSpace(SelectedIsoMediaFile);
        }

        return !string.IsNullOrWhiteSpace(MediaFilePath);
    }

    [RelayCommand(CanExecute = nameof(CanRebuild))]
    private async Task RebuildAsync()
    {
        IsRebuilding = true;
        ShowProgress = true;
        ShowResult = false;
        ProgressPercent = 0;
        ProgressMessage = "Starting...";
        LogEntries.Clear();

        _cts = new CancellationTokenSource();
        var sw = Stopwatch.StartNew();

        try
        {
            Log("Starting SRS reconstruction...");
            Log($"SRS file:   {SrsFilePath}");

            string mediaPath;

            // If ISO source, extract the selected file first
            if (IsIsoSource && !string.IsNullOrWhiteSpace(SelectedIsoMediaFile))
            {
                Log($"ISO image:  {IsoFilePath}");
                Log($"Media file: {SelectedIsoMediaFile}");
                Log("Extracting media file from ISO...");

                string tempDir = Path.Combine(Path.GetTempPath(), "ReScene.NET", Guid.NewGuid().ToString("N")[..8]);
                Directory.CreateDirectory(tempDir);
                string tempFile = Path.Combine(tempDir, Path.GetFileName(SelectedIsoMediaFile));
                _extractedTempFile = tempFile;

                await IsoMediaExtractor.ExtractFileAsync(
                    IsoFilePath, SelectedIsoMediaFile, tempFile,
                    p => Application.Current.Dispatcher.BeginInvoke(() =>
                    {
                        ProgressPercent = p / 2; // 0-50% for extraction
                        ProgressMessage = $"Extracting from ISO... {p}%";
                    }), _cts.Token);

                Log($"Extracted to temp: {tempFile}");
                mediaPath = tempFile;
            }
            else
            {
                Log($"Media file: {MediaFilePath}");
                mediaPath = MediaFilePath;
            }

            Log($"Output:     {OutputPath}");

            SrsReconstructionResult result = await _service.RebuildAsync(
                SrsFilePath, mediaPath, OutputPath, _cts.Token);

            sw.Stop();

            Log($"Reconstruction complete in {sw.Elapsed.TotalSeconds:F1}s");
            Log($"  Expected CRC: {result.ExpectedCrc:X8}");
            Log($"  Actual CRC:   {result.ActualCrc:X8}");
            Log($"  CRC match:    {(result.CrcMatch ? "YES" : "NO")}");
            Log($"  Expected size: {result.ExpectedSize:N0} bytes");
            Log($"  Actual size:   {result.ActualSize:N0} bytes");

            if (result.Success)
            {
                ProgressPercent = 100;
                ProgressMessage = "Complete!";
                ResultSuccess = true;

                ResultSummary = $"CRC32 match: {result.ActualCrc:X8} ({result.ActualSize:N0} bytes)";
            }
            else
            {
                ProgressMessage = "Failed.";
                ResultSuccess = false;
                ResultSummary = result.ErrorMessage ?? "Unknown error";
            }

            ShowResult = true;
        }
        catch (Exception ex)
        {
            ProgressMessage = "Error.";
            ResultSuccess = false;
            ResultSummary = ex.Message;
            ShowResult = true;
            Log($"ERROR: {ex.Message}");
        }
        finally
        {
            IsRebuilding = false;
            _cts?.Dispose();
            _cts = null;
            CleanupTempFile();
        }
    }

    [RelayCommand]
    private void CancelRebuild()
    {
        _cts?.Cancel();
        Log("Cancellation requested...");
    }

    #region ISO Support

    private void LoadIsoFile(string isoPath)
    {
        IsoFilePath = isoPath;
        IsoMediaFiles.Clear();
        SelectedIsoMediaFile = null;

        try
        {
            List<string> files = IsoMediaExtractor.ListMediaFiles(isoPath);
            foreach (string file in files)
            {
                IsoMediaFiles.Add(file);
            }

            IsIsoSource = true;
            MediaFilePath = isoPath;

            if (IsoMediaFiles.Count == 1)
            {
                SelectedIsoMediaFile = IsoMediaFiles[0];
            }

            Log($"ISO loaded: {Path.GetFileName(isoPath)} — {IsoMediaFiles.Count} media file(s) found");
        }
        catch (Exception ex)
        {
            IsIsoSource = false;
            Log($"ERROR reading ISO: {ex.Message}");
            MessageBox.Show(
                $"Unable to read ISO image:\n{ex.Message}",
                "ISO Error",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    partial void OnSelectedIsoMediaFileChanged(string? value)
    {
        RebuildCommand.NotifyCanExecuteChanged();
    }

    private void CleanupTempFile()
    {
        if (_extractedTempFile is null)
        {
            return;
        }

        try
        {
            string? tempDir = Path.GetDirectoryName(_extractedTempFile);
            if (File.Exists(_extractedTempFile))
            {
                File.Delete(_extractedTempFile);
            }

            if (tempDir is not null && Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, true);
            }
        }
        catch
        {
            // Best-effort cleanup
        }

        _extractedTempFile = null;
    }

    #endregion

    private void OnProgress(object? _, SrsReconstructionProgressEventArgs e)
    {
        Application.Current.Dispatcher.BeginInvoke(() =>
        {
            int percent = (int)e.ProgressPercent;
            // If ISO extraction was done, reconstruction progress maps to 50-100%
            if (_extractedTempFile is not null)
            {
                percent = 50 + percent / 2;
            }

            ProgressPercent = percent;
            string msg = e.TotalTracks > 0
                ? $"{e.Phase} (track {e.TrackNumber}/{e.TotalTracks})"
                : e.Phase;
            ProgressMessage = msg;
            Log(msg);
        });
    }

    private void Log(string message)
    {
        string entry = $"{DateTime.Now:HH:mm:ss} {message}";
        LogEntries.Add(entry);
    }

    private void AutoSetOutputPath(string srsPath)
    {
        if (!string.IsNullOrWhiteSpace(OutputPath))
        {
            return;
        }

        // Try to infer the sample file name from the SRS
        try
        {
            var srs = SRSFile.Load(srsPath);
            if (srs.FileData is { } fd && !string.IsNullOrWhiteSpace(fd.FileName))
            {
                string dir = Path.GetDirectoryName(srsPath) ?? ".";
                OutputPath = Path.Combine(dir, fd.FileName);
                return;
            }
        }
        catch
        {
            // Fall through to extension-based default
        }

        // Fallback: same directory, .avi extension
        string fallbackDir = Path.GetDirectoryName(srsPath) ?? ".";
        string name = Path.GetFileNameWithoutExtension(srsPath);
        OutputPath = Path.Combine(fallbackDir, name + ".avi");
    }
}
