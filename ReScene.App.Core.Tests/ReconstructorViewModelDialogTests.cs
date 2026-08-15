using System.Diagnostics;
using ReScene.App.Core.Models;
using ReScene.App.Core.Services;
using ReScene.App.Core.ViewModels;
using ReScene.Core;

namespace ReScene.App.Core.Tests;

/// <summary>
/// Exercises ReconstructorViewModel validation branches that were previously untestable because
/// they called <c>MessageBox.Show</c> directly. Now that the VM routes those calls through
/// <see cref="IFileDialogService"/> and marshals via <see cref="IUiDispatcher"/>, the validation
/// logic in <c>StartAsync</c> can be driven without a WPF UI thread.
/// </summary>
public sealed class ReconstructorViewModelDialogTests : IDisposable
{
    private readonly List<string> _tempDirs = [];

    // ── Fakes ───────────────────────────────────────────────

    /// <summary>Records the dialogs raised so tests can assert on title/message and Confirm answers.</summary>
    private sealed class RecordingFileDialogService : NoOpFileDialogService
    {
        public List<(string Title, string Message)> Errors { get; } = [];
        public List<(string Title, string Message)> Warnings { get; } = [];
        public List<(string Title, string Message)> Infos { get; } = [];

        // Scripted answer for the synchronous Confirm(...) and async ShowConfirmAsync(...) seams.
        public bool ConfirmResult { get; set; } = true;
        public List<(string Title, string Message)> Confirms { get; } = [];

        public override Task<bool> ShowConfirmAsync(string title, string message)
        {
            Confirms.Add((title, message));
            return Task.FromResult(ConfirmResult);
        }

        public override void ShowError(string title, string message) => Errors.Add((title, message));
        public override void ShowWarning(string title, string message) => Warnings.Add((title, message));
        public override void ShowInfo(string title, string message) => Infos.Add((title, message));

        public override bool Confirm(string title, string message)
        {
            Confirms.Add((title, message));
            return ConfirmResult;
        }
    }

    /// <summary>Runs everything inline on the calling thread; no real dispatcher needed.</summary>
    private sealed class SynchronousUiDispatcher : IUiDispatcher
    {
        public void Invoke(Action action) => action();
        public void Post(Action action) => action();
        public void Post(Action action, UiDispatcherPriority priority) => action();
        public bool CheckAccess() => true;
    }

    /// <summary>Brute-force service that never runs — Start should bail out during validation.</summary>
    private sealed class FakeBruteForceService : IBruteForceService
    {
        public event EventHandler<BruteForceProgressEventArgs>? Progress { add { } remove { } }
        public event EventHandler<BruteForceStatusChangedEventArgs>? StatusChanged { add { } remove { } }
        public event EventHandler<LogEventArgs>? LogMessage { add { } remove { } }
        public event EventHandler<FileCopyProgressEventArgs>? FileCopyProgress { add { } remove { } }
        public event EventHandler<CRCValidationProgressEventArgs>? CRCValidationProgress { add { } remove { } }
        public event EventHandler<TimestampPreservationFailedEventArgs>? TimestampPreservationFailed { add { } remove { } }

        public int RunCalls { get; private set; }

        public Task<BruteForceRunResult> RunAsync(BruteForceOptions options, CancellationToken cancellationToken = default)
        {
            RunCalls++;
            return Task.FromResult(new BruteForceRunResult(true, null));
        }
    }

    private static ReconstructorViewModel CreateVm(out RecordingFileDialogService dialog, out FakeBruteForceService brute)
    {
        dialog = new RecordingFileDialogService();
        brute = new FakeBruteForceService();
        return new ReconstructorViewModel(brute, dialog, new SynchronousUiDispatcher(), new TestUiTimerFactory(), settingsService: null);
    }

