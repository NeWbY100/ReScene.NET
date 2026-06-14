using ReScene.NET.Models;
using ReScene.NET.Services;
using ReScene.NET.ViewModels;
using ReScene.SRR;
using ReScene.SRS;

namespace ReScene.NET.Tests;

/// <summary>
/// Tests the CreatorViewModel behaviors the Create-an-SRR wizard relies on: the auto-scan that
/// fills the stored-files list as soon as a release is chosen, build-success gating, stored-name
/// computation, and Reset. The full creation pipeline is faked; only orchestration is exercised.
/// </summary>
public sealed class CreatorViewModelTests : IDisposable
{
    private readonly List<string> _tempPaths = [];

    // ── Fakes ───────────────────────────────────────────────

    private sealed class FakeSrrCreationService : ISrrCreationService
    {
        public event EventHandler<SRRCreationProgressEventArgs>? Progress { add { } remove { } }

        public bool Succeed { get; set; } = true;
        public int Calls { get; private set; }
        public string? LastOutputPath { get; private set; }

        public IReadOnlyList<StoredFileEntry>? LastStoredFiles { get; private set; }

        public Task<SRRCreationResult> CreateFromRarAsync(string outputPath, IReadOnlyList<string> rarVolumePaths,
            IReadOnlyList<StoredFileEntry>? storedFiles, SRRCreationOptions options, CancellationToken ct)
        {
            LastStoredFiles = storedFiles;
            return Build(outputPath);
        }

        public Task<SRRCreationResult> CreateFromSFVAsync(string outputPath, string sfvFilePath,
            IReadOnlyList<StoredFileEntry>? additionalFiles, SRRCreationOptions options, CancellationToken ct)
        {
            LastStoredFiles = additionalFiles;
            return Build(outputPath);
        }

        private Task<SRRCreationResult> Build(string outputPath)
        {
            Calls++;
            LastOutputPath = outputPath;
            return Task.FromResult(new SRRCreationResult
            {
                Success = Succeed,
                ErrorMessage = Succeed ? null : "boom",
            });
        }
    }

    private sealed class FakeSrsCreationService : ISrsCreationService
    {
        public event EventHandler<SRSCreationProgressEventArgs>? Progress { add { } remove { } }
        public event EventHandler<SRSScanProgressEventArgs>? ScanProgress { add { } remove { } }

        // Succeeds without touching disk; the SRS phase only runs when a test opts in via AutoCreateSRS.
        public Task<SRSCreationResult> CreateAsync(string outputPath, string sampleFilePath, SRSCreationOptions options, CancellationToken ct)
            => Task.FromResult(new SRSCreationResult { Success = true, SRSFileSize = 1 });
    }

    private sealed class FakeAppSettingsService : IAppSettingsService
    {
        public event EventHandler? Changed { add { } remove { } }
        public AppSettings Load() => new();
        public void Save(AppSettings settings) { }
    }

    private sealed class FakeTempDirectoryService(List<string> createdSink) : ITempDirectoryService
    {
        public string CreateTempDirectory()
        {
            string dir = Directory.CreateTempSubdirectory("rescene-creator-test-").FullName;
            createdSink.Add(dir);   // tracked so the test fixture can clean it up
            return dir;
        }

