using ReScene.App.Core.Services;
using ReScene.App.Core.ViewModels;
using ReScene.SRR;
using ReScene.SRS;
namespace ReScene.App.Core.Tests;

/// <summary>
/// Tests the CreatorViewModel behaviors the Create-an-SRR wizard relies on: the auto-scan that
/// fills the stored-files list as soon as a release is chosen, build-success gating, stored-name
/// computation, and Reset. The full creation pipeline is faked; only orchestration is exercised.
/// </summary>
public sealed class CreatorViewModelTests : IDisposable
{
    private readonly List<string> _tempPaths = [];

    // ── Fakes ───────────────────────────────────────────────

    private sealed class FakeSRRCreationService : ISRRCreationService
    {
        /// <summary>
        /// A REAL event, not the discarding accessor the other doubles use: the view-model
        /// subscribes to this in its constructor, and the cross-instance isolation test needs that
        /// subscription to be observable. Nothing raises it unless a test calls
        /// <see cref="RaiseProgress"/>, so existing tests are unaffected.
        /// </summary>
        public event EventHandler<SRRCreationProgressEventArgs>? Progress;

        /// <summary>Raises <see cref="Progress"/> with a caller-chosen, recognisable payload.</summary>
        public void RaiseProgress(int percent, string message) =>
            Progress?.Invoke(this, new SRRCreationProgressEventArgs { ProgressPercent = percent, Message = message });

        public bool Succeed { get; set; } = true;
        public int Calls { get; private set; }
        public string? LastOutputPath { get; private set; }

        public IReadOnlyList<StoredFileEntry>? LastStoredFiles { get; private set; }