    private string NewTempDir()
    {
        string dir = Path.Combine(Path.GetTempPath(), $"rescene-recon-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        _tempDirs.Add(dir);
        return dir;
    }

    public void Dispose()
    {
        foreach (string d in _tempDirs)
        {
            try
            { Directory.Delete(d, recursive: true); }
            catch { /* best effort */ }
        }
    }

    /// <summary>
    /// Creates a WinRAR folder with a real version subfolder, points the VM at it, and AWAITS the
    /// version scan so it cannot race StartAsync's version guards. The scan is fire-and-forget in
    /// production (marshalled to the UI thread); in tests we await LastVersionScan for determinism.
    /// </summary>
    private async Task SetWinRARWithVersionAsync(ReconstructorViewModel vm)
    {
        string dir = NewTempDir();
        Directory.CreateDirectory(Path.Combine(dir, "winrar-500"));
        // The scanner looks for the platform's console binary (rar.exe on Windows, rar elsewhere),
        // so stub whichever name this OS resolves to — otherwise the folder scans as version-less
        // and every test here stops at the "no WinRAR versions" guard instead of its own branch.
        File.WriteAllText(Path.Combine(dir, "winrar-500", RarExecutable.FileName), "stub");
        vm.WinRARPath = dir;
        if (vm.LastVersionScan is { } scan)
        {
            await scan;
        }
    }

    // ── Tests ───────────────────────────────────────────────

    [Fact]
    public async Task Start_WithMissingWinRARPath_ShowsValidationError_AndDoesNotRun()
    {
        ReconstructorViewModel vm = CreateVm(out RecordingFileDialogService dialog, out FakeBruteForceService brute);
        // WinRARPath is blank -> first validation branch fires.
        vm.ReleasePath = NewTempDir();
        vm.OutputPath = NewTempDir();

        await vm.StartCommand.ExecuteAsync(null);

        Assert.Contains(("Validation Error", "Invalid WinRAR directory."), dialog.Errors);
        Assert.Equal(0, brute.RunCalls);
        Assert.False(vm.IsRunning);
    }

    [Fact]
    public async Task Start_WithNonExistentReleasePath_ShowsReleaseError()
    {
        ReconstructorViewModel vm = CreateVm(out RecordingFileDialogService dialog, out FakeBruteForceService brute);
        await SetWinRARWithVersionAsync(vm);
        vm.ReleasePath = Path.Combine(NewTempDir(), "does", "not", "exist");
        vm.OutputPath = NewTempDir();

        await vm.StartCommand.ExecuteAsync(null);

        Assert.Contains(("Validation Error", "Release directory does not exist."), dialog.Errors);
        Assert.Equal(0, brute.RunCalls);
    }

    [Fact]
    public async Task Start_WithMissingVerificationFile_ShowsVerificationError()
    {
        ReconstructorViewModel vm = CreateVm(out RecordingFileDialogService dialog, out FakeBruteForceService brute);
        await SetWinRARWithVersionAsync(vm);
        vm.ReleasePath = NewTempDir();   // empty dir -> no subdir warning, no missing-input warning
        vm.OutputPath = NewTempDir();
        // VerificationPath left blank -> verification validation branch fires.

        await vm.StartCommand.ExecuteAsync(null);

        Assert.Contains(("Validation Error", "Invalid verification file path."), dialog.Errors);
        Assert.Equal(0, brute.RunCalls);
    }

    [Fact]
    public async Task Start_WithSubdirectoriesAndNoTimestamps_AbortsWhenConfirmDeclined()
    {
        ReconstructorViewModel vm = CreateVm(out RecordingFileDialogService dialog, out FakeBruteForceService brute);
        await SetWinRARWithVersionAsync(vm);

        string release = NewTempDir();
        Directory.CreateDirectory(Path.Combine(release, "subdir"));   // triggers the modified-date warning
        vm.ReleasePath = release;
        vm.OutputPath = NewTempDir();

        dialog.ConfirmResult = false;   // user declines the subdirectory modified-date warning

        await vm.StartCommand.ExecuteAsync(null);

        // The confirm seam (ShowConfirmAsync) is what guards this branch; declining must abort.
        Assert.Single(dialog.Confirms);
        Assert.Equal(0, brute.RunCalls);
        Assert.False(vm.IsRunning);
    }

    [Fact]
    public async Task Start_WhenReleaseDirectoryCannotBeEnumerated_ShowsValidationError_AndDoesNotRun()
    {
        ReconstructorViewModel vm = CreateVm(out RecordingFileDialogService dialog, out FakeBruteForceService brute);
        await SetWinRARWithVersionAsync(vm);

        string release = NewTempDir();
        // Deny only "list directory" so Directory.Exists(release) still succeeds (the earlier existence
        // check must pass) while Directory.EnumerateDirectories(release) — the subdirectory-timestamp
        // preflight check (#18) — throws UnauthorizedAccessException instead of silently seeing "empty".
        DenyListing(release);
        try
        {
            vm.ReleasePath = release;
            vm.OutputPath = NewTempDir();

            await vm.StartCommand.ExecuteAsync(null);

            Assert.Contains(dialog.Errors, e => e.Title == "Validation Error");
            Assert.Equal(0, brute.RunCalls);
            Assert.False(vm.IsRunning);
        }
        finally
        {
            RestoreListing(release);
        }
    }

    /// <summary>
    /// Denies only "list directory contents" on <paramref name="path"/> itself — via <c>icacls</c>'s
    /// granular (RD) right on Windows, or clearing the read bit while keeping execute on Unix — so
    /// <see cref="Directory.Exists"/> still sees the folder while <see cref="Directory.EnumerateDirectories(string)"/>
    /// throws <see cref="UnauthorizedAccessException"/>. Callers must pair this with
    /// <see cref="RestoreListing"/> so cleanup (<see cref="Dispose"/>) can proceed.
    /// </summary>
    private static void DenyListing(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            RunIcacls(path, "/deny", $"{Environment.UserName}:(OI)(CI)(RD)");
        }
        else
        {
            File.SetUnixFileMode(path, UnixFileMode.UserExecute);
        }
    }