        public void Cleanup(string? tempDir)
        {
            if (!string.IsNullOrEmpty(tempDir) && Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }

    private sealed class FakeFileDialogService : IFileDialogService
    {
        public string? PromptResult { get; set; }
        public Queue<string?> PromptResults { get; } = new();   // consumed first, for re-prompt loops
        public IReadOnlyList<string> OpenFilesResult { get; set; } = [];

        public Task<string?> OpenFileAsync(string title, IReadOnlyList<string> filters) => Task.FromResult<string?>(null);
        public Task<IReadOnlyList<string>> OpenFilesAsync(string title, IReadOnlyList<string> filters) => Task.FromResult(OpenFilesResult);
        public Task<string?> SaveFileAsync(string title, string defaultExtension, IReadOnlyList<string> filters, string? defaultFileName = null) => Task.FromResult<string?>(null);
        public Task<string?> OpenFolderAsync(string title) => Task.FromResult<string?>(null);
        public Task<bool> ShowConfirmAsync(string title, string message) => Task.FromResult(true);
        public Task<string?> PromptForTextAsync(string title, string message, string initialValue)
            => Task.FromResult(PromptResults.Count > 0 ? PromptResults.Dequeue() : PromptResult);
    }

    // ── Helpers ─────────────────────────────────────────────

    private FakeFileDialogService _dialog = new();

    private CreatorViewModel CreateVm(out FakeSrrCreationService srr, bool autoInclude = false)
    {
        srr = new FakeSrrCreationService();
        _dialog = new FakeFileDialogService();
        var vm = new CreatorViewModel(srr, new FakeSrsCreationService(), _dialog,
            new FakeTempDirectoryService(_tempPaths), new FakeAppSettingsService())
        {
            // Keep the build trivial and deterministic: no sample/vobsub/fix phases.
            AutoCreateSRS = false,
            CreateVobsubSRR = false,
            StoreFixRar = false,
            AutoIncludeFiles = autoInclude,
        };
        return vm;
    }

    /// <summary>Creates a temp release directory containing the given (empty) files and returns its path.</summary>
    private string CreateTempRelease(params string[] fileNames)
    {
        string dir = Directory.CreateTempSubdirectory("rescene-release-test-").FullName;
        _tempPaths.Add(dir);

        foreach (string name in fileNames)
        {
            string path = Path.Combine(dir, name);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, name.EndsWith(".sfv", StringComparison.OrdinalIgnoreCase)
                ? "movie.rar 00000000\n"
                : string.Empty);
        }

        return dir;
    }

    public void Dispose()
    {
        foreach (string p in _tempPaths)
        {
            try
            {
                if (Directory.Exists(p))
                {
                    Directory.Delete(p, recursive: true);
                }
                else if (File.Exists(p))
                {
                    File.Delete(p);
                }
            }
            catch { /* best effort */ }
        }
    }

    // ── Auto-include scan ───────────────────────────────────

    [Fact]
    public void InputPath_WithAutoInclude_PopulatesStoredFilesFromReleaseDirectory()
    {
        // Note: "movie.nfo" would be skipped — the scanner blacklists media-center junk names.
        string dir = CreateTempRelease("movie.sfv", "release-group.nfo");
        CreatorViewModel vm = CreateVm(out _, autoInclude: true);

        vm.InputPath = Path.Combine(dir, "movie.sfv");

        Assert.Contains(vm.StoredFiles, f => f.StoredName.Equals("release-group.nfo", StringComparison.OrdinalIgnoreCase));
    }

    // ── Build-success gating ────────────────────────────────

    [Fact]
    public async Task CreateSRR_Success_SetsBuildSucceeded_AndConsumesSuppressFlag()
    {
        string dir = CreateTempRelease("movie.sfv");
        CreatorViewModel vm = CreateVm(out FakeSrrCreationService srr);
        vm.InputPath = Path.Combine(dir, "movie.sfv");
        vm.OutputPath = Path.Combine(dir, "movie.srr");
        vm.SuppressOverwriteConfirm = true;

        vm.CreateSRRCommand.Execute(null);
        await vm.CreateSRRCommand.ExecutionTask!;

        Assert.True(vm.BuildSucceeded);
        Assert.False(vm.SuppressOverwriteConfirm);   // one-shot, consumed by the run
        Assert.Equal(1, srr.Calls);
        Assert.Equal(vm.OutputPath, srr.LastOutputPath);
    }

    [Fact]
    public async Task CreateSRR_Failure_LeavesBuildSucceededFalse()
    {
        string dir = CreateTempRelease("movie.sfv");
        CreatorViewModel vm = CreateVm(out FakeSrrCreationService srr);
        srr.Succeed = false;
        vm.InputPath = Path.Combine(dir, "movie.sfv");
        vm.OutputPath = Path.Combine(dir, "movie.srr");

        vm.CreateSRRCommand.Execute(null);
        await vm.CreateSRRCommand.ExecutionTask!;

        Assert.False(vm.BuildSucceeded);
    }

    // ── Stored-name collisions ──────────────────────────────

    [Fact]
    public async Task CreateSRR_CollidingStoredNames_LogsWarning()
    {
        string dir = CreateTempRelease("movie.sfv");
        CreatorViewModel vm = CreateVm(out _);
        vm.InputPath = Path.Combine(dir, "movie.sfv");
        vm.OutputPath = Path.Combine(dir, "movie.srr");
        // Two distinct files from different folders, both outside the release → both resolve to
        // the bare filename "dup.nfo", so they collide on the stored name.
        vm.AddStoredFiles([@"X:\a\dup.nfo", @"Y:\b\dup.nfo"]);
        Assert.Equal(2, vm.StoredFiles.Count);
        Assert.All(vm.StoredFiles, f => Assert.Equal("dup.nfo", f.StoredName));

        vm.CreateSRRCommand.Execute(null);
        await vm.CreateSRRCommand.ExecutionTask!;

        Assert.Contains(vm.LogEntries, e => e.Contains("Two stored files use the name", StringComparison.Ordinal));
    }

