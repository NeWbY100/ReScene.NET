using ReScene.App.Core.Services;
using ReScene.App.Core.ViewModels;
using ReScene.SRR;
using ReScene.SRS;

namespace ReScene.App.Core.Tests;

/// <summary>
/// Test matrix for folder mode's generated-artifact staging on <see cref="CreatorViewModel"/> (see
/// docs/superpowers/plans/2026-07-19-multiset-srr-creation.md) — extension-swap naming,
/// generate_srr's <c>same_srs_name</c> collision keying, pre-existing-.srs supersede, SRS-failure
/// .txt storage, multi-SRR subtitle append, a RAR-backed .vob sample's nested SRR, working-dir
/// cleanup on cancellation, and the proof-before-sfv reorder over the complete merged list.
/// <see cref="CreatorViewModelFolderModeTests"/> covers the surrounding scan/Create-call plumbing
/// this file assumes already works; this file only exercises the staging step (invoked via
/// <see cref="CreatorViewModel.CreateSRRCommand"/> when samples/subtitles exist).
/// </summary>
public sealed class CreatorViewModelArtifactTests : TempDirTestBase
{
    // ── Fakes (follow CreatorViewModelFolderModeTests.cs's patterns, extended with per-call
    // recording and per-path configurability so a single instance can drive a mixed scenario). ──

    private sealed class RecordingSRSCreationService : ISRSCreationService
    {
        public event EventHandler<SRSCreationProgressEventArgs>? Progress { add { } remove { } }
        public event EventHandler<SRSScanProgressEventArgs>? ScanProgress { add { } remove { } }

        public List<string> CallsInOrder { get; } = [];

        private readonly Dictionary<string, (bool Success, string? Error)> _perSample =
            new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Configures a specific sample's outcome; unconfigured samples default to success.</summary>
        public void Configure(string samplePath, bool success, string? error = null) =>
            _perSample[samplePath] = (success, error);

        /// <summary>Configures a specific sample to make the call itself throw (cancellation propagation test).</summary>
        public void ConfigureThrow(string samplePath, Exception exception) => _throws[samplePath] = exception;

        private readonly Dictionary<string, Exception> _throws = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Runs inside <see cref="CreateAsync"/>, before it returns — lets a test mutate view-model
        /// state DURING sample generation, which is how the phase-local snapshot timing of the
        /// LATER staging phases is pinned.
        /// </summary>
        public Action<string>? OnCreate
        {
            get; set;
        }

        public Task<SRSCreationResult> CreateAsync(string outputPath, string sampleFilePath, SRSCreationOptions options, CancellationToken ct)
        {
            CallsInOrder.Add(sampleFilePath);
            OnCreate?.Invoke(sampleFilePath);

            if (_throws.TryGetValue(sampleFilePath, out Exception? toThrow))
            {
                throw toThrow;
            }

            if (_perSample.TryGetValue(sampleFilePath, out (bool Success, string? Error) cfg))
            {
                if (!cfg.Success)
                {
                    return Task.FromResult(new SRSCreationResult { Success = false, ErrorMessage = cfg.Error });
                }
            }

            File.WriteAllBytes(outputPath, [1, 2, 3]);
            return Task.FromResult(new SRSCreationResult { Success = true, SRSFileSize = 3 });
        }
    }

    private sealed class RecordingSRRCreationService : ISRRCreationService
    {
        public event EventHandler<SRRCreationProgressEventArgs>? Progress { add { } remove { } }

        public IReadOnlyList<StoredFileEntry>? LastAdditionalFiles { get; private set; }
        public List<string> RarCalls { get; } = [];
        public List<string> SfvCalls { get; } = [];
        public bool RarShouldSucceed { get; set; } = true;
        public bool SfvShouldSucceed { get; set; } = true;

        /// <summary>
        /// The <see cref="SRRCreationOptions"/> actually passed to each <see cref="CreateFromRARAsync"/>
        /// call, in call order — lets a test verify a NESTED SRR call received its own, possibly
        /// different, options rather than just the outer run's.
        /// </summary>
        public List<SRRCreationOptions> RarCallOptions { get; } = [];

        /// <summary>
        /// The <see cref="SRRCreationOptions"/> passed to each <see cref="CreateFromSFVAsync"/> call,
        /// parallel to <see cref="SfvCalls"/> — lets the wizard-placeholder test verify the nested
        /// subtitle SRR call forced ComputeOSOHashes off even when the outer run enabled it.
        /// </summary>
        public List<SRRCreationOptions> SfvCallOptions { get; } = [];

        /// <summary>
        /// Every additional file's bytes, captured AT CALL TIME (mirroring what the real writer
        /// does — reads each source before returning) so a test can assert on generated-artifact
        /// content even though CreatorViewModel's own `finally` deletes the working dir right after
        /// this call returns.
        /// </summary>
        public Dictionary<string, byte[]> CapturedContents { get; } = new(StringComparer.OrdinalIgnoreCase);

        public Task<SRRCreationResult> CreateFromRARAsync(string outputPath, IReadOnlyList<string> rarVolumePaths,
            IReadOnlyList<StoredFileEntry>? storedFiles, SRRCreationOptions options, CancellationToken ct)
        {
            RarCalls.Add(rarVolumePaths[0]);
            RarCallOptions.Add(options);
            if (RarShouldSucceed)
            {
                File.WriteAllBytes(outputPath, [9]);
            }

            return Task.FromResult(new SRRCreationResult { Success = RarShouldSucceed, ErrorMessage = RarShouldSucceed ? null : "boom" });
        }

        public Task<SRRCreationResult> CreateFromSFVAsync(string outputPath, string sfvFilePath,
            IReadOnlyList<StoredFileEntry>? additionalFiles, SRRCreationOptions options, CancellationToken ct)
        {
            SfvCalls.Add(sfvFilePath);
            SfvCallOptions.Add(options);
            if (SfvShouldSucceed)
            {
                File.WriteAllBytes(outputPath, [9]);
            }

            return Task.FromResult(new SRRCreationResult { Success = SfvShouldSucceed, ErrorMessage = SfvShouldSucceed ? null : "boom" });
        }

        public Task<SRRCreationResult> CreateFromInputsAsync(string outputPath, IReadOnlyList<string> inputFiles,
            string? rootFolder, bool storeRelativePaths, IReadOnlyList<StoredFileEntry>? additionalFiles,
            SRRCreationOptions options, CancellationToken ct)
        {
            LastAdditionalFiles = additionalFiles;
            if (additionalFiles is not null)
            {
                foreach (StoredFileEntry entry in additionalFiles)
                {
                    if (File.Exists(entry.FullPath))
                    {
                        CapturedContents[entry.StoredName] = File.ReadAllBytes(entry.FullPath);
                    }
                }
            }

            return Task.FromResult(new SRRCreationResult { Success = true });
        }
    }

    private sealed class StubReleaseScanner(ReleaseScanResult result) : IReleaseScanner
    {
        public ReleaseScanResult Scan(string releaseRoot, CancellationToken ct = default) => result;
    }

    /// <summary>
    /// Real temp-directory service (the default <c>NoOpTempDirectoryService</c> throws) for the
    /// file-mode Advanced-tab / wizard-placeholder paths, which call
    /// <c>ITempDirectoryService.CreateTempDirectory()</c>. Roots temp dirs UNDER the fixture's
    /// <see cref="TempDirTestBase.TempDir"/> so the base fixture removes them; <see cref="Cleanup"/>
    /// is a no-op (the assertions read captured call state, never the on-disk temp files).
    /// </summary>
    private sealed class RealTempDirectoryService(string baseDir) : ITempDirectoryService
    {
        public string CreateTempDirectory()
        {
            string dir = Path.Combine(baseDir, "tmp-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            return dir;
        }

        public void Cleanup(string? tempDir) { }
    }

    // ── Helpers ─────────────────────────────────────────────

    private static string Touch(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "x");
        return path;
    }

