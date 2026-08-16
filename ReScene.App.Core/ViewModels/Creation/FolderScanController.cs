using System.Collections.ObjectModel;
using ReScene.App.Core.Models;
using ReScene.App.Core.Services;

namespace ReScene.App.Core.ViewModels.Creation;

/// <summary>
/// Owns the Advanced tab's folder-mode lifecycle: starting a background release scan when
/// <c>InputPath</c> becomes a directory, discarding superseded results, applying a completed scan to
/// the view-model's collections, and tearing the mode down again.
/// </summary>
/// <remarks>
/// <para>
/// This is the seam that drags the most view-model back with it. The scan's result is not a value
/// the view-model then applies — applying it IS the work, and it writes eight different pieces of
/// view-model state. Those writes stay writes, through the <see cref="Hooks"/> callbacks, rather
/// than being restructured into a returned result object: the ordering between them is load-bearing
/// (see <see cref="Hooks.NotifyCanExecuteChanged"/>) and a returned-result design would have to
/// re-establish it at every call site.
/// </para>
/// <para>
/// The four <see cref="ObservableCollection{T}"/>s are held BY REFERENCE, not copied — they are the
/// view-model's own bound collections, and the DataGrids are already bound to those instances.
/// </para>
/// </remarks>
internal sealed class FolderScanController(
    IReleaseScanner releaseScanner,
    IUiDispatcher uiDispatcher,
    ObservableCollection<ReleaseSetInput> detectedSets,
    ObservableCollection<CreatorViewModel.StoredFileItem> storedFiles,
    ObservableCollection<string> extraSampleFiles,
    ObservableCollection<string> extraSubtitleSfvFiles,
    FolderScanController.Hooks hooks)
{
    /// <summary>
    /// The view-model state this controller writes. Grouped into a record rather than passed as
    /// twelve positional constructor arguments so the single construction site reads as named
    /// assignments; the members are individually documented because several are ordering-sensitive.
    /// </summary>
    /// <param name="SetIsScanning">
    /// Assigns <c>IsScanning</c>. Must be settable SYNCHRONOUSLY from <see cref="ExitFolderMode"/> —
    /// a discarded scan will never clear it, and deferring it strands the UI on "Scanning…" with
    /// Create disabled.
    /// </param>
    /// <param name="SetInputStatus">Assigns <c>InputStatus</c> — also the busy announcement's live region.</param>
    /// <param name="SetOutputStatus">Assigns <c>OutputStatus</c>.</param>
    /// <param name="TrySetAutoOutputPath">
    /// The view-model's shared auto-vs-user output-path rule. Stays on the view-model because file
    /// mode uses it too; this controller only calls the folder-mode half.
    /// </param>
    /// <param name="NotifyCanExecuteChanged">
    /// Raises <c>CreateSRRCommand.NotifyCanExecuteChanged()</c>. Ordering-sensitive:
    /// <c>IsMusicOnly</c> and <c>IsInvalid</c> have no <c>[NotifyCanExecuteChangedFor]</c> backing,
    /// and the <c>IsScanning</c> notification that fires just before them reports the gate as it was
    /// BEFORE they were assigned. So on the fault, root-error and success paths this call is the only
    /// notification carrying the final answer — drop it and a bound view keeps whatever the stale
    /// one said. Its position after both flags are final is what matters, not the call count.
    /// The drive-root call in <see cref="Start"/> is the exception: <c>Start</c> only runs inside
    /// <c>OnInputPathChanged</c>, and <c>InputPath</c>'s own generated notification follows the
    /// partial hook, so there the final state is announced either way. It is kept for symmetry.
    /// </param>
    /// <param name="NotifyFolderModeChanged">
    /// Raises <c>OnPropertyChanged(nameof(IsFolderMode))</c>. <c>IsFolderMode</c> is a plain
    /// computed property over this controller's state, so nothing raises it automatically.
    /// </param>
    /// <param name="ClearStoredFileSelection">
    /// Nulls <c>SelectedStoredFile</c>. One hook per selection, rather than a single call that nulls
    /// all three, so <see cref="ClearFolderScanResults"/> can keep interleaving each reset
    /// immediately after its own collection's clear exactly as the original did — see that method's
    /// remarks for why the order is worth preserving.
    /// </param>
    /// <param name="ClearExtraSampleSelection">Nulls <c>SelectedExtraSample</c>.</param>
    /// <param name="ClearExtraSubtitleSelection">Nulls <c>SelectedExtraSubtitle</c>.</param>
    /// <param name="UpdateActionHint">Recomputes the hint under the primary button.</param>
    /// <param name="DetectedSetsSummary">
    /// Reads the view-model's <c>DetectedSetsSummary</c>. A live accessor, not a value: the summary
    /// is derived from the detected-sets collection this controller has just repopulated.
    /// </param>
    /// <param name="AppendLog">Appends a line to the view-model's log.</param>
    internal sealed record Hooks(
        Action<bool> SetIsScanning,
        Action<FieldStatus> SetInputStatus,
        Action<FieldStatus> SetOutputStatus,
        Action<string> TrySetAutoOutputPath,
        Action NotifyCanExecuteChanged,
        Action NotifyFolderModeChanged,
        Action ClearStoredFileSelection,
        Action ClearExtraSampleSelection,
        Action ClearExtraSubtitleSelection,
        Action UpdateActionHint,
        Func<string> DetectedSetsSummary,
        Action<string> AppendLog);

    // Mirrors InspectorViewModel's generation-guard house pattern: every InputPath change bumps the
    // scan generation and cancels the in-flight source, so a scan whose generation is no longer
    // current is discarded on the UI thread even if it had already finished before the cancellation
    // was seen. FolderScanSession owns that discipline.
    private readonly FolderScanSession _scan = new();

    private bool _isFolderMode;
    private bool _isMusicOnlyFolder;
    private bool _folderScanInvalid;
    private string? _releaseRoot;

    /// <summary>Whether the Advanced tab is currently in folder mode.</summary>
    public bool IsFolderMode => _isFolderMode;

    /// <summary>Whether the last scan found only music SFVs — gates Create.</summary>
    public bool IsMusicOnly => _isMusicOnlyFolder;

    /// <summary>
    /// Whether the current folder-mode state has nothing creatable: the input itself is invalid (a
    /// filesystem root) or the scanner couldn't enumerate the root at all. Gates Create so an
    /// empty/header-only SRR can't be built from a scan that never actually looked at the release.
    /// </summary>
    public bool IsInvalid => _folderScanInvalid;

    /// <summary>The release root of the most recent scan, or <see langword="null"/> outside folder mode.</summary>
    public string? ReleaseRoot => _releaseRoot;

    /// <summary>
    /// The most recent folder-scan Task, exposed so tests can await scan completion deterministically
    /// (production is fire-and-forget and marshals results to the UI thread).
    /// </summary>
    public Task? LastScan { get; private set; }

    /// <summary>
    /// Invalidates any in-flight scan without otherwise changing state: bumps the generation and
    /// cancels+disposes the source, so a stale completion is discarded rather than overwriting newer
    /// state. Called unconditionally at the top of every input change, before the branch that decides
    /// whether the new input starts a scan, leaves folder mode, or neither.
    /// </summary>
    public void InvalidateInFlight()
    {
        _scan.BumpGeneration();
        _scan.CancelInFlight();
    }

    /// <summary>
    /// Clears the controller's own folder-mode state for a full view-model reset. Does NOT clear the
    /// collections: <c>CreatorViewModel.Reset</c> clears those itself, in its own order, along with
    /// the file-mode state it also resets.
    /// </summary>
    public void Reset()
    {
        InvalidateInFlight();
        _isFolderMode = false;
        hooks.NotifyFolderModeChanged();
        _isMusicOnlyFolder = false;
        _folderScanInvalid = false;
        _releaseRoot = null;
    }

    /// <summary>
    /// Leaves folder mode when InputPath changes to a file/blank/nonexistent path: resets the
    /// folder-only state so a stale detected-set list or a music-only gate can't linger into file
    /// mode. The in-flight scan (if any) was already cancelled by <see cref="InvalidateInFlight"/>;
    /// since its completion will be discarded by the generation check in
    /// <see cref="ApplyFolderScanResult"/>, <c>IsScanning</c> must be cleared here synchronously —
    /// nothing else will do it.
    /// </summary>
    public void ExitFolderMode()
    {
        _isFolderMode = false;
        hooks.NotifyFolderModeChanged();
        _isMusicOnlyFolder = false;
        _folderScanInvalid = false;
        _releaseRoot = null;
        hooks.SetIsScanning(false);
        ClearFolderScanResults();
    }

    /// <summary>
    /// Empties the four scan-populated collections and their selections, interleaved: each selection
    /// is reset immediately after its own collection's clear.
    /// </summary>
    /// <remarks>
    /// The interleaving is deliberate, and reproduces the original exactly. Clearing all four
    /// collections first and then resetting the three selections would reach the same end state, but
    /// it is NOT unobservable: the selection properties are <c>[ObservableProperty]</c>, so each
    /// setter raises its own <c>PropertyChanged</c>, and the collections and selections are two-way
    /// bound to a DataGrid and two ListBoxes that can write a null selection back through the
    /// binding when their source collection empties. Changing the interleaving changes the event
    /// sequence those bindings see.
    /// </remarks>
    private void ClearFolderScanResults()
    {
        detectedSets.Clear();
        storedFiles.Clear();
        hooks.ClearStoredFileSelection();
        extraSampleFiles.Clear();
        hooks.ClearExtraSampleSelection();
        extraSubtitleSfvFiles.Clear();
        hooks.ClearExtraSubtitleSelection();
    }

    /// <summary>
    /// Kicks off a background release scan of <paramref name="releaseRoot"/>. A filesystem root
    /// (e.g. "C:\") is rejected synchronously without scanning — the scanner walks the tree
    /// recursively, so scanning an entire drive would be both meaningless (no name to derive an SRR
    /// filename from) and dangerously slow.
    /// </summary>
    /// <remarks>
    /// PRECONDITION: <see cref="InvalidateInFlight"/> must have been called first. This installs a
    /// fresh session source without cancelling an existing one, so calling it twice in a row would
    /// abandon a live scan rather than cancel it. <c>OnInputPathChanged</c> satisfies this by
    /// invalidating unconditionally before it branches.
    /// </remarks>
    public void Start(string releaseRoot)
    {
        _isFolderMode = true;
        hooks.NotifyFolderModeChanged();
        _releaseRoot = releaseRoot;

        if (CreatorArtifactNaming.IsFilesystemRoot(releaseRoot))
        {
            // Never scanned — Create must not be able to build an empty/header-only SRR from an
            // input that was rejected outright. Reset music-only too: a prior scan's gate must not
            // linger once the input itself becomes invalid.
            hooks.SetIsScanning(false);
            _isMusicOnlyFolder = false;
            _folderScanInvalid = true;
            ClearFolderScanResults();
            hooks.SetInputStatus(FieldStatus.Error("This is a drive root, not a release folder — choose the folder containing the release's files."));
            hooks.SetOutputStatus(FieldStatus.Error("Choose a release folder, not a drive root — there's no name to base the SRR on."));
            hooks.UpdateActionHint();
            hooks.NotifyCanExecuteChanged();
            return;
        }

        hooks.SetIsScanning(true);

        // Busy announcement: reuse the existing InputStatus + its FieldStatusLine live region for a
        // single announced busy→result transition. ApplyFolderScanResult (or the root-error paths
        // above) overwrites this with the Ok(summary)/Error(...) result on completion — no second
        // status line, so a screen reader isn't double-announced.
        hooks.SetInputStatus(FieldStatus.Info("Scanning release folder…"));

        // Begin() installs the new source and hands back the token captured as a VALUE, while the
        // source is certainly not yet disposed — the background delegate below must read `token`
        // and never `cts.Token` again (see FolderScanSession.Begin's remarks, and the
        // RapidInputSwitching_WithoutAwaiting_NeverThrows test).
        (int generation, CancellationTokenSource cts, CancellationToken token) = _scan.Begin();

        LastScan = RunFolderScanAsync(releaseRoot, generation, cts, token);
    }

    private async Task RunFolderScanAsync(string releaseRoot, int generation, CancellationTokenSource cts, CancellationToken token)
    {
        try
        {
            ReleaseScanResult result = await Task.Run(
                () => releaseScanner.Scan(releaseRoot, token), token).ConfigureAwait(false);

            uiDispatcher.Post(() =>
            {
                // Every scan-session read/write below happens inside this Post callback — which, like
                // every other UI-thread-invoked entry point (a property-changed hook, Reset), is
                // serialized onto the UI thread — never on the background thread that ran the
                // scan. That's what keeps TryComplete's identity check race-free against a cancel:
                // the two either run one-at-a-time on the same thread, or (in a real app) are
                // serialized by the dispatcher itself.
                //
                // TryComplete performs the identity check AND the cleanup as one operation. A false
                // return means superseded: the newer input change already cancelled, disposed and
                // cleared `cts` itself, so this is a hard bail, not just a "don't apply the result"
                // check — touching it again would resurrect a reference someone else tore down.
                if (!_scan.TryComplete(generation, cts))
                {
                    return;
                }

                ApplyFolderScanResult(releaseRoot, result);
            });
        }
        catch (OperationCanceledException)
        {
            // Superseded — the newer input change already cancelled, disposed, and null'd out `cts`
            // itself (see FolderScanSession.CancelInFlight); there's nothing left for us to do here.
        }
        catch (Exception ex)
        {
            // The scan faulted with an UNEXPECTED (non-OCE) exception — the scan and
            // RarProofInspector.Inspect both catch only IOException/UnauthorizedAccessException, so
            // an ArgumentException/NotSupportedException/SecurityException from a FileStream or a
            // RAR-parser fault escapes here. Without this catch the background Task faults, the
            // success Post never runs, and IsScanning + InputStatus stay stranded on the busy
            // "Scanning release folder…" state (Create disabled, the live region stuck announcing
            // busy) until the user re-inputs. Post the SAME session-gated UI-thread
            // continuation the success completion uses — every session read/write stays on the UI
            // thread, preserving the CTS-lifecycle invariants — then fail closed EXACTLY like
            // ApplyFolderScanResult's root-enumeration (IsRootError) branch: clear IsScanning, gate
            // Create, and surface the failure, so a faulted scan can never leave an empty/header-only
            // SRR buildable. Cancellation stays silent (handled above).
            uiDispatcher.Post(() =>
            {
                if (!_scan.TryComplete(generation, cts))
                {
                    return;
                }

                hooks.SetIsScanning(false);
                _isMusicOnlyFolder = false;
                _folderScanInvalid = true;
                ClearFolderScanResults();
                hooks.SetInputStatus(FieldStatus.Error($"Could not scan the folder: {ex.Message}"));
                hooks.NotifyCanExecuteChanged();
                hooks.UpdateActionHint();
            });
        }
    }

    /// <summary>
    /// Applies a completed, still-current folder scan: populates the detected sets, the stored files
    /// (StoredName = root-relative path), the extra samples and the extra subtitle SFVs; sets the
    /// input status summary (or a music-only error, which also gates Create); logs every warning in
    /// order (the status line shows only a count/preview); and auto-fills the output path when it is
    /// still blank or auto-generated.
    /// </summary>
    private void ApplyFolderScanResult(string releaseRoot, ReleaseScanResult result)
    {
        hooks.SetIsScanning(false);
        _releaseRoot = releaseRoot;

        if (CreatorArtifactNaming.IsRootError(result))
        {
            // The scanner couldn't enumerate the root at all (e.g. permission denied) — surface the
            // failure and gate Create, rather than the previous fail-open behavior of treating the
            // resulting empty collections as an ordinary (successful, Ok-status) empty scan, which
            // let Create build an empty/header-only SRR from a root that was never actually read.
            _isMusicOnlyFolder = false;
            _folderScanInvalid = true;
            ClearFolderScanResults();
            hooks.SetInputStatus(FieldStatus.Error(result.Warnings[0]));
            hooks.NotifyCanExecuteChanged();
            hooks.UpdateActionHint();
            return;
        }

        // A successful scan clears any earlier invalid/error state — Create is re-enabled once the
        // input points at something the scanner could actually read.
        _folderScanInvalid = false;

        detectedSets.Clear();
        foreach (ReleaseSetInput set in result.MainSets)
        {
            detectedSets.Add(set);
        }

        storedFiles.Clear();
        foreach (string path in result.StoredFiles)
        {
            storedFiles.Add(new CreatorViewModel.StoredFileItem
            {
                FullPath = path,
                StoredName = CreatorArtifactNaming.RootRelativeName(releaseRoot, path),
            });
        }

        extraSampleFiles.Clear();
        foreach (string sample in result.SampleFiles)
        {
            extraSampleFiles.Add(sample);
        }

        extraSubtitleSfvFiles.Clear();
        foreach (string sfv in result.SubtitleSfvs)
        {
            extraSubtitleSfvFiles.Add(sfv);
        }

        // [DIVERGENCE: Spec 2] the scanner routes rescue-fallback music SFVs to MusicSfvs instead of
        // admitting them as ordinary main sets; a folder holding only music has no supported output
        // yet, so Create is gated off with an explanatory error rather than silently building an
        // empty (or wrong) SRR.
        _isMusicOnlyFolder = result.MusicSfvs.Count > 0 && result.MainSets.Count == 0;

        if (_isMusicOnlyFolder)
        {
            hooks.SetInputStatus(FieldStatus.Error("Music release — folder scan support arrives in a later update."));
        }
        else
        {
            // Reuse DetectedSetsSummary for the set-count segment so the status line's grammar
            // ("No RAR sets"/"1 RAR set"/"{n} RAR sets") matches the detected-sets list's own
            // automation Name — the detected sets were populated from result.MainSets just above, so
            // the two counts are identical here. (The sample/stored-file "(s)" segments are left as-is.)
            string summary = $"{hooks.DetectedSetsSummary()} · {result.SampleFiles.Count} sample(s) · {result.StoredFiles.Count} stored file(s)";
            if (result.Warnings.Count > 0)
            {
                summary += $" · {result.Warnings.Count} warning(s): {result.Warnings[0]}";
            }

            hooks.SetInputStatus(FieldStatus.Ok(summary));
        }

        foreach (string warning in result.Warnings)
        {
            hooks.AppendLog($"WARNING: {warning}");
        }

        AutoSetFolderOutputPath(releaseRoot);
        hooks.NotifyCanExecuteChanged();
        hooks.UpdateActionHint();
    }

    /// <summary>
    /// Auto-fills the output path from the release root when it is still blank or holds a previously
    /// auto-generated value — never a user-typed or user-picked one.
    /// <paramref name="releaseRoot"/> is never a filesystem root here: <see cref="Start"/> rejects
    /// that case before a scan ever runs.
    /// </summary>
    private void AutoSetFolderOutputPath(string releaseRoot)
    {
        string trimmedRoot = Path.TrimEndingDirectorySeparator(releaseRoot);
        string? parent = Path.GetDirectoryName(trimmedRoot);
        string rootName = Path.GetFileName(trimmedRoot);
        if (string.IsNullOrEmpty(parent) || string.IsNullOrEmpty(rootName))
        {
            return;
        }

        hooks.TrySetAutoOutputPath(Path.Combine(parent, rootName + ".srr"));
    }
}