    [Fact]
    public async Task CreateSRR_BackslashAndSlashName_TreatedAsOneEntry()
    {
        string dir = CreateTempRelease("movie.sfv");
        CreatorViewModel vm = CreateVm(out FakeSrrCreationService srr);
        vm.InputPath = Path.Combine(dir, "movie.sfv");
        vm.OutputPath = Path.Combine(dir, "movie.srr");
        vm.AddStoredFiles([@"X:\a\one.idx", @"Y:\b\two.idx"]);
        // Simulate the editable grid: one row with a backslash, one with a forward slash.
        vm.StoredFiles[0].StoredName = @"subs\dup.idx";
        vm.StoredFiles[1].StoredName = "subs/dup.idx";

        vm.CreateSRRCommand.Execute(null);
        await vm.CreateSRRCommand.ExecutionTask!;

        // Both normalize to the writer's key space, so the app collapses them and warns — the lib
        // never has to silently drop one.
        Assert.NotNull(srr.LastStoredFiles);
        Assert.Single(srr.LastStoredFiles!);
        Assert.Equal("subs/dup.idx", srr.LastStoredFiles![0].StoredName);
        Assert.Contains(vm.LogEntries, e => e.Contains("Two stored files use the name", StringComparison.Ordinal));
    }

    // ── Sample/subtitle placeholders (wizard samples step) ──

    [Fact]
    public void BuildPlaceholders_AddsPlaceholderRowsWithoutGenerating()
    {
        string dir = CreateTempRelease("movie.sfv");
        CreatorViewModel vm = CreateVm(out FakeSrrCreationService srr);
        vm.InputPath = Path.Combine(dir, "movie.sfv");
        vm.ExtraSampleFiles.Add(Path.Combine(dir, "Sample", "movie-sample.mkv"));

        vm.BuildSampleAndSubtitlePlaceholders();

        var placeholder = Assert.Single(vm.StoredFiles, f => f.Kind == CreatorViewModel.StoredFileKind.GeneratedSrs);
        Assert.Equal("Sample/movie-sample.srs", placeholder.StoredName);
        Assert.Equal(string.Empty, placeholder.FullPath);    // nothing generated yet
        Assert.Equal(0, srr.Calls);                          // no creation happened
    }

    [Fact]
    public void BuildPlaceholders_UnchangedSources_PreservesExistingRowsAndOrder()
    {
        string dir = CreateTempRelease("movie.sfv");
        CreatorViewModel vm = CreateVm(out _);
        vm.InputPath = Path.Combine(dir, "movie.sfv");
        vm.ExtraSampleFiles.Add(Path.Combine(dir, "a.mkv"));
        vm.ExtraSampleFiles.Add(Path.Combine(dir, "b.mkv"));
        vm.BuildSampleAndSubtitlePlaceholders();
        var first = vm.StoredFiles.First(f => f.Kind != CreatorViewModel.StoredFileKind.Regular);

        vm.BuildSampleAndSubtitlePlaceholders();   // same sources → keep existing rows

        Assert.Same(first, vm.StoredFiles.First(f => f.Kind != CreatorViewModel.StoredFileKind.Regular));
    }

    [Fact]
    public async Task CreateSRR_MaterializesPlaceholders_InListOrder()
    {
        string dir = CreateTempRelease("movie.sfv");
        CreatorViewModel vm = CreateVm(out FakeSrrCreationService srr);
        vm.InputPath = Path.Combine(dir, "movie.sfv");
        vm.OutputPath = Path.Combine(dir, "movie.srr");
        vm.ExtraSampleFiles.Add(Path.Combine(dir, "a.mkv"));
        vm.ExtraSampleFiles.Add(Path.Combine(dir, "b.mkv"));
        vm.BuildSampleAndSubtitlePlaceholders();

        // Reorder the placeholders: move b above a.
        vm.SelectedStoredFile = vm.StoredFiles.First(f => f.StoredName == "b.srs");
        vm.MoveStoredFileUpCommand.Execute(null);

        vm.CreateSRRCommand.Execute(null);
        await vm.CreateSRRCommand.ExecutionTask!;

        Assert.NotNull(srr.LastStoredFiles);
        Assert.Equal(["b.srs", "a.srs"], srr.LastStoredFiles!.Select(e => e.StoredName).ToArray());
        // Placeholders are non-destructive: they remain placeholders so a retry regenerates.
        Assert.Equal(2, vm.StoredFiles.Count(f => f.Kind == CreatorViewModel.StoredFileKind.GeneratedSrs));
    }