    private static string WriteBytes(string path, byte[] bytes)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    private static string WriteSfv(string path, params string[] entries)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllLines(path, entries.Select(e => $"{e} 00000000"));
        return path;
    }

    private (CreatorViewModel Vm, RecordingSRSCreationService Srs, RecordingSRRCreationService Srr, string WorkDir) CreateVm(
        ReleaseScanResult scan, string? fixedWorkDir = null)
    {
        var srs = new RecordingSRSCreationService();
        var srr = new RecordingSRRCreationService();
        string workDir = fixedWorkDir ?? Path.Combine(TempDir, "work-" + Guid.NewGuid().ToString("N"));
        var vm = new CreatorViewModel(srr, srs, new NoOpFileDialogService(), new NoOpTempDirectoryService(),
            new NoOpAppSettingsService(), new TestUiDispatcher(), new StubReleaseScanner(scan), () => workDir)
        {
            AutoIncludeFiles = false,
            // Folder mode GATES sample-SRS staging on AutoCreateSRS and nested-subtitle-SRR
            // generation on CreateVobsubSRR. These staging tests exercise those artifacts, so both
            // must be ON (the production default) — a gating test flips the one it targets to false
            // explicitly. StoreFixRAR has no folder-mode staging effect.
            AutoCreateSRS = true,
            CreateVobsubSRR = true,
            StoreFixRAR = false,
        };
        return (vm, srs, srr, workDir);
    }

    private async Task<IReadOnlyList<StoredFileEntry>> RunCreateAsync(CreatorViewModel vm, string root, RecordingSRRCreationService srr)
    {
        vm.InputPath = root;
        await vm.LastFolderScan!;
        vm.OutputPath = Path.Combine(TempDir, "out-" + Guid.NewGuid().ToString("N") + ".srr");
        await vm.CreateSRRCommand.ExecuteAsync(null);
        return srr.LastAdditionalFiles ?? [];
    }

    /// <summary>
    /// Builds a FILE-mode VM (input is a .sfv/.rar FILE, not a folder) with a real temp-dir service,
    /// for the Advanced-tab (<c>CreateVobsubSRR</c>) and wizard-placeholder paths (both live in the
    /// non-folder-mode branch of <c>CreateSRRAsync</c> and use <c>ITempDirectoryService</c>).
    /// </summary>
    private (CreatorViewModel Vm, RecordingSRRCreationService Srr) CreateFileModeVm(bool createVobsubSrr)
    {
        var srs = new RecordingSRSCreationService();
        var srr = new RecordingSRRCreationService();
        var vm = new CreatorViewModel(srr, srs, new NoOpFileDialogService(), new RealTempDirectoryService(TempDir),
            new NoOpAppSettingsService(), new TestUiDispatcher(),
            new StubReleaseScanner(new ReleaseScanResult([], [], [], [], [], [])),
            () => Path.Combine(TempDir, "work-" + Guid.NewGuid().ToString("N")))
        {
            AutoIncludeFiles = false,
            AutoCreateSRS = false,
            CreateVobsubSRR = createVobsubSrr,
            StoreFixRAR = false,
        };
        return (vm, srr);
    }

    // ── 1. Extension-swap naming ──────────────────────────────

    [Fact]
    public async Task Sample_NoCollision_SrsNameDropsSourceExtension()
    {
        string root = Path.Combine(TempDir, "release-" + Guid.NewGuid().ToString("N"));
        string sample = Touch(Path.Combine(root, "Sample", "clip.mkv"));
        var scan = new ReleaseScanResult([], [sample], [], [], [], []);
        (CreatorViewModel vm, _, RecordingSRRCreationService srr, _) = CreateVm(scan);

        IReadOnlyList<StoredFileEntry> additionalFiles = await RunCreateAsync(vm, root, srr);

        Assert.Contains(additionalFiles, e => e.StoredName == "Sample/clip.srs");
    }

    // ── 2. Cross-dir same-stem: NOT a collision ───────────────

    [Fact]
    public async Task Samples_SameBasenameStem_DifferentDirs_NoCollision_BothDropExtension()
    {
        string root = Path.Combine(TempDir, "release-" + Guid.NewGuid().ToString("N"));
        string sample1 = Touch(Path.Combine(root, "Sample", "clip.mkv"));
        string sample2 = Touch(Path.Combine(root, "Extras", "clip.avi"));
        var scan = new ReleaseScanResult([], [sample1, sample2], [], [], [], []);
        (CreatorViewModel vm, _, RecordingSRRCreationService srr, _) = CreateVm(scan);

        IReadOnlyList<StoredFileEntry> additionalFiles = await RunCreateAsync(vm, root, srr);

        Assert.Contains(additionalFiles, e => e.StoredName == "Sample/clip.srs");
        Assert.Contains(additionalFiles, e => e.StoredName == "Extras/clip.srs");
    }

    // ── 3. Same-stem, same dir: collision keeps the full source extension ──

    [Fact]
    public async Task Samples_SameRelativeStem_Collision_KeepsFullSourceExtension()
    {
        string root = Path.Combine(TempDir, "release-" + Guid.NewGuid().ToString("N"));
        string sample1 = Touch(Path.Combine(root, "Sample", "clip.mkv"));
        string sample2 = Touch(Path.Combine(root, "Sample", "clip.avi"));
        var scan = new ReleaseScanResult([], [sample1, sample2], [], [], [], []);
        (CreatorViewModel vm, _, RecordingSRRCreationService srr, _) = CreateVm(scan);

        IReadOnlyList<StoredFileEntry> additionalFiles = await RunCreateAsync(vm, root, srr);

        Assert.Contains(additionalFiles, e => e.StoredName == "Sample/clip.mkv.srs");
        Assert.Contains(additionalFiles, e => e.StoredName == "Sample/clip.avi.srs");
        Assert.DoesNotContain(additionalFiles, e => e.StoredName == "Sample/clip.srs");
    }

    // ── 4. Supersede: a freshly-generated SRS replaces a pre-existing one at the same name ──

    [Fact]
    public async Task GeneratedSrs_SupersedesPreExistingSrs_SameLogicalName_NoCollisionError()
    {
        string root = Path.Combine(TempDir, "release-" + Guid.NewGuid().ToString("N"));
        string sample = Touch(Path.Combine(root, "Sample", "clip.mkv"));
        string preExistingSrs = Touch(Path.Combine(root, "Sample", "clip.srs"));
        // The baseline StoredFiles snapshot (as ApplyFolderScanResult would build it) already
        // contains the pre-existing srs at its root-relative name.
        var scan = new ReleaseScanResult([], [sample], [], [preExistingSrs], [], []);
        (CreatorViewModel vm, _, RecordingSRRCreationService srr, string workDir) = CreateVm(scan);

        IReadOnlyList<StoredFileEntry> additionalFiles = await RunCreateAsync(vm, root, srr);

        StoredFileEntry entry = Assert.Single(additionalFiles, e => e.StoredName == "Sample/clip.srs");
        // The surviving entry points at the freshly-generated file (under the working dir), not
        // the original pre-existing one on disk.
        Assert.StartsWith(workDir, entry.FullPath, StringComparison.OrdinalIgnoreCase);
        Assert.NotEqual(preExistingSrs, entry.FullPath, StringComparer.OrdinalIgnoreCase);
    }

    // ── 5. SRS failure .txt gated on the SAMPLE FILE's size, not the error text's ──

    [Fact]
    public async Task SrsFailure_NonEmptySample_TxtStored_ZeroByteSample_NothingStored()
    {
        // generate_srr gates the failure .txt on `os.path.getsize(sample) > 0` — the SAMPLE FILE's
        // own size — unconditionally on the error text. A genuinely 0-byte sample must suppress the
        // .txt even when the error message itself is non-empty.
        string root = Path.Combine(TempDir, "release-" + Guid.NewGuid().ToString("N"));
        string failingSample = Touch(Path.Combine(root, "Sample", "bad.mkv")); // 1 byte, non-empty
        string zeroByteSample = WriteBytes(Path.Combine(root, "Sample", "empty.mkv"), []); // 0 bytes
        var scan = new ReleaseScanResult([], [failingSample, zeroByteSample], [], [], [], []);
        (CreatorViewModel vm, RecordingSRSCreationService srs, RecordingSRRCreationService srr, _) = CreateVm(scan);
        srs.Configure(failingSample, success: false, error: "SRS creation failed for bad.mkv!");
        // Non-empty error text on the 0-byte sample too — proves the gate is sample SIZE, not error length.
        srs.Configure(zeroByteSample, success: false, error: "SRS creation failed for empty.mkv!");

        IReadOnlyList<StoredFileEntry> additionalFiles = await RunCreateAsync(vm, root, srr);

        Assert.Contains(additionalFiles, e => e.StoredName == "Sample/bad.mkv.txt");
        Assert.Equal("SRS creation failed for bad.mkv!", System.Text.Encoding.UTF8.GetString(srr.CapturedContents["Sample/bad.mkv.txt"]));
        Assert.DoesNotContain(additionalFiles, e => e.StoredName == "Sample/empty.mkv.txt");
        Assert.DoesNotContain(additionalFiles, e => e.StoredName == "Sample/empty.mkv.srs");
    }

    // ── 7. RAR-backed .vob sample keeps its SRS AND adds a nested SRR ──

    [Fact]
    public async Task RarBackedVobSample_KeepsSrs_AndAddsNestedSrr()
    {
        string root = Path.Combine(TempDir, "release-" + Guid.NewGuid().ToString("N"));
        string vobSample = WriteBytes(Path.Combine(root, "Sample", "clip.vob"), [(byte)'R', (byte)'a', (byte)'r', (byte)'!', 0x1A, 0x07, 0x00]);
        var scan = new ReleaseScanResult([], [vobSample], [], [], [], []);
        (CreatorViewModel vm, _, RecordingSRRCreationService srr, _) = CreateVm(scan);

        IReadOnlyList<StoredFileEntry> additionalFiles = await RunCreateAsync(vm, root, srr);

        Assert.Contains(additionalFiles, e => e.StoredName == "Sample/clip.srs");
        Assert.Contains(additionalFiles, e => e.StoredName == "Sample/clip.srr");
        Assert.Contains(vobSample, srr.RarCalls);
    }

    [Fact]
    public async Task UppercaseVOB_RarBacked_DoesNotGetNestedSrr_CaseSensitiveExtensionCheck()
    {
        // generate_srr: sample.endswith(".vob") is case-SENSITIVE — a ".VOB" sample never matches,
        // even though its leading bytes are the same RAR marker.
        string root = Path.Combine(TempDir, "release-" + Guid.NewGuid().ToString("N"));
        string vobSample = WriteBytes(Path.Combine(root, "Sample", "clip.VOB"), [(byte)'R', (byte)'a', (byte)'r', (byte)'!', 0x1A, 0x07, 0x00]);
        var scan = new ReleaseScanResult([], [vobSample], [], [], [], []);
        (CreatorViewModel vm, _, RecordingSRRCreationService srr, _) = CreateVm(scan);

        IReadOnlyList<StoredFileEntry> additionalFiles = await RunCreateAsync(vm, root, srr);

        Assert.Contains(additionalFiles, e => e.StoredName == "Sample/clip.srs");
        Assert.DoesNotContain(additionalFiles, e => e.StoredName == "Sample/clip.srr");
        Assert.Empty(srr.RarCalls);
    }

    [Fact]
    public async Task RarBackedVobSample_NestedSrr_ForcesComputeOSOHashesFalse_RegardlessOfOuterSetting()
    {
        // pyrescene's create_srr_single_volume (main.py) writes ONLY SrrHeaderBlock +
        // SrrRarFileBlock + raw RAR block bytes — it has NO oso_hash parameter or logic AT ALL, so a
        // .vob/single-volume nested SRR NEVER contains OSO blocks. Forwarding the OUTER `options`
        // (ComputeOSOHashes on) into the .vob nested CreateFromRARAsync would emit OSO blocks
        // pyrescene omits — a byte divergence the golden can't catch (it runs oso-off + a .ts
        // sample, not a .vob). Mirrors the sibling nested-SRR tests: the nested call must force oso
        // off regardless of the outer run's setting.
        string root = Path.Combine(TempDir, "release-" + Guid.NewGuid().ToString("N"));
        string vobSample = WriteBytes(Path.Combine(root, "Sample", "clip.vob"), [(byte)'R', (byte)'a', (byte)'r', (byte)'!', 0x1A, 0x07, 0x00]);
        var scan = new ReleaseScanResult([], [vobSample], [], [], [], []);
        (CreatorViewModel vm, _, RecordingSRRCreationService srr, _) = CreateVm(scan);
        vm.ComputeOSOHashes = true; // the OUTER setting — must NOT reach the .vob nested SRR call

        await RunCreateAsync(vm, root, srr);

        // The .vob nested SRR is the only CreateFromRARAsync in this scenario (no subtitle SFVs).
        Assert.Contains(vobSample, srr.RarCalls);
        SRRCreationOptions nestedOptions = Assert.Single(srr.RarCallOptions);
        Assert.False(nestedOptions.ComputeOSOHashes);
    }

    // ── 8. Cancellation removes the working dir; OCE not swallowed by the staging code ──

    [Fact]
    public async Task Cancellation_DuringArtifactStaging_RemovesWorkingDir_DoesNotSwallowOce()
    {
        string root = Path.Combine(TempDir, "release-" + Guid.NewGuid().ToString("N"));
        string sample = Touch(Path.Combine(root, "Sample", "clip.mkv"));
        var scan = new ReleaseScanResult([], [sample], [], [], [], []);
        string workDir = Path.Combine(TempDir, "cancel-work-" + Guid.NewGuid().ToString("N"));
        (CreatorViewModel vm, RecordingSRSCreationService srs, RecordingSRRCreationService srr, _) = CreateVm(scan, workDir);
        srs.ConfigureThrow(sample, new OperationCanceledException("simulated mid-staging cancellation"));

        vm.InputPath = root;
        await vm.LastFolderScan!;
        vm.OutputPath = Path.Combine(TempDir, "out.srr");

        await vm.CreateSRRCommand.ExecuteAsync(null);

        // The command's OWN Task completes normally either way (this codebase's established
        // convention — see ReconstructorViewModel/FileCompareViewModel — is to swallow OCE and
        // report it through VM state, not rethrow), so "observably cancelled" is proven via that
        // state instead: IsCreating resets (the command is runnable again, not stuck "in progress"
        // forever) and BuildSucceeded/ProgressMessage below never read as a normal completion.
        Assert.False(vm.IsCreating);
        // The staging code's own `finally` must have deleted the working dir it created — a
        // swallowed OCE (or one that skipped the finally) would leave it behind.
        Assert.False(Directory.Exists(workDir));
        // The VM's own top-level catch (which distinguishes cancellation from a real error) is what
        // ultimately absorbs the exception — but if my inner staging code had SWALLOWED it instead
        // of letting it propagate, the run would incorrectly report success.
        Assert.False(vm.BuildSucceeded);
        Assert.Null(srr.LastAdditionalFiles); // CreateFromInputsAsync never reached
        Assert.Equal("Cancelled.", vm.ProgressMessage);
        Assert.DoesNotContain(vm.LogEntries, e => e.Contains("ERROR", StringComparison.Ordinal));
    }

    // ── 9. Proof-before-sfv reorder over the complete merged list ────────

    [Fact]
    public async Task SubtitleNestedSrr_NaturallyPrecedesItsSfv_InTheMergedList()
    {
        string root = Path.Combine(TempDir, "release-" + Guid.NewGuid().ToString("N"));
        string nfo = Touch(Path.Combine(root, "release.nfo"));
        // A genuine SFV listing: GenerateNestedSubtitleSrrsAsync parses this SFV's OWN entries for
        // real, to discover its RAR chain(s) — a plain Touch'd placeholder file, valid when the SFV
        // content itself was never inspected, no longer is.
        string subSfv = WriteSfv(Path.Combine(root, "Subs", "subs.sfv"), "subs.rar");
        Touch(Path.Combine(root, "Subs", "subs.rar"));
        // The scanner's pass-10 ALREADY stores every sfv, including excluded/subtitle ones — the
        // subtitle sfv IS in the baseline StoredFiles a real scan would hand back (it must not be
        // re-added by GenerateSubtitleArtifactsAsync itself).
        var scan = new ReleaseScanResult([], [], [subSfv], [nfo, subSfv], [], []);
        (CreatorViewModel vm, _, RecordingSRRCreationService srr, _) = CreateVm(scan);

        IReadOnlyList<StoredFileEntry> additionalFiles = await RunCreateAsync(vm, root, srr);

        List<string> names = [.. additionalFiles.Select(e => e.StoredName)];
        int srrIndex = names.IndexOf("Subs/subs.srr");
        int sfvIndex = names.IndexOf("Subs/subs.sfv");
        Assert.True(srrIndex >= 0 && sfvIndex >= 0);
        Assert.Equal(sfvIndex - 1, srrIndex);
        // The sfv is stored exactly ONCE — no redundant re-add on top of the baseline's own.
        Assert.Single(names, n => n == "Subs/subs.sfv");
        // nfo (unrelated to the reorder) keeps its own position ahead of everything else.
        Assert.Equal(0, names.IndexOf("release.nfo"));
    }

    [Fact]
    public async Task BaselineProofPair_ArrivingOutOfOrder_IsCorrectedByTheVmsOwnReorderPass()
    {
        // Defense-in-depth: the REAL ReleaseScanner already reorders a rule-4 proof sfv/rar pair
        // before returning (its OWN internal ApplyProofBeforeSfvReorder call — see
        // ReleaseScannerStoredTests.ProofRar_AlreadyStoredByRule4_NotDoubleAdded), so a genuine
        // scan result never arrives at the VM out of order. This test feeds a hand-built
        // ReleaseScanResult with the pair in the WRONG order anyway (as a user-edited StoredFiles
        // list, or a different IReleaseScanner implementation, might) to prove the VM's OWN
        // ApplyProofBeforeSfvReorder call is what fixes it — not merely inherited for free from the
        // scanner having already done so.
        string root = Path.Combine(TempDir, "release-" + Guid.NewGuid().ToString("N"));
        string proofSfv = Touch(Path.Combine(root, "Proof", "p.sfv"));
        string proofRar = Touch(Path.Combine(root, "Proof", "p.rar"));
        // A sample is present purely so the folder-mode Create branch actually invokes
        // StageFolderArtifactsAsync (it's a no-op when there's nothing to generate).
        string sample = Touch(Path.Combine(root, "Sample", "clip.mkv"));
        var scan = new ReleaseScanResult([], [sample], [], [proofSfv, proofRar], [], []);
        (CreatorViewModel vm, _, RecordingSRRCreationService srr, _) = CreateVm(scan);

        IReadOnlyList<StoredFileEntry> additionalFiles = await RunCreateAsync(vm, root, srr);

        List<string> names = [.. additionalFiles.Select(e => e.StoredName)];
        int sfvIndex = names.IndexOf("Proof/p.sfv");
        int rarIndex = names.IndexOf("Proof/p.rar");
        Assert.True(sfvIndex >= 0 && rarIndex >= 0);
        Assert.Equal(sfvIndex - 1, rarIndex);
    }

    // ── 10. Splice anchors use PROOF-DIRECTORY classification, not a name substring ──

    [Fact]
    public async Task ProofSubstringInFilenames_NotMisclassifiedAsProofDirectory()
    {
        // FindSampleArtifactSpliceIndex used to skip any entry whose STORED NAME merely CONTAINED
        // "proof" as a splice anchor — misclassifying a main `proofread.sfv` or a conditional fix
        // RAR named `movie.proof.fix.rar` (both contain "proof" as PART of a filename, not as a
        // directory) as proof entries, splicing the sample at the wrong index.
        string root = Path.Combine(TempDir, "release-" + Guid.NewGuid().ToString("N"));
        string mainSfv = Touch(Path.Combine(root, "proofread.sfv"));
        string fixRar = Touch(Path.Combine(root, "movie.proof.fix.rar"));
        string sample = Touch(Path.Combine(root, "Sample", "clip.mkv"));
        var scan = new ReleaseScanResult([], [sample], [], [fixRar, mainSfv], [], []);
        (CreatorViewModel vm, _, RecordingSRRCreationService srr, _) = CreateVm(scan);

        IReadOnlyList<StoredFileEntry> additionalFiles = await RunCreateAsync(vm, root, srr);

        List<string> names = [.. additionalFiles.Select(e => e.StoredName)];
        int sampleIdx = names.IndexOf("Sample/clip.srs");
        int fixRarIdx = names.IndexOf("movie.proof.fix.rar");
        Assert.True(sampleIdx >= 0 && fixRarIdx >= 0);
        Assert.True(sampleIdx < fixRarIdx,
            "the sample must splice BEFORE the non-proof-directory fix RAR anchor, not skip past it as if it were a proof entry");
    }

    // ── 10b. Splice anchor reconciles with the scanner's OWN proof-before-sfv reorder — a proof
    //         pair already relocated into the final-SFV region must not be treated as an
    //         un-anchored "early proof category" blob. ──

    [Fact]
    public async Task ProofPair_AlreadyRelocatedIntoFinalSfvRegion_SampleSplicesBeforeTheWholeTail()
    {
        // A REAL ReleaseScanner.Scan already applies its own proof-before-sfv reorder
        // (ApplyProofBeforeSfvReorder) BEFORE handing back StoredFiles — so a proof RAR/SFV pair
        // sits ADJACENT, with the RAR immediately before its matching SFV, as part of the final-SFV
        // tail. This stub reproduces exactly that already-reordered shape (the stub bypasses the
        // scanner, so the test sets it up directly) rather than the pre-reorder "proof category,
        // then main sfv" shape the previous test used. The old splice logic treated ANY
        // proof-directory entry as unanchored regardless of this relocation (the `.rar` branch had
        // no reconciliation, and the `.sfv` branch still excluded proof-linked sfvs) — so the
        // sample would splice at the tail's very end (after main.sfv) instead of before the whole
        // relocated region, right after the plain nfo.
        string root = Path.Combine(TempDir, "release-" + Guid.NewGuid().ToString("N"));
        string nfo = Touch(Path.Combine(root, "release.nfo"));
        string proofRar = Touch(Path.Combine(root, "Proof", "p.rar"));
        string proofSfv = Touch(Path.Combine(root, "Proof", "p.sfv"));
        string mainSfv = Touch(Path.Combine(root, "main.sfv"));
        string sample = Touch(Path.Combine(root, "Sample", "clip.mkv"));
        var scan = new ReleaseScanResult([], [sample], [], [nfo, proofRar, proofSfv, mainSfv], [], []);
        (CreatorViewModel vm, _, RecordingSRRCreationService srr, _) = CreateVm(scan);

        IReadOnlyList<StoredFileEntry> additionalFiles = await RunCreateAsync(vm, root, srr);

        List<string> names = [.. additionalFiles.Select(e => e.StoredName)];
        int sampleIdx = names.IndexOf("Sample/clip.srs");
        int proofRarIdx = names.IndexOf("Proof/p.rar");
        int proofSfvIdx = names.IndexOf("Proof/p.sfv");
        int mainSfvIdx = names.IndexOf("main.sfv");
        Assert.True(sampleIdx >= 0 && proofRarIdx >= 0 && proofSfvIdx >= 0 && mainSfvIdx >= 0);
        Assert.True(sampleIdx < proofRarIdx,
            "the sample must splice BEFORE the entire already-relocated proof pair, not just before " +
            "the first entry that happens not to be under a proof directory");
        Assert.True(proofRarIdx < proofSfvIdx && proofSfvIdx < mainSfvIdx,
            "sanity: the scanner's own reorder (rar immediately before its matching sfv) is undisturbed by splicing");
    }

    // ── 11. A REAL ReleaseScanner (not a stub) exercises the actual pass-10/staging contract ──

    [Fact]
    public async Task RealScanner_SubtitleSfv_StoredExactlyOnce_NestedSrrImmediatelyBeforeIt()
    {
        // Every other test in this file uses StubReleaseScanner with hand-built results, which
        // never actually exercised the real scanner's pass-10 (which ALREADY stores every sfv,
        // including excluded/subtitle ones) — so a redundant re-add in
        // GenerateSubtitleArtifactsAsync went untested against the real contract. Wires a REAL
        // ReleaseScanner through CreatorViewModel end-to-end.
        string root = Path.Combine(TempDir, "release-" + Guid.NewGuid().ToString("N"));
        Touch(Path.Combine(root, "release.nfo"));
        WriteSfv(Path.Combine(root, "Subs", "subs.sfv"), "subs.rar");
        Touch(Path.Combine(root, "Subs", "subs.rar"));

        var realScanner = new ReleaseScanner(sfvEntryReader: null, proofRarReader: null);
        var srs = new RecordingSRSCreationService();
        var srr = new RecordingSRRCreationService();
        string workDir = Path.Combine(TempDir, "work-" + Guid.NewGuid().ToString("N"));
        var vm = new CreatorViewModel(srr, srs, new NoOpFileDialogService(), new NoOpTempDirectoryService(),
            new NoOpAppSettingsService(), new TestUiDispatcher(), realScanner, () => workDir)
        {
            AutoIncludeFiles = false,
            // This test asserts the nested subtitle SRR is staged, so folder mode's
            // AutoCreateSRS/CreateVobsubSRR gates must be ON (the production default).
            AutoCreateSRS = true,
            CreateVobsubSRR = true,
            StoreFixRAR = false,
        };

        IReadOnlyList<StoredFileEntry> additionalFiles = await RunCreateAsync(vm, root, srr);

        List<string> names = [.. additionalFiles.Select(e => e.StoredName)];
        Assert.Single(names, n => n == "Subs/subs.sfv");
        int srrIdx = names.IndexOf("Subs/subs.srr");
        int sfvIdx = names.IndexOf("Subs/subs.sfv");
        Assert.True(srrIdx >= 0 && sfvIdx >= 0);
        Assert.Equal(sfvIdx - 1, srrIdx);
    }

    // ── 11b. A subtitle SFV listing MULTIPLE RAR chains yields ONE nested SRR PER CHAIN, each
    //        named by that chain's own first-RAR basename ──

    [Fact]
    public async Task SubtitleSfv_MultipleRarChains_YieldsOneNestedSrrPerChain_NamedByEachChainsOwnBasename()
    {
        // Multi-chain subtitle SFVs are in scope, not a dead/out-of-scope test seam — pyrescene's
        // create_srr_for_subs requires this: a two-language subtitle SFV ("eng.rar" + a separate
        // "jpn.rar", NOT one merged archive) must produce TWO nested SRRs, "eng.srr" and "jpn.srr"
        // — never a single "subs.srr" wrapping both chains.
        string root = Path.Combine(TempDir, "release-" + Guid.NewGuid().ToString("N"));
        string nfo = Touch(Path.Combine(root, "release.nfo"));
        string subSfv = WriteSfv(Path.Combine(root, "Subs", "subs.sfv"), "eng.rar", "jpn.rar");
        Touch(Path.Combine(root, "Subs", "eng.rar"));
        Touch(Path.Combine(root, "Subs", "jpn.rar"));
        // The scanner's pass-10 already stores the subtitle sfv itself.
        var scan = new ReleaseScanResult([], [], [subSfv], [nfo, subSfv], [], []);
        (CreatorViewModel vm, _, RecordingSRRCreationService srr, _) = CreateVm(scan);

        IReadOnlyList<StoredFileEntry> additionalFiles = await RunCreateAsync(vm, root, srr);

        List<string> names = [.. additionalFiles.Select(e => e.StoredName)];
        Assert.Single(names, n => n == "Subs/eng.srr");
        Assert.Single(names, n => n == "Subs/jpn.srr");
        Assert.DoesNotContain("Subs/subs.srr", names);
        // The sfv itself is still stored exactly once (pass-10's baseline — not re-added per chain
        // just because there happen to be two of them now).
        Assert.Single(names, n => n == "Subs/subs.sfv");
        // Both chains' RAR volumes were actually handed to the writer as TWO SEPARATE calls (one
        // nested SRR per chain), not folded into a single call covering both.
        Assert.Equal(2, srr.RarCalls.Count);
    }

    // ── 12. Cancellation is reported as "Cancelled.", not swallowed as a generic error ──

    [Fact]
    public async Task Cancellation_ReportsCancelled_NotSwallowedAsGenericError()
    {
        string root = Path.Combine(TempDir, "release-" + Guid.NewGuid().ToString("N"));
        string sample = Touch(Path.Combine(root, "Sample", "clip.mkv"));
        var scan = new ReleaseScanResult([], [sample], [], [], [], []);
        (CreatorViewModel vm, RecordingSRSCreationService srs, RecordingSRRCreationService srr, _) = CreateVm(scan);
        srs.ConfigureThrow(sample, new OperationCanceledException("simulated cancellation"));

        vm.InputPath = root;
        await vm.LastFolderScan!;
        vm.OutputPath = Path.Combine(TempDir, "out.srr");

        await vm.CreateSRRCommand.ExecuteAsync(null);

        Assert.Equal("Cancelled.", vm.ProgressMessage);
        Assert.False(vm.BuildSucceeded);
        Assert.DoesNotContain(vm.LogEntries, e => e.Contains("ERROR", StringComparison.Ordinal));
        Assert.Contains(vm.LogEntries, e => e.Contains("Cancelled", StringComparison.Ordinal));
        // The command's Task itself completes normally either way (this codebase's established
        // swallow-and-report convention, not an omission — see the catch block's own remarks);
        // IsCreating resetting to false is what proves the run is actually OVER, not stuck "in
        // progress" while silently having given up.
        Assert.False(vm.IsCreating);
    }

    // ── 13. A manually-added subtitle source OUTSIDE the release root gets a valid name AND is
    //       actually stored ──

    [Fact]
    public async Task SubtitleSfv_OutsideReleaseRoot_UsesSubsFallbackName_AndIsStoredExactlyOnce()
    {
        // ExtraSubtitleSfvFiles is shared with the file-mode Advanced tab's "Add Subtitle" command
        // — a user in folder mode can still append an out-of-root subtitle file without triggering
        // a re-scan. The raw root-relative name would keep an invalid "../" prefix the writer's
        // CanonicalizeRelative rejects; FolderRelativeStem falls back to "Subs/<basename>" instead,
        // matching the pre-existing GeneratedStoredName convention.
        //
        // This scenario is also the regression introduced by relying on pass-10 baseline storage
        // instead of an unconditional re-add: a manually-added subtitle (unlike a scanner-origin
        // one) never reaches the scanner's pass-10 sfv storage at all, so dropping the unconditional
        // re-add left it stored NOWHERE. The stub scan below has an EMPTY StoredFiles/SubtitleSfvs
        // (this sfv was never seen by any scanner — exactly the manual-add case), so this .sfv
        // reaching the merged list can only be this fix, not pass-10 baseline storage.
        string root = Path.Combine(TempDir, "release-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        // A genuine SFV listing (GenerateNestedSubtitleSrrsAsync parses this SFV's OWN entries for
        // real) — OUTSIDE root, alongside its one RAR chain.
        string outsideSfv = WriteSfv(Path.Combine(TempDir, "external-subs.sfv"), "external-subs.rar");
        Touch(Path.Combine(TempDir, "external-subs.rar"));
        var scan = new ReleaseScanResult([], [], [], [], [], []);
        (CreatorViewModel vm, _, RecordingSRRCreationService srr, _) = CreateVm(scan);

        vm.InputPath = root;
        await vm.LastFolderScan!;
        // Simulates the "Add Subtitle" command appending an out-of-root file without a re-scan.
        vm.ExtraSubtitleSfvFiles.Add(outsideSfv);
        vm.OutputPath = Path.Combine(TempDir, "out-" + Guid.NewGuid().ToString("N") + ".srr");
        await vm.CreateSRRCommand.ExecuteAsync(null);
        IReadOnlyList<StoredFileEntry> additionalFiles = srr.LastAdditionalFiles ?? [];

        Assert.Contains(additionalFiles, e => e.StoredName == "Subs/external-subs.srr");
        Assert.Single(additionalFiles, e => e.StoredName == "Subs/external-subs.sfv");
        Assert.DoesNotContain(additionalFiles, e => e.StoredName.Contains("..", StringComparison.Ordinal));
    }

    // ── 14. Skip a nested SRR when a same-stem RAR is already stored ──

    [Fact]
    public async Task SubtitleSfv_SameStemRarAlreadyStored_NestedSrrSkipped_SfvStillStoredOnce()
    {
        // generate_srr: "not for Proof RARs that are already stored inside the SRR" — a subtitle
        // SFV whose basename-stem-swapped-to-.rar is already present in the stored list (e.g. an
        // independently-discovered proof RAR sharing the excluded SFV's stem) must not ALSO get a
        // redundant nested SRR wrapping that same RAR content.
        string root = Path.Combine(TempDir, "release-" + Guid.NewGuid().ToString("N"));
        string subSfv = Touch(Path.Combine(root, "Subs", "subs.sfv"));
        string alreadyStoredRar = Touch(Path.Combine(root, "Subs", "subs.rar"));
        var scan = new ReleaseScanResult([], [], [subSfv], [alreadyStoredRar, subSfv], [], []);
        (CreatorViewModel vm, _, RecordingSRRCreationService srr, _) = CreateVm(scan);

        IReadOnlyList<StoredFileEntry> additionalFiles = await RunCreateAsync(vm, root, srr);

        Assert.DoesNotContain(additionalFiles, e => e.StoredName == "Subs/subs.srr");
        Assert.Single(additionalFiles, e => e.StoredName == "Subs/subs.sfv");
        Assert.Empty(srr.SfvCalls); // nested-SRR creation never even attempted
    }

    // ── 15. The nested subtitle SRR forces ComputeOSOHashes off, regardless of the outer run's
    //        own setting ──

    [Fact]
    public async Task SubtitleNestedSrr_ForcesComputeOSOHashesFalse_RegardlessOfOuterSetting()
    {
        // create_srr_for_subs HARDCODES oso_hash=False for its own nested-creation call — it does
        // NOT forward whichever setting the outer SRR happens to use. A user enabling
        // ComputeOSOHashes for the OUTER SRR must not leak OSO blocks into a nested subtitle SRR
        // that pyrescene never adds them to.
        string root = Path.Combine(TempDir, "release-" + Guid.NewGuid().ToString("N"));
        string subSfv = WriteSfv(Path.Combine(root, "Subs", "subs.sfv"), "subs.rar");
        Touch(Path.Combine(root, "Subs", "subs.rar"));
        var scan = new ReleaseScanResult([], [], [subSfv], [subSfv], [], []);
        (CreatorViewModel vm, _, RecordingSRRCreationService srr, _) = CreateVm(scan);
        vm.ComputeOSOHashes = true; // the OUTER setting — must NOT reach the nested SRR call

        await RunCreateAsync(vm, root, srr);

        SRRCreationOptions nestedOptions = Assert.Single(srr.RarCallOptions);
        Assert.False(nestedOptions.ComputeOSOHashes);
    }

    // ── 16. A subtitle SFV's nested SRR is emitted BEFORE the SFV's own entry — pyrescene pass 9
    //        (nested SRRs) precedes pass 10 (subtitle SFV tail). The same-stem reorder cannot
    //        repair a DIFFERENTLY-named pair (subs.sfv listing eng.rar), so the artifact-block
    //        ORDER itself must be right. ──

    [Fact]
    public async Task ManualSubtitleSfv_DifferentlyNamedChain_NestedSrrImmediatelyPrecedesSfv()
    {
        // A manually-added subtitle SFV (subs.sfv) whose RAR chain (eng.rar) has a DIFFERENT stem:
        // the pass-10 same-stem reorder looks for eng.sfv to place eng.srr before, but the SFV is
        // subs.sfv, so it can never repair the order. The old per-SFV interleaving emitted
        // [subs.sfv, eng.srr]; pyrescene emits [eng.srr, subs.sfv].
        string root = Path.Combine(TempDir, "release-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string subSfv = WriteSfv(Path.Combine(root, "Subs", "subs.sfv"), "eng.rar");
        Touch(Path.Combine(root, "Subs", "eng.rar"));
        var scan = new ReleaseScanResult([], [], [], [], [], []);
        (CreatorViewModel vm, _, RecordingSRRCreationService srr, _) = CreateVm(scan);

        vm.InputPath = root;
        await vm.LastFolderScan!;
        vm.ExtraSubtitleSfvFiles.Add(subSfv); // manual "Add Subtitle", differently-named
        vm.OutputPath = Path.Combine(TempDir, "out-" + Guid.NewGuid().ToString("N") + ".srr");
        await vm.CreateSRRCommand.ExecuteAsync(null);
        IReadOnlyList<StoredFileEntry> additionalFiles = srr.LastAdditionalFiles ?? [];

        List<string> names = [.. additionalFiles.Select(e => e.StoredName)];
        int srrIndex = names.IndexOf("Subs/eng.srr");
        int sfvIndex = names.IndexOf("Subs/subs.sfv");
        Assert.True(srrIndex >= 0, "nested eng.srr must be present");
        Assert.True(sfvIndex >= 0, "subtitle subs.sfv must be present");
        Assert.Equal(sfvIndex - 1, srrIndex); // eng.srr IMMEDIATELY before subs.sfv
    }

    [Fact]
    public async Task TwoManualSubtitleSfvs_AllNestedSrrsPrecedeAllSfvEntries()
    {
        // The >=2-subtitle-SFV case: pyrescene's vobsub loop creates EVERY nested SRR (pass 9),
        // THEN the tail loop appends EVERY subtitle SFV (pass 10) — two passes, not per-SFV
        // interleaving. So both nested SRRs must precede both SFV entries.
        string root = Path.Combine(TempDir, "release-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string subA = WriteSfv(Path.Combine(root, "Subs", "subsA.sfv"), "aaa.rar");
        string subB = WriteSfv(Path.Combine(root, "Subs", "subsB.sfv"), "bbb.rar");
        Touch(Path.Combine(root, "Subs", "aaa.rar"));
        Touch(Path.Combine(root, "Subs", "bbb.rar"));
        var scan = new ReleaseScanResult([], [], [], [], [], []);
        (CreatorViewModel vm, _, RecordingSRRCreationService srr, _) = CreateVm(scan);

        vm.InputPath = root;
        await vm.LastFolderScan!;
        vm.ExtraSubtitleSfvFiles.Add(subA);
        vm.ExtraSubtitleSfvFiles.Add(subB);
        vm.OutputPath = Path.Combine(TempDir, "out-" + Guid.NewGuid().ToString("N") + ".srr");
        await vm.CreateSRRCommand.ExecuteAsync(null);
        IReadOnlyList<StoredFileEntry> additionalFiles = srr.LastAdditionalFiles ?? [];

        List<string> names = [.. additionalFiles.Select(e => e.StoredName)];
        int aaaSrr = names.IndexOf("Subs/aaa.srr");
        int bbbSrr = names.IndexOf("Subs/bbb.srr");
        int subASfv = names.IndexOf("Subs/subsA.sfv");
        int subBSfv = names.IndexOf("Subs/subsB.sfv");
        Assert.True(aaaSrr >= 0 && bbbSrr >= 0 && subASfv >= 0 && subBSfv >= 0);
        Assert.True(Math.Max(aaaSrr, bbbSrr) < Math.Min(subASfv, subBSfv),
            "ALL nested SRRs must precede ALL subtitle-SFV entries (pyrescene pass 9 then pass 10)");
    }

    // ── 17. A spaced RAR name in a subtitle SFV must group as ONE chain, not throw and drop the
    //        whole chain (the old SFVFile.ReadFile split every space). ──

    [Fact]
    public async Task SubtitleSfv_SpacedRarName_ParsedAsOneChain_NotDropped()
    {
        // "sub title.rar"/"sub title.r00" — the name itself contains a space. The old
        // SFVFile.ReadFile(tolerant:false) split on every space, saw an 8-char-CRC check fail, and
        // THREW InvalidDataException -> the catch returned [] -> NO nested SRR at all. The shared
        // resolver's last-space parse keeps the space, yielding one chain / one "sub title.srr".
        string root = Path.Combine(TempDir, "release-" + Guid.NewGuid().ToString("N"));
        string nfo = Touch(Path.Combine(root, "release.nfo"));
        string subSfv = WriteSfv(Path.Combine(root, "Subs", "subs.sfv"), "sub title.rar", "sub title.r00");
        Touch(Path.Combine(root, "Subs", "sub title.rar"));
        Touch(Path.Combine(root, "Subs", "sub title.r00"));
        var scan = new ReleaseScanResult([], [], [subSfv], [nfo, subSfv], [], []);
        (CreatorViewModel vm, _, RecordingSRRCreationService srr, _) = CreateVm(scan);

        IReadOnlyList<StoredFileEntry> additionalFiles = await RunCreateAsync(vm, root, srr);

        List<string> names = [.. additionalFiles.Select(e => e.StoredName)];
        Assert.Single(names, n => n == "Subs/sub title.srr");
        Assert.Single(srr.RarCalls); // exactly one chain handed to the writer, not dropped
    }

    // ── 18. A `.\`-prefixed continuation must RESOLVE onto the same chain as its head, not split
    //        into a second same-named SRR (the old raw Path.Combine kept the `.\`, giving a
    //        distinct archive-set key and a duplicate "eng.srr"). ──

    [Fact]
    public async Task SubtitleSfv_DotSlashContinuation_GroupsWithHead_OneChainOneName()
    {
        // "eng.rar" + ".\eng.r00" is ONE chain. The old raw Path.Combine left ".\eng.r00" keyed as
        // ".../Subs/./eng" vs "eng.rar"'s ".../Subs/eng" -> two chains -> two "eng.srr" (a duplicate
        // logical name the writer later rejects). ResolveSfvEntry collapses the "." segment first.
        string root = Path.Combine(TempDir, "release-" + Guid.NewGuid().ToString("N"));
        string nfo = Touch(Path.Combine(root, "release.nfo"));
        string subSfv = WriteSfv(Path.Combine(root, "Subs", "subs.sfv"), "eng.rar", @".\eng.r00");
        Touch(Path.Combine(root, "Subs", "eng.rar"));
        Touch(Path.Combine(root, "Subs", "eng.r00"));
        var scan = new ReleaseScanResult([], [], [subSfv], [nfo, subSfv], [], []);
        (CreatorViewModel vm, _, RecordingSRRCreationService srr, _) = CreateVm(scan);

        IReadOnlyList<StoredFileEntry> additionalFiles = await RunCreateAsync(vm, root, srr);

        List<string> names = [.. additionalFiles.Select(e => e.StoredName)];
        Assert.Single(names, n => n == "Subs/eng.srr"); // ONE, not a duplicate pair
        Assert.Single(srr.RarCalls); // ONE chain: eng.rar + eng.r00 folded together
    }

    // ── 19. A folder with BOTH a scanner-discovered subtitle SFV AND a manually-added one stores
    //        EACH subtitle SFV exactly once. The relative ORDER of a manually-added SFV vs a
    //        scanner-origin one is an accepted [DIVERGENCE: determinism] (pyrescene has no
    //        manual-add feature — no parity target); the invariant that matters is exactly-once
    //        storage (no dup, none dropped). ──

    [Fact]
    public async Task MixedScannerAndManualSubtitleSfvs_EachStoredExactlyOnce()
    {
        string root = Path.Combine(TempDir, "release-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        // Scanner-discovered subtitle SFV: appears in BOTH the scan's SubtitleSfvs (→
        // ExtraSubtitleSfvFiles) and its StoredFiles baseline (the scanner's own pass-10 storage).
        string scannerSfv = WriteSfv(Path.Combine(root, "Subs", "scan.sfv"), "scan.rar");
        Touch(Path.Combine(root, "Subs", "scan.rar"));
        // Manually-added subtitle SFV: reaches only ExtraSubtitleSfvFiles (the "Add Subtitle"
        // command), never the scan's StoredFiles.
        string manualSfv = WriteSfv(Path.Combine(root, "Subs", "manual.sfv"), "manual.rar");
        Touch(Path.Combine(root, "Subs", "manual.rar"));

        var scan = new ReleaseScanResult([], [], [scannerSfv], [scannerSfv], [], []);
        (CreatorViewModel vm, _, RecordingSRRCreationService srr, _) = CreateVm(scan);

        vm.InputPath = root;
        await vm.LastFolderScan!;
        // Simulate "Add Subtitle" appending the manual SFV after the scan populated the scanner one.
        vm.ExtraSubtitleSfvFiles.Add(manualSfv);
        vm.OutputPath = Path.Combine(TempDir, "out-" + Guid.NewGuid().ToString("N") + ".srr");
        await vm.CreateSRRCommand.ExecuteAsync(null);
        IReadOnlyList<StoredFileEntry> additionalFiles = srr.LastAdditionalFiles ?? [];

        List<string> names = [.. additionalFiles.Select(e => e.StoredName)];
        // The invariant: each subtitle SFV stored EXACTLY ONCE (relative order is a [DIVERGENCE]).
        Assert.Single(names, n => n == "Subs/scan.sfv");
        Assert.Single(names, n => n == "Subs/manual.sfv");
    }

    // ── 20. The Advanced-tab create-time vobsub scan (CreateVobsubSRRsAsync) now produces one
    //        nested SRR PER RAR CHAIN via GenerateNestedSubtitleSrrsAsync, with oso forced off —
    //        like folder mode, not the old single-merged GenerateNestedSRRFileAsync. ──

    [Fact]
    public async Task AdvancedTab_VobsubScan_MultiChainSubtitleSfv_YieldsOneNestedSrrPerChain()
    {
        string root = Path.Combine(TempDir, "release-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        // Main SFV = the file-mode input (Advanced tab). The subtitle SFV lives in a Subs/ subdir so
        // ReleaseFileScanner.FindSubtitleSFVFiles auto-detects it at create time; it lists TWO chains.
        string mainSfv = WriteSfv(Path.Combine(root, "main.sfv"), "main.rar");
        WriteSfv(Path.Combine(root, "Subs", "subs.sfv"), "eng.rar", "jpn.rar");
        Touch(Path.Combine(root, "Subs", "eng.rar"));
        Touch(Path.Combine(root, "Subs", "jpn.rar"));

        (CreatorViewModel vm, RecordingSRRCreationService srr) = CreateFileModeVm(createVobsubSrr: true);
        vm.InputPath = mainSfv;
        vm.OutputPath = Path.Combine(TempDir, "out-" + Guid.NewGuid().ToString("N") + ".srr");
        await vm.CreateSRRCommand.ExecuteAsync(null);

        List<string> stored = [.. vm.StoredFiles.Select(f => f.StoredName)];
        Assert.Single(stored, n => n == "Subs/eng.srr");
        Assert.Single(stored, n => n == "Subs/jpn.srr");
        Assert.DoesNotContain("Subs/subs.srr", stored); // never one merged SRR
        Assert.Equal(2, srr.RarCalls.Count);            // two chains → two CreateFromRARAsync calls
    }

    [Fact]
    public async Task AdvancedTab_VobsubScan_OuterOsoTrue_NestedSubtitleSrrHasNoOso()
    {
        string root = Path.Combine(TempDir, "release-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string mainSfv = WriteSfv(Path.Combine(root, "main.sfv"), "main.rar");
        WriteSfv(Path.Combine(root, "Subs", "subs.sfv"), "subs.rar");
        Touch(Path.Combine(root, "Subs", "subs.rar"));

        (CreatorViewModel vm, RecordingSRRCreationService srr) = CreateFileModeVm(createVobsubSrr: true);
        vm.ComputeOSOHashes = true; // outer run enables OSO — must NOT reach the nested subtitle SRR
        vm.InputPath = mainSfv;
        vm.OutputPath = Path.Combine(TempDir, "out-" + Guid.NewGuid().ToString("N") + ".srr");
        await vm.CreateSRRCommand.ExecuteAsync(null);

        SRRCreationOptions nested = Assert.Single(srr.RarCallOptions);
        Assert.False(nested.ComputeOSOHashes); // nested subtitle SRR forces oso off
    }

    // ── 21. Wizard-placeholder path (GenerateNestedSRRFileAsync): multi-chain support stays
    //        deferred (the placeholder model is 1:1), but oso-off IS applied now. ──

    [Fact]
    public async Task WizardPlaceholder_NestedSubtitleSrr_OuterOsoTrue_HasNoOso()
    {
        string root = Path.Combine(TempDir, "release-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string mainSfv = WriteSfv(Path.Combine(root, "main.sfv"), "main.rar");
        string subSfv = WriteSfv(Path.Combine(root, "Subs", "subs.sfv"), "subs.rar");

        (CreatorViewModel vm, RecordingSRRCreationService srr) = CreateFileModeVm(createVobsubSrr: false);
        vm.ComputeOSOHashes = true; // outer run enables OSO
        vm.InputPath = mainSfv;
        vm.OutputPath = Path.Combine(TempDir, "out-" + Guid.NewGuid().ToString("N") + ".srr");
        // A wizard GeneratedNestedSRR placeholder, materialized via GenerateNestedSRRFileAsync.
        vm.StoredFiles.Add(new CreatorViewModel.StoredFileItem
        {
            StoredName = "Subs/subs.srr",
            GenerateFromPath = subSfv,
            Kind = CreatorViewModel.StoredFileKind.GeneratedNestedSRR,
        });

        await vm.CreateSRRCommand.ExecuteAsync(null);

        // Two CreateFromSFVAsync calls: the nested placeholder (subs.sfv) and the outer main SRR.
        int nestedIdx = srr.SfvCalls.IndexOf(subSfv);
        Assert.True(nestedIdx >= 0, "the nested subtitle SRR was materialized via CreateFromSFVAsync");
        Assert.False(srr.SfvCallOptions[nestedIdx].ComputeOSOHashes); // nested forces oso off
        // Sanity: the OUTER main SRR still carries the user's setting (proves the two are distinct).
        int mainIdx = srr.SfvCalls.IndexOf(mainSfv);
        Assert.True(srr.SfvCallOptions[mainIdx].ComputeOSOHashes);
    }

    // ── 22. pyrescene --no-srs parity: folder mode honors AutoCreateSRS, just as file mode already
    //        does. OFF → no sample .srs is staged. ──

    [Fact]
    public async Task Sample_AutoCreateSrsOff_NoSrsStaged_MediaNotStoredEither()
    {
        // With AutoCreateSRS off, folder mode generates no sample SRS artifacts (a sample's ONLY
        // stored output is its .srs — the sample MEDIA itself is never stored), matching pyrescene
        // --no-srs. GenerateSampleArtifactsAsync must not even run.
        string root = Path.Combine(TempDir, "release-" + Guid.NewGuid().ToString("N"));
        string sample = Touch(Path.Combine(root, "Sample", "clip.mkv"));
        var scan = new ReleaseScanResult([], [sample], [], [], [], []);
        (CreatorViewModel vm, RecordingSRSCreationService srs, RecordingSRRCreationService srr, _) = CreateVm(scan);
        vm.AutoCreateSRS = false;

        IReadOnlyList<StoredFileEntry> additionalFiles = await RunCreateAsync(vm, root, srr);

        Assert.DoesNotContain(additionalFiles, e => e.StoredName == "Sample/clip.srs");
        Assert.DoesNotContain(additionalFiles, e => e.StoredName.EndsWith(".srs", StringComparison.OrdinalIgnoreCase));
        Assert.Empty(srs.CallsInOrder); // GenerateSampleArtifactsAsync never invoked the SRS service
    }

    [Fact]
    public async Task Sample_AutoCreateSrsOn_SrsStaged_RegressionGuard()
    {
        // Regression guard: the default (AutoCreateSRS = true) still stages the sample .srs.
        string root = Path.Combine(TempDir, "release-" + Guid.NewGuid().ToString("N"));
        string sample = Touch(Path.Combine(root, "Sample", "clip.mkv"));
        var scan = new ReleaseScanResult([], [sample], [], [], [], []);
        (CreatorViewModel vm, _, RecordingSRRCreationService srr, _) = CreateVm(scan); // AutoCreateSRS = true (default)

        IReadOnlyList<StoredFileEntry> additionalFiles = await RunCreateAsync(vm, root, srr);

        Assert.Contains(additionalFiles, e => e.StoredName == "Sample/clip.srs");
    }

    // ── 23. pyrescene --vobsub-srr parity: folder mode gates ONLY the nested-SRR generation (pass
    //        9) on CreateVobsubSRR; the subtitle-SFV storage (pass 10) always runs, so
    //        scanner-origin AND manually-added subs stay stored even with vobsub off. ──

    [Fact]
    public async Task SubtitleSfv_VobsubSrrOff_NoNestedSrr_ButScannerSfvStillStored()
    {
        // With CreateVobsubSRR off, no nested .srr is produced, but pass 10 still runs so the
        // subtitle SFV itself stays stored — here via the scanner's own pass-10 baseline (the scan
        // already lists it in StoredFiles). Matches pyrescene without --vobsub-srr: it still stores
        // extra_sfvs, only skipping create_srr_for_subs.
        string root = Path.Combine(TempDir, "release-" + Guid.NewGuid().ToString("N"));
        string nfo = Touch(Path.Combine(root, "release.nfo"));
        string subSfv = WriteSfv(Path.Combine(root, "Subs", "subs.sfv"), "subs.rar");
        Touch(Path.Combine(root, "Subs", "subs.rar"));
        var scan = new ReleaseScanResult([], [], [subSfv], [nfo, subSfv], [], []);
        (CreatorViewModel vm, _, RecordingSRRCreationService srr, _) = CreateVm(scan);
        vm.CreateVobsubSRR = false;

        IReadOnlyList<StoredFileEntry> additionalFiles = await RunCreateAsync(vm, root, srr);

        List<string> names = [.. additionalFiles.Select(e => e.StoredName)];
        Assert.DoesNotContain(names, n => n.EndsWith(".srr", StringComparison.OrdinalIgnoreCase));
        Assert.Single(names, n => n == "Subs/subs.sfv"); // pass-10 baseline storage intact, exactly once
        Assert.Empty(srr.RarCalls);                      // pass-9 nested-SRR creation never attempted
    }

    [Fact]
    public async Task SubtitleSfv_VobsubSrrOn_NestedSrrPresent_RegressionGuard()
    {
        // Regression guard: the default (CreateVobsubSRR = true) still generates the nested
        // subtitle SRR before the SFV's own entry.
        string root = Path.Combine(TempDir, "release-" + Guid.NewGuid().ToString("N"));
        string nfo = Touch(Path.Combine(root, "release.nfo"));
        string subSfv = WriteSfv(Path.Combine(root, "Subs", "subs.sfv"), "subs.rar");
        Touch(Path.Combine(root, "Subs", "subs.rar"));
        var scan = new ReleaseScanResult([], [], [subSfv], [nfo, subSfv], [], []);
        (CreatorViewModel vm, _, RecordingSRRCreationService srr, _) = CreateVm(scan); // CreateVobsubSRR = true (default)

        IReadOnlyList<StoredFileEntry> additionalFiles = await RunCreateAsync(vm, root, srr);

        List<string> names = [.. additionalFiles.Select(e => e.StoredName)];
        Assert.Single(names, n => n == "Subs/subs.srr");
        Assert.Single(names, n => n == "Subs/subs.sfv");
    }

    [Fact]
    public async Task ManualSubtitleSfv_VobsubSrrOff_SfvStillStoredExactlyOnce_NoNestedSrr()
    {
        // A MANUALLY-added subtitle SFV never reaches the scanner's pass-10, so its storage happens
        // in GenerateSubtitleArtifactsAsync's own pass 10 — which must stay independent of the
        // CreateVobsubSRR gate. With vobsub off, the SFV is still stored exactly once and no nested
        // .srr is produced.
        string root = Path.Combine(TempDir, "release-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string manualSfv = WriteSfv(Path.Combine(TempDir, "external-subs.sfv"), "external-subs.rar");
        Touch(Path.Combine(TempDir, "external-subs.rar"));
        var scan = new ReleaseScanResult([], [], [], [], [], []);
        (CreatorViewModel vm, _, RecordingSRRCreationService srr, _) = CreateVm(scan);
        vm.CreateVobsubSRR = false;

        vm.InputPath = root;
        await vm.LastFolderScan!;
        vm.ExtraSubtitleSfvFiles.Add(manualSfv); // "Add Subtitle" — never seen by the scanner
        vm.OutputPath = Path.Combine(TempDir, "out-" + Guid.NewGuid().ToString("N") + ".srr");
        await vm.CreateSRRCommand.ExecuteAsync(null);
        IReadOnlyList<StoredFileEntry> additionalFiles = srr.LastAdditionalFiles ?? [];

        List<string> names = [.. additionalFiles.Select(e => e.StoredName)];
        Assert.Single(names, n => n == "Subs/external-subs.sfv");
        Assert.DoesNotContain(names, n => n.EndsWith(".srr", StringComparison.OrdinalIgnoreCase));
        Assert.Empty(srr.RarCalls);
    }

    // ── Phase-local snapshot timing ──────────────────────────

    [Fact]
    public async Task Staging_ReadsSubtitleInputsAfterSampleGeneration_NotUpFront()
    {
        // Staging reads its inputs at DIFFERENT moments: sample inputs when sample generation
        // begins, subtitle inputs only AFTER all sample generation has completed. An extraction
        // that gathers both up front into one inputs record would still produce a valid SRR and
        // would still pass every other test in this file — but a subtitle SFV that arrived while
        // samples were generating would silently stop being stored.
        //
        // The SRS double adds one mid-sample-generation, which is when a scanner result or an
        // "Add Subtitle" click landing during an earlier awaiting phase would arrive.
        string root = Path.Combine(TempDir, "rel-" + Guid.NewGuid().ToString("N"));
        string sample = Path.Combine(root, "Sample", "clip.mkv");
        Directory.CreateDirectory(Path.GetDirectoryName(sample)!);
        File.WriteAllBytes(sample, [1, 2, 3]);

        var scan = new ReleaseScanResult([], [sample], [], [], [], []);
        (CreatorViewModel vm, RecordingSRSCreationService srs, RecordingSRRCreationService srr, _) = CreateVm(scan);

        string lateSfv = WriteSfv(Path.Combine(root, "Subs", "late.sfv"), "sub.rar");
        srs.OnCreate = _ => vm.ExtraSubtitleSfvFiles.Add(lateSfv);

        IReadOnlyList<StoredFileEntry> stored = await RunCreateAsync(vm, root, srr);

        Assert.Contains(stored, e => e.StoredName.EndsWith("late.sfv", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Staging_ReadsCreateVobsubSRRAfterSampleGeneration_NotUpFront()
    {
        // The companion to the test above: the subtitle COLLECTION is not the only late read — the
        // CreateVobsubSRR toggle is read at the same phase boundary. A refactor could snapshot the
        // collection lazily but the toggle up front and still pass the previous test, so the toggle
        // is pinned separately.
        //
        // Starts true (the folder-mode default) and is flipped OFF during sample generation. Pass 9
        // must then generate no nested SRR, while pass 10 still stores the SFV itself — that split
        // is pyrescene's --vobsub-srr parity, which the toggle gates only the nested-SRR half of.
        string root = Path.Combine(TempDir, "rel-" + Guid.NewGuid().ToString("N"));
        string sample = Path.Combine(root, "Sample", "clip.mkv");
        Directory.CreateDirectory(Path.GetDirectoryName(sample)!);
        File.WriteAllBytes(sample, [1, 2, 3]);

        string subSfv = WriteSfv(Path.Combine(root, "Subs", "subs.sfv"), "eng.rar");
        Touch(Path.Combine(root, "Subs", "eng.rar"));

        var scan = new ReleaseScanResult([], [sample], [subSfv], [subSfv], [], []);
        (CreatorViewModel vm, RecordingSRSCreationService srs, RecordingSRRCreationService srr, _) = CreateVm(scan);
        Assert.True(vm.CreateVobsubSRR, "the folder-mode factory must start with the toggle on for this test to mean anything");

        srs.OnCreate = _ => vm.CreateVobsubSRR = false;

        IReadOnlyList<StoredFileEntry> stored = await RunCreateAsync(vm, root, srr);

        List<string> names = [.. stored.Select(e => e.StoredName)];
        Assert.DoesNotContain(names, n => n.EndsWith(".srr", StringComparison.OrdinalIgnoreCase));
        Assert.Empty(srr.RarCalls);
        Assert.Single(names, n => n == "Subs/subs.sfv");
    }
}