        public Task<SRRCreationResult> CreateFromRARAsync(string outputPath, IReadOnlyList<string> rarVolumePaths,
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

        public IReadOnlyList<string>? LastInputFiles { get; private set; }
        public string? LastRootFolder { get; private set; }
        public bool? LastStoreRelativePaths { get; private set; }

        public Task<SRRCreationResult> CreateFromInputsAsync(string outputPath, IReadOnlyList<string> inputFiles,
            string? rootFolder, bool storeRelativePaths, IReadOnlyList<StoredFileEntry>? additionalFiles,
            SRRCreationOptions options, CancellationToken ct)
        {
            LastInputFiles = inputFiles;
            LastRootFolder = rootFolder;
            LastStoreRelativePaths = storeRelativePaths;
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

    private sealed class FakeSRSCreationService : ISRSCreationService
    {
        public event EventHandler<SRSCreationProgressEventArgs>? Progress { add { } remove { } }
        public event EventHandler<SRSScanProgressEventArgs>? ScanProgress { add { } remove { } }

        // Succeeds without touching disk; the SRS phase only runs when a test opts in via AutoCreateSRS.
        public Task<SRSCreationResult> CreateAsync(string outputPath, string sampleFilePath, SRSCreationOptions options, CancellationToken ct)
            => Task.FromResult(new SRSCreationResult { Success = true, SRSFileSize = 1 });
    }

    // None of this file's tests exercise folder mode (InputPath is always a file) — an empty-result
    // stub is enough to satisfy the constructor. Folder-mode scan behavior is covered in
    // CreatorViewModelFolderModeTests.cs.
    private sealed class StubReleaseScanner : IReleaseScanner
    {
        public ReleaseScanResult Scan(string releaseRoot, CancellationToken ct = default) => new([], [], [], [], [], []);
    }

    private sealed class FakeTempDirectoryService(List<string> createdSink) : NoOpTempDirectoryService
    {
        public override string CreateTempDirectory()
        {
            string dir = Directory.CreateTempSubdirectory("rescene-creator-test-").FullName;
            createdSink.Add(dir);   // tracked so the test fixture can clean it up
            return dir;
        }

        public override void Cleanup(string? tempDir)
        {
            if (!string.IsNullOrEmpty(tempDir) && Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }

    private sealed class FakeFileDialogService : NoOpFileDialogService
    {
        public string? PromptResult { get; set; }
        public Queue<string?> PromptResults { get; } = new();   // consumed first, for re-prompt loops
        public IReadOnlyList<string> OpenFilesResult { get; set; } = [];

        public override Task<IReadOnlyList<string>> OpenFilesAsync(string title, IReadOnlyList<string> filters, string? initialPath = null) => Task.FromResult(OpenFilesResult);
        public override Task<bool> ShowConfirmAsync(string title, string message) => Task.FromResult(true);
        public override Task<string?> PromptForTextAsync(string title, string message, string initialValue)
            => Task.FromResult(PromptResults.Count > 0 ? PromptResults.Dequeue() : PromptResult);
        public override bool Confirm(string title, string message) => true;
    }

    // ── Helpers ─────────────────────────────────────────────

    private FakeFileDialogService _dialog = new();

    private CreatorViewModel CreateVm(out FakeSRRCreationService srr, bool autoInclude = false)
    {
        srr = new FakeSRRCreationService();
        _dialog = new FakeFileDialogService();
        var vm = new CreatorViewModel(srr, new FakeSRSCreationService(), _dialog,
            new FakeTempDirectoryService(_tempPaths), new NoOpAppSettingsService(), new TestUiDispatcher(), new StubReleaseScanner())
        {
            // Keep the build trivial and deterministic: no sample/vobsub/fix phases.
            AutoCreateSRS = false,
            CreateVobsubSRR = false,
            StoreFixRAR = false,
            AutoIncludeFiles = autoInclude,
        };
        return vm;
    }

    /// <summary>
    /// Builds an absolute path under a fixed root, using this OS's separator. Stored-name
    /// computation never touches disk, so these files need not exist — but they must be shaped
    /// like real paths: a Windows-style literal is one separator-less file name on POSIX, which
    /// would make the whole string the stored name.
    /// </summary>
    private static string FakePath(params string[] segments) =>
        Path.Combine([Path.Combine(Path.GetTempPath(), "creator-vm-tests"), .. segments]);

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
        CreatorViewModel vm = CreateVm(out FakeSRRCreationService srr);
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
        CreatorViewModel vm = CreateVm(out FakeSRRCreationService srr);
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
        vm.AddStoredFiles([FakePath("a", "dup.nfo"), FakePath("b", "dup.nfo")]);
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
        CreatorViewModel vm = CreateVm(out FakeSRRCreationService srr);
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
        CreatorViewModel vm = CreateVm(out FakeSRRCreationService srr);
        vm.InputPath = Path.Combine(dir, "movie.sfv");
        vm.ExtraSampleFiles.Add(Path.Combine(dir, "Sample", "movie-sample.mkv"));

        vm.BuildSampleAndSubtitlePlaceholders();

        CreatorViewModel.StoredFileItem placeholder = Assert.Single(vm.StoredFiles, f => f.Kind == CreatorViewModel.StoredFileKind.GeneratedSRS);
        Assert.Equal("Sample/movie-sample.srs", placeholder.StoredName);
        Assert.Equal(string.Empty, placeholder.FullPath);    // nothing generated yet
        Assert.Equal(0, srr.Calls);                          // no creation happened
    }

    [Fact]
    public void InputPathChange_WithSamplePlaceholder_DoesNotThrow_AndRecomputesRealItems()
    {
        string dirA = CreateTempRelease("movie.sfv");
        // A real stored file living in a subfolder of the SECOND release, so switching the input to
        // it must recompute the stored name to the release-relative "subs/real.nfo".
        string dirB = CreateTempRelease("other.sfv", Path.Combine("subs", "real.nfo"));
        string realFile = Path.Combine(dirB, "subs", "real.nfo");

        CreatorViewModel vm = CreateVm(out _);
        vm.InputPath = Path.Combine(dirA, "movie.sfv");
        vm.AddStoredFiles([realFile]);

        // A wizard sample placeholder has an empty FullPath (Kind = GeneratedSRS).
        vm.ExtraSampleFiles.Add(Path.Combine(dirA, "Sample", "movie-sample.mkv"));
        vm.BuildSampleAndSubtitlePlaceholders();
        CreatorViewModel.StoredFileItem placeholder = Assert.Single(vm.StoredFiles, f => f.Kind == CreatorViewModel.StoredFileKind.GeneratedSRS);
        Assert.Equal(string.Empty, placeholder.FullPath);

        // Changing the input path re-runs UpdateStoredNames over EVERY stored item, including the
        // empty-FullPath placeholder. Before the fix, Path.GetRelativePath(releaseDir, "") threw
        // ArgumentException here and aborted the rest of OnInputPathChanged.
        Exception? ex = Record.Exception(() => vm.InputPath = Path.Combine(dirB, "other.sfv"));

        Assert.Null(ex);
        // The real item is recomputed against the new release dir; the placeholder is left untouched.
        CreatorViewModel.StoredFileItem real = vm.StoredFiles.Single(f => f.Kind == CreatorViewModel.StoredFileKind.Regular);
        Assert.Equal("subs/real.nfo", real.StoredName);
        Assert.Equal(string.Empty, placeholder.FullPath);
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
        CreatorViewModel.StoredFileItem first = vm.StoredFiles.First(f => f.Kind != CreatorViewModel.StoredFileKind.Regular);

        vm.BuildSampleAndSubtitlePlaceholders();   // same sources → keep existing rows

        Assert.Same(first, vm.StoredFiles.First(f => f.Kind != CreatorViewModel.StoredFileKind.Regular));
    }

    [Fact]
    public async Task CreateSRR_MaterializesPlaceholders_InListOrder()
    {
        string dir = CreateTempRelease("movie.sfv");
        CreatorViewModel vm = CreateVm(out FakeSRRCreationService srr);
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
        Assert.Equal(["b.srs", "a.srs"], [.. srr.LastStoredFiles!.Select(e => e.StoredName)]);
        // Placeholders are non-destructive: they remain placeholders so a retry regenerates.
        Assert.Equal(2, vm.StoredFiles.Count(f => f.Kind == CreatorViewModel.StoredFileKind.GeneratedSRS));
    }

    [Fact]
    public async Task CreateSRR_RetryAfterFailure_RematerializesPlaceholders()
    {
        string dir = CreateTempRelease("movie.sfv");
        CreatorViewModel vm = CreateVm(out FakeSRRCreationService srr);
        vm.InputPath = Path.Combine(dir, "movie.sfv");
        vm.OutputPath = Path.Combine(dir, "movie.srr");
        vm.ExtraSampleFiles.Add(Path.Combine(dir, "a.mkv"));
        vm.BuildSampleAndSubtitlePlaceholders();

        srr.Succeed = false;
        vm.CreateSRRCommand.Execute(null);
        await vm.CreateSRRCommand.ExecutionTask!;
        Assert.False(vm.BuildSucceeded);
        // The placeholder survives the failed run (not turned into a dead temp-path entry).
        Assert.Contains(vm.StoredFiles, f => f.Kind == CreatorViewModel.StoredFileKind.GeneratedSRS);

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
        Assert.Equal(["CD1/sample.srs", "CD2/sample.srs"], [.. srs.Select(f => f.StoredName).Order()]);
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
        if (!OperatingSystem.IsWindows())
        {
            // "A different drive" is a Windows-only concept: POSIX has a single rooted tree, so
            // GetRelativePath can always express the relationship and never returns a rooted path.
            // The sibling test below covers the outside-the-release-folder branch on every platform.
            return;
        }

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
        vm.InputPath = FakePath("rel", "movie.sfv");
        vm.AddStoredFiles([FakePath("other", "extra.nfo")]);   // sibling of the release dir → "../other/..."

        Assert.Equal("extra.nfo", vm.StoredFiles[0].StoredName);
    }

    [Fact]
    public void AddStoredFiles_InputPathIsBareFileName_StoresFilenameOnly()
    {
        // The Input text box accepts a typed bare file name, which has no directory component at
        // all. GetDirectoryName returns "" (not null) for it, so an unguarded relativeTo would
        // throw ArgumentException straight out of AddStoredFiles.
        CreatorViewModel vm = CreateVm(out _);
        vm.InputPath = "movie.sfv";

        vm.AddStoredFiles([FakePath("other", "extra.nfo")]);

        Assert.Equal("extra.nfo", vm.StoredFiles[0].StoredName);
    }

    [Fact]
    public void BuildSampleAndSubtitlePlaceholders_InputPathIsBareFileName_DoesNotThrow()
    {
        // Sibling of the AddStoredFiles bare-name bug (same field, same GetDirectoryName-""
        // trap): the release scanners throw ArgumentException on an empty path, so an unguarded
        // releaseDir crashed the wizard's samples step for a typed bare file name. A bare input
        // means the current directory — the scan must run there instead of throwing.
        CreatorViewModel vm = CreateVm(out _);
        vm.InputPath = "movie.sfv";

        vm.BuildSampleAndSubtitlePlaceholders();
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
        vm.AddStoredFiles([FakePath("a.nfo"), FakePath("b.nfo")]);
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
        vm.AddStoredFiles([FakePath("a.nfo"), FakePath("b.nfo")]);
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
        vm.AddStoredFiles([FakePath("rel", "a.nfo")]);
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
        CreatorViewModel vm = CreateVm(out FakeSRRCreationService srr);
        vm.InputPath = Path.Combine(dir, "movie.sfv");
        vm.OutputPath = Path.Combine(dir, "movie.srr");
        vm.AddStoredFiles([Path.Combine(dir, "a.nfo"), Path.Combine(dir, "b.nfo"), Path.Combine(dir, "c.nfo")]);
        vm.SelectedStoredFile = vm.StoredFiles.First(f => f.StoredName == "c.nfo");
        vm.MoveStoredFileUpCommand.Execute(null);   // a, c, b

        vm.CreateSRRCommand.Execute(null);
        await vm.CreateSRRCommand.ExecutionTask!;

        Assert.NotNull(srr.LastStoredFiles);
        Assert.Equal(["a.nfo", "c.nfo", "b.nfo"], [.. srr.LastStoredFiles!.Select(e => e.StoredName)]);
    }

    [Fact]
    public void MoveStoredFile_ReordersList_AndStopsAtBounds()
    {
        CreatorViewModel vm = CreateVm(out _);
        vm.AddStoredFiles([FakePath("rel", "a.nfo"), FakePath("rel", "b.sfv")]);
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
        Assert.Equal("b.sfv", vm.SelectedStoredFile.StoredName);
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

    // ── Stored-file reordering ──────────────────────────────

    /// <summary>
    /// Move must be implemented as Remove+Insert, not ObservableCollection.Move: Avalonia's
    /// DataGridCollectionView drops Move notifications, so a Move reorders the list without the
    /// grid ever repainting. The remove clears the grid-bound selection, so the command must
    /// restore it — otherwise the second click of a double-move lands on a null selection.
    /// </summary>
    [Fact]
    public void MoveStoredFile_ReordersAndKeepsSelection()
    {
        CreatorViewModel vm = CreateVm(out _);
        vm.AddStoredFiles([FakePath("r", "a.nfo"), FakePath("r", "b.sfv"), FakePath("r", "c.jpg")]);

        vm.SelectedStoredFile = vm.StoredFiles[2]; // c.jpg
        vm.MoveStoredFileUpCommand.Execute(null);

        Assert.Equal(["a.nfo", "c.jpg", "b.sfv"], vm.StoredFiles.Select(f => f.StoredName));
        Assert.Same(vm.StoredFiles[1], vm.SelectedStoredFile);

        vm.MoveStoredFileUpCommand.Execute(null);

        Assert.Equal(["c.jpg", "a.nfo", "b.sfv"], vm.StoredFiles.Select(f => f.StoredName));
        Assert.Same(vm.StoredFiles[0], vm.SelectedStoredFile);

        // At the top: a further up-move is a no-op, selection intact.
        vm.MoveStoredFileUpCommand.Execute(null);
        Assert.Equal(["c.jpg", "a.nfo", "b.sfv"], vm.StoredFiles.Select(f => f.StoredName));
        Assert.Same(vm.StoredFiles[0], vm.SelectedStoredFile);

        vm.MoveStoredFileDownCommand.Execute(null);
        Assert.Equal(["a.nfo", "c.jpg", "b.sfv"], vm.StoredFiles.Select(f => f.StoredName));
        Assert.Same(vm.StoredFiles[1], vm.SelectedStoredFile);
    }

    // ── Cross-instance isolation ─────────────────────────────

    [Fact]
    public void TwoInstances_DoNotShareState_OrProgressStreams()
    {
        // MainWindowViewModel constructs TWO CreatorViewModels — the Advanced tab's and the Beginner
        // wizard's — and gives the wizard its OWN creation-service instances precisely so progress
        // never crosses. The constructor does `_sRRService.Progress += OnProgress` and never
        // unsubscribes, so an extraction that changes when or where that subscription happens can
        // route one instance's progress into the other's log. Nothing tested that they coexist.
        //
        // Scope: this pins CreatorViewModel's own isolation GIVEN distinct publishers. That the
        // composition root actually hands them distinct publishers is a separate risk, pinned by
        // CompositionRootTests.Wizard_And_AdvancedCreator_DoNotShareAProgressStream. Only the SRR
        // stream is covered here — the view-model subscribes to no SRS progress event.
        CreatorViewModel advanced = CreateVm(out FakeSRRCreationService advancedSrr);
        CreatorViewModel wizard = CreateVm(out FakeSRRCreationService wizardSrr);

        // Seed BOTH sides with recognisable state, so the untouched instance is asserted to have
        // PRESERVED its own values rather than merely to have kept type defaults.
        advanced.AddStoredFiles([FakePath("adv", "advanced-only.nfo")]);
        wizard.AddStoredFiles([FakePath("wiz", "wizard-a.nfo"), FakePath("wiz", "wizard-b.nfo")]);
        wizard.ProgressPercent = 7;

        // A snapshot is captured as arrays and compared FIELD BY FIELD below. Comparing the whole
        // tuple with one Assert.Equal would compare its string[] members BY REFERENCE: two
        // structurally identical snapshots would never be equal, so that assertion could only ever
        // fail — including on unmutated code, which makes it worthless as a regression guard.
        static (int Percent, string Message, string[] Log, string[] Stored) SnapshotOf(CreatorViewModel vm) =>
            (vm.ProgressPercent, vm.ProgressMessage, [.. vm.LogEntries], [.. vm.StoredFiles.Select(f => f.StoredName)]);

        static void AssertUnchanged((int Percent, string Message, string[] Log, string[] Stored) before, CreatorViewModel vm)
        {
            (int percent, string message, string[] log, string[] stored) = SnapshotOf(vm);
            Assert.Equal(before.Percent, percent);
            Assert.Equal(before.Message, message);
            Assert.Equal(before.Log, log);
            Assert.Equal(before.Stored, stored);
        }

        // ── Direction 1: the Advanced tab's service must reach ONLY the Advanced view-model.
        var wizardBefore = SnapshotOf(wizard);
        advancedSrr.RaiseProgress(41, "advanced-only progress line");

        Assert.Equal(41, advanced.ProgressPercent);
        Assert.Contains(advanced.LogEntries, l => l.Contains("advanced-only progress line", StringComparison.Ordinal));
        AssertUnchanged(wizardBefore, wizard);

        // ── Direction 2: and the wizard's service must reach ONLY the wizard. Asserted separately
        // because a shared router that always targets the first instance to publish would satisfy
        // direction 1 while failing here.
        var advancedBefore = SnapshotOf(advanced);
        wizardSrr.RaiseProgress(83, "wizard-only progress line");

        Assert.Equal(83, wizard.ProgressPercent);
        Assert.Contains(wizard.LogEntries, l => l.Contains("wizard-only progress line", StringComparison.Ordinal));
        AssertUnchanged(advancedBefore, advanced);

        // ── And the collections stayed each instance's own throughout.
        Assert.Equal(["advanced-only.nfo"], advanced.StoredFiles.Select(f => f.StoredName));
        Assert.Equal(["wizard-a.nfo", "wizard-b.nfo"], wizard.StoredFiles.Select(f => f.StoredName));
    }
}