    [Fact]
    public async Task CreateSRR_RetryAfterFailure_RematerializesPlaceholders()
    {
        string dir = CreateTempRelease("movie.sfv");
        CreatorViewModel vm = CreateVm(out FakeSrrCreationService srr);
        vm.InputPath = Path.Combine(dir, "movie.sfv");
        vm.OutputPath = Path.Combine(dir, "movie.srr");
        vm.ExtraSampleFiles.Add(Path.Combine(dir, "a.mkv"));
        vm.BuildSampleAndSubtitlePlaceholders();

        srr.Succeed = false;
        vm.CreateSRRCommand.Execute(null);
        await vm.CreateSRRCommand.ExecutionTask!;
        Assert.False(vm.BuildSucceeded);
        // The placeholder survives the failed run (not turned into a dead temp-path entry).
        Assert.Contains(vm.StoredFiles, f => f.Kind == CreatorViewModel.StoredFileKind.GeneratedSrs);

        srr.Succeed = true;
        vm.CreateSRRCommand.Execute(null);
        await vm.CreateSRRCommand.ExecutionTask!;

        Assert.True(vm.BuildSucceeded);
        Assert.Contains(srr.LastStoredFiles!, e => e.StoredName == "a.srs");   // regenerated on retry
    }

    [Fact]
    public async Task CreateSRR_TwoSamplesSameBasename_GenerateDistinctTempFiles()
    {
        // Multi-disc style: two samples sharing a filename but in different release subfolders get
        // distinct stored names — their generated .srs must not overwrite each other on disk.
        string dir = CreateTempRelease("movie.sfv");
        CreatorViewModel vm = CreateVm(out _);
        vm.AutoCreateSRS = true;
        vm.InputPath = Path.Combine(dir, "movie.sfv");
        vm.OutputPath = Path.Combine(dir, "movie.srr");
        vm.ExtraSampleFiles.Add(Path.Combine(dir, "CD1", "sample.mkv"));
        vm.ExtraSampleFiles.Add(Path.Combine(dir, "CD2", "sample.mkv"));

        vm.CreateSRRCommand.Execute(null);
        await vm.CreateSRRCommand.ExecutionTask!;

        var srs = vm.StoredFiles
            .Where(f => f.StoredName.EndsWith(".srs", StringComparison.OrdinalIgnoreCase))
            .ToList();
        Assert.Equal(["CD1/sample.srs", "CD2/sample.srs"], srs.Select(f => f.StoredName).Order().ToArray());
        // Distinct temp paths — without the index prefix both would be <temp>\sample.srs.
        Assert.Equal(2, srs.Select(f => f.FullPath).Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    // ── Stored names ────────────────────────────────────────

    [Fact]
    public void AddStoredFiles_UsesReleaseRelativeNames_AndSkipsDuplicates()
    {
        string dir = CreateTempRelease("movie.sfv", @"Subs\subs.idx");
        CreatorViewModel vm = CreateVm(out _);
        vm.InputPath = Path.Combine(dir, "movie.sfv");
        string subsPath = Path.Combine(dir, "Subs", "subs.idx");

        vm.AddStoredFiles([subsPath, subsPath]);

        Assert.Single(vm.StoredFiles);
        Assert.Equal("Subs/subs.idx", vm.StoredFiles[0].StoredName);
    }

    [Fact]
    public void AddStoredFiles_OnDifferentDrive_StoresFilenameOnly()
    {
        CreatorViewModel vm = CreateVm(out _);
        vm.InputPath = @"D:\rel\movie.sfv";              // release on D:
        vm.AddStoredFiles([@"Z:\elsewhere\extra.nfo"]);  // file on a different drive

        // GetRelativePath returns the rooted Z:\ path; it must not leak as the stored name.
        Assert.Single(vm.StoredFiles);
        Assert.Equal("extra.nfo", vm.StoredFiles[0].StoredName);
    }

    [Fact]
    public void AddStoredFiles_OutsideReleaseFolder_StoresFilenameOnly()
    {
        CreatorViewModel vm = CreateVm(out _);
        vm.InputPath = @"D:\rel\movie.sfv";
        vm.AddStoredFiles([@"D:\other\extra.nfo"]);   // sibling of the release dir → "..\other\..."

        Assert.Equal("extra.nfo", vm.StoredFiles[0].StoredName);
    }

    // ── Rename ──────────────────────────────────────────────

    [Fact]
    public async Task RenameStoredFile_UpdatesStoredName()
    {
        CreatorViewModel vm = CreateVm(out _);
        vm.AddStoredFiles([@"X:\rel\a.nfo"]);
        vm.SelectedStoredFile = vm.StoredFiles[0];
        _dialog.PromptResult = @"renamed\a.nfo";   // backslashes normalized to forward slashes

        await vm.RenameStoredFileCommand.ExecuteAsync(null);

        Assert.Equal("renamed/a.nfo", vm.StoredFiles[0].StoredName);
    }

    [Fact]
    public async Task RenameStoredFile_DuplicateName_RepromptsUntilUnique()
    {
        CreatorViewModel vm = CreateVm(out _);
        vm.AddStoredFiles([@"X:\a.nfo", @"X:\b.nfo"]);
        vm.SelectedStoredFile = vm.StoredFiles[1];   // rename b.nfo
        _dialog.PromptResults.Enqueue("a.nfo");      // collides → re-prompt
        _dialog.PromptResults.Enqueue("c.nfo");      // unique → applied

        await vm.RenameStoredFileCommand.ExecuteAsync(null);

        Assert.Equal(["a.nfo", "c.nfo"], vm.StoredFiles.Select(f => f.StoredName));
    }

    [Fact]
    public async Task RenameStoredFile_DuplicateThenCancel_KeepsOriginal()
    {
        CreatorViewModel vm = CreateVm(out _);
        vm.AddStoredFiles([@"X:\a.nfo", @"X:\b.nfo"]);
        vm.SelectedStoredFile = vm.StoredFiles[1];
        _dialog.PromptResults.Enqueue("a.nfo");      // collides → re-prompt
        _dialog.PromptResults.Enqueue(null);         // cancel → keep original

        await vm.RenameStoredFileCommand.ExecuteAsync(null);

        Assert.Equal("b.nfo", vm.StoredFiles[1].StoredName);
    }

    [Fact]
    public void IsStoredNameTaken_MatchesAcrossSlashStyles_ExcludingSelf()
    {
        CreatorViewModel vm = CreateVm(out _);
        vm.AddStoredFiles([@"X:\one.idx"]);
        vm.StoredFiles[0].StoredName = "subs/x.idx";

        Assert.True(vm.IsStoredNameTaken(@"subs\x.idx", except: null));        // backslash form collides
        Assert.False(vm.IsStoredNameTaken("subs/x.idx", except: vm.StoredFiles[0])); // self excluded
        Assert.False(vm.IsStoredNameTaken("subs/other.idx", except: null));
    }

    [Fact]
    public async Task RenameStoredFile_BlankInput_LeavesNameUnchanged()
    {
        CreatorViewModel vm = CreateVm(out _);
        vm.AddStoredFiles([@"X:\rel\a.nfo"]);
        vm.SelectedStoredFile = vm.StoredFiles[0];
        _dialog.PromptResult = null;   // user cancelled

        await vm.RenameStoredFileCommand.ExecuteAsync(null);

        Assert.Equal("a.nfo", vm.StoredFiles[0].StoredName);
    }

    // ── Sample / subtitle extras ────────────────────────────

    [Fact]
    public async Task AddSample_AddsToExtras_SkippingDuplicates()
    {
        CreatorViewModel vm = CreateVm(out _);
        _dialog.OpenFilesResult = [@"X:\stuff\sample.mkv", @"X:\stuff\sample.mkv"];

        await vm.AddSampleCommand.ExecuteAsync(null);

        Assert.Equal([@"X:\stuff\sample.mkv"], vm.ExtraSampleFiles);
    }

    [Fact]
    public void RemoveSample_RemovesSelected()
    {
        CreatorViewModel vm = CreateVm(out _);
        vm.ExtraSampleFiles.Add(@"X:\a.mkv");
        vm.ExtraSampleFiles.Add(@"X:\b.mkv");
        vm.SelectedExtraSample = @"X:\a.mkv";

        vm.RemoveSampleCommand.Execute(null);

        Assert.Equal([@"X:\b.mkv"], vm.ExtraSampleFiles);
    }

    [Fact]
    public async Task AddSubtitle_AddsToExtras_SkippingDuplicates()
    {
        CreatorViewModel vm = CreateVm(out _);
        _dialog.OpenFilesResult = [@"X:\Subs\s.sfv", @"X:\Subs\s.sfv"];

        await vm.AddSubtitleCommand.ExecuteAsync(null);

        Assert.Equal([@"X:\Subs\s.sfv"], vm.ExtraSubtitleSfvFiles);
    }

    [Fact]
    public void Reset_ClearsSampleAndSubtitleExtras()
    {
        CreatorViewModel vm = CreateVm(out _);
        vm.ExtraSampleFiles.Add(@"X:\a.mkv");
        vm.ExtraSubtitleSfvFiles.Add(@"X:\Subs\s.sfv");

        vm.Reset();

        Assert.Empty(vm.ExtraSampleFiles);
        Assert.Empty(vm.ExtraSubtitleSfvFiles);
    }

    [Fact]
    public async Task CreateSRR_PassesStoredFilesToLibInCollectionOrder()
    {
        string dir = CreateTempRelease("movie.sfv", "a.nfo", "b.nfo", "c.nfo");
        CreatorViewModel vm = CreateVm(out FakeSrrCreationService srr);
        vm.InputPath = Path.Combine(dir, "movie.sfv");
        vm.OutputPath = Path.Combine(dir, "movie.srr");
        vm.AddStoredFiles([Path.Combine(dir, "a.nfo"), Path.Combine(dir, "b.nfo"), Path.Combine(dir, "c.nfo")]);
        vm.SelectedStoredFile = vm.StoredFiles.First(f => f.StoredName == "c.nfo");
        vm.MoveStoredFileUpCommand.Execute(null);   // a, c, b

        vm.CreateSRRCommand.Execute(null);
        await vm.CreateSRRCommand.ExecutionTask!;

        Assert.NotNull(srr.LastStoredFiles);
        Assert.Equal(["a.nfo", "c.nfo", "b.nfo"], srr.LastStoredFiles!.Select(e => e.StoredName).ToArray());
    }

    [Fact]
    public void MoveStoredFile_ReordersList_AndStopsAtBounds()
    {
        CreatorViewModel vm = CreateVm(out _);
        vm.AddStoredFiles([@"X:\rel\a.nfo", @"X:\rel\b.sfv"]);
        vm.SelectedStoredFile = vm.StoredFiles[1];

        vm.MoveStoredFileUpCommand.Execute(null);
        Assert.Equal(["b.sfv", "a.nfo"], vm.StoredFiles.Select(f => f.StoredName));

        vm.MoveStoredFileUpCommand.Execute(null);   // already first — no-op
        Assert.Equal(["b.sfv", "a.nfo"], vm.StoredFiles.Select(f => f.StoredName));

        vm.MoveStoredFileDownCommand.Execute(null);
        Assert.Equal(["a.nfo", "b.sfv"], vm.StoredFiles.Select(f => f.StoredName));

        vm.MoveStoredFileDownCommand.Execute(null); // already last — no-op
        Assert.Equal(["a.nfo", "b.sfv"], vm.StoredFiles.Select(f => f.StoredName));

        // Selection follows the moved item throughout.
        Assert.Equal("b.sfv", vm.SelectedStoredFile!.StoredName);
    }

    // ── Reset ───────────────────────────────────────────────

    [Fact]
    public void Reset_ClearsInputStoredFilesAndBuildState()
    {
        string dir = CreateTempRelease("movie.sfv", "movie.nfo");
        CreatorViewModel vm = CreateVm(out _, autoInclude: true);
        vm.InputPath = Path.Combine(dir, "movie.sfv");
        vm.BuildSucceeded = true;

        Assert.NotEmpty(vm.StoredFiles);

        vm.Reset();

        Assert.Equal(string.Empty, vm.InputPath);
        Assert.Empty(vm.StoredFiles);
        Assert.False(vm.BuildSucceeded);
        Assert.True(vm.AutoIncludeFiles);   // option defaults restored
    }
}