    private static void RestoreListing(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            RunIcacls(path, "/remove:d", Environment.UserName);
        }
        else
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    private static void RunIcacls(string path, params string[] args)
    {
        var psi = new ProcessStartInfo("icacls.exe")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        psi.ArgumentList.Add(path);
        foreach (string a in args)
        {
            psi.ArgumentList.Add(a);
        }

        using Process proc = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start 'icacls' — cannot set up the access-denied test fixture.");
        proc.WaitForExit();

        if (proc.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"icacls {path} {string.Join(' ', args)} failed (exit {proc.ExitCode}): {proc.StandardError.ReadToEnd()}");
        }
    }

    [Fact]
    public void SettingsChanged_FillsEmptyWinRARPath_WithoutRestart()
    {
        var settings = new FakeAppSettingsService(); // default settings: empty paths
        var brute = new FakeBruteForceService();
        var dialog = new RecordingFileDialogService();
        var vm = new ReconstructorViewModel(brute, dialog, new SynchronousUiDispatcher(), new TestUiTimerFactory(), settings);

        Assert.Equal(string.Empty, vm.WinRARPath); // nothing to fill at construction

        settings.Settings = new AppSettings { ReconstructWinRARPath = @"C:\winrar-versions" };
        settings.RaiseChanged();

        Assert.Equal(@"C:\winrar-versions", vm.WinRARPath);
    }

    [Fact]
    public void ReleaseEqualsOutput_TurnsBothStatusesRed_AndClears()
    {
        ReconstructorViewModel vm = CreateVm(out _, out _);
        string shared = NewTempDir();

        vm.ReleasePath = shared;
        vm.OutputPath = shared; // overlap

        Assert.Equal(FieldState.Error, vm.ReleaseStatus.State);
        Assert.Equal(FieldState.Error, vm.OutputStatus.State);

        vm.OutputPath = NewTempDir(); // separate folder clears the overlap

        Assert.NotEqual(FieldState.Error, vm.ReleaseStatus.State);
        Assert.NotEqual(FieldState.Error, vm.OutputStatus.State);
    }

    [Fact]
    public void CanStart_FalseWhenReleaseEqualsOutput_TrueWhenSeparate()
    {
        ReconstructorViewModel vm = CreateVm(out _, out _);
        string release = NewTempDir();

        vm.WinRARPath = NewTempDir();
        vm.ReleasePath = release;
        vm.OutputPath = release; // overlap

        Assert.False(vm.StartCommand.CanExecute(null));

        vm.OutputPath = NewTempDir(); // separate folder

        Assert.True(vm.StartCommand.CanExecute(null));
    }

    [Fact]
    public void PathsReadyToStart_FalseOnOverlap_TrueWhenSeparate()
    {
        ReconstructorViewModel vm = CreateVm(out _, out _);
        string release = NewTempDir();
        vm.WinRARPath = NewTempDir();
        vm.ReleasePath = release;
        vm.OutputPath = release; // overlap
        Assert.False(vm.PathsReadyToStart);

        vm.OutputPath = NewTempDir(); // separate
        Assert.True(vm.PathsReadyToStart);
    }

    [Fact]
    public void PathsReadyToStart_FalseWhenAPathMissing()
    {
        ReconstructorViewModel vm = CreateVm(out _, out _);
        vm.WinRARPath = NewTempDir();
        vm.ReleasePath = NewTempDir();
        // OutputPath left empty
        Assert.False(vm.PathsReadyToStart);
    }

    [Fact]
    public async Task Start_ReleaseEqualsOutput_BlocksAndDoesNotDelete()
    {
        ReconstructorViewModel vm = CreateVm(out RecordingFileDialogService dialog, out FakeBruteForceService brute);

        string shared = NewTempDir();
        string releaseFile = Path.Combine(shared, "movie.mkv");
        File.WriteAllText(releaseFile, "release contents");

        await SetWinRARWithVersionAsync(vm);
        vm.ReleasePath = shared;
        vm.OutputPath = shared; // same folder as release

        await vm.StartCommand.ExecuteAsync(null);

        Assert.Contains(dialog.Errors, e => e.Message.Contains("must be different from the Release folder", StringComparison.Ordinal));
        Assert.Equal(0, brute.RunCalls);          // never started the run
        Assert.True(File.Exists(releaseFile));     // release contents NOT deleted
    }

    [Fact]
    public void UncheckingStopOnFirstMatch_ClearsRename()
    {
        ReconstructorViewModel vm = CreateVm(out _, out _);
        vm.StopOnFirstMatch = true;
        vm.RenameToReleaseNames = true;

        vm.StopOnFirstMatch = false;

        Assert.False(vm.RenameToReleaseNames);
    }

    [Fact]
    public void StopOnFirstMatch_DrivesRenameEnabled()
    {
        ReconstructorViewModel vm = CreateVm(out _, out _);
        vm.StopOnFirstMatch = true;
        Assert.True(vm.IsRenameEnabled);

        vm.StopOnFirstMatch = false;
        Assert.False(vm.IsRenameEnabled);
    }

    [Fact]
    public void UncheckingRename_DoesNotChangeStopOnFirstMatch()
    {
        ReconstructorViewModel vm = CreateVm(out _, out _);
        vm.StopOnFirstMatch = true;
        vm.RenameToReleaseNames = true;

        vm.RenameToReleaseNames = false;

        Assert.True(vm.StopOnFirstMatch); // unchanged
    }
}
