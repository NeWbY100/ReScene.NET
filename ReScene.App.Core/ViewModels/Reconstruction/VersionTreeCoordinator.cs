using System.Collections.ObjectModel;
using ReScene.App.Core.Services;

namespace ReScene.App.Core.ViewModels.Reconstruction;

/// <summary>
/// Owns the WinRAR version tree: scanning the versions folder, reconciling a scan against the coarse
/// major toggles and any pending config selection, and rebuilding the bound group list.
/// </summary>
/// <remarks>
/// <para>
/// The six <c>VersionN</c> bools stay on the view-model, and so does the projection that writes them:
/// <paramref name="syncMajorsFromTree"/> is called at the points the original called
/// <c>SyncMajorsFromTree</c>. That projection interleaves each per-major READ with its own WRITE, and
/// each write can synchronously raise <c>PropertyChanged</c> - so a subscriber that mutates a later
/// major's leaves is seen by the reads that follow. Batching the six reads and then writing them,
/// which an earlier version of this class did, silently breaks that. The coordinator owns WHEN the
/// sync happens; the view-model owns HOW the tree projects onto its properties.
/// </para>
/// <para>
/// <b><see cref="_suppressGroupSync"/> guards the tree→major sync during a programmatic bulk change,
/// and BOTH regions that raise it are load-bearing.</b> In <see cref="RebuildVersionGroups"/> a sync
/// would otherwise run against a partially-built group list; in <see cref="SetAllLeaves"/> it keeps
/// the bulk tick/untick atomic to any subscriber of the tree's own SelectionChanged. Each is pinned
/// separately in <c>ReconstructorViewModelVersionsTests</c>.
/// </para>
/// </remarks>
internal sealed class VersionTreeCoordinator(
    IUiDispatcher uiDispatcher,
    ObservableCollection<RARVersionGroup> versionGroups,
    Func<string> winRarPath,
    Func<bool> hasScannedVersions,
    Action<bool> setHasScannedVersions,
    Action<bool> setShowNoVersionsHint,
    Func<HashSet<int>> readMajors,
    Action syncMajorsFromTree)
{
    private IReadOnlyList<InstalledRARVersion> _lastScan = [];

    /// <summary>Latest-wins guard for overlapping async scans.</summary>
    private int _scanToken;

    private List<int>? _pendingVersionSelection;

    private bool _suppressGroupSync;

    /// <summary>The most recent scan's installed versions. Read by the run's shared-settings build.</summary>
    public IReadOnlyList<InstalledRARVersion> LastScan => _lastScan;

    /// <summary>The most recent folder-scan Task, exposed so tests can await scan completion
    /// deterministically (production is fire-and-forget and marshals results to the UI thread).</summary>
    public Task? LastVersionScan { get; private set; }

    /// <summary>
    /// Invalidates the current tree and starts a scan of the new folder, as ONE ordered operation.
    /// </summary>
    /// <remarks>
    /// The order is load-bearing and the three steps must not be split across the seam. The folder
    /// changed, so the previous folder's scan no longer describes the current path: mark the tree
    /// not-yet-scanned and invalidate any in-flight scan BEFORE kicking off the new one. Otherwise a
    /// config's pending version selection applied right after this (the mapper sets WinRARPath, then
    /// LoadPendingVersionSelection) would be consumed by the reconcile against the STALE previous
    /// scan and lost before the new folder's scan lands, clearing the restored major toggles too.
    /// <para>
    /// The path is NOT passed in: <see cref="TriggerVersionScan"/> reads it itself, at that later
    /// point, and passing the property-changed value would turn a live read into a snapshot.
    /// </para>
    /// </remarks>
    public void InvalidateAndStartScan()
    {
        setHasScannedVersions(false);
        _scanToken++;
        TriggerVersionScan();
    }

    /// <summary>Rescans the current folder (the Rescan command).</summary>
    public void Rescan() => TriggerVersionScan();

    /// <summary>Ticks or unticks every leaf in one bulk operation.</summary>
    public void SetAllLeaves(bool value)
    {
        _suppressGroupSync = true;
        foreach (RARVersionGroup group in versionGroups)
        {
            foreach (RARVersionLeaf leaf in group.Leaves)
            {
                leaf.IsChecked = value;
            }
        }

        _suppressGroupSync = false;
        syncMajorsFromTree();
    }

    /// <summary>Stores a scan result and reconciles the tree. Also the test seam for the async scan.</summary>
    public void ApplyScanResult(IReadOnlyList<InstalledRARVersion> installed, bool folderScanned)
    {
        _lastScan = installed;
        setHasScannedVersions(folderScanned);
        ApplyReconcile();
    }

    /// <summary>Sets the pending explicit selection (config load) and reconciles against the last scan.</summary>
    public void LoadPendingVersionSelection(IReadOnlyList<int>? explicitVersions)
    {
        _pendingVersionSelection = explicitVersions?.ToList();
        ApplyReconcile();
    }

    /// <summary>
    /// Drops any pending config selection and reconciles - the SRR import path, which has just set the
    /// major toggles from the SRR and must not let a stale config selection override them.
    /// </summary>
    public void ClearPendingSelectionAndReconcile()
    {
        _pendingVersionSelection = null;
        ApplyReconcile();
    }

    /// <summary>Kicks off a folder scan: synchronous empty result for an invalid folder (keeps tests
    /// deterministic), otherwise off-thread with a latest-wins token.</summary>
    private void TriggerVersionScan()
    {
        string folder = winRarPath();
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
        {
            // Bump the token so a still-running async scan of a previous folder cannot land later
            // and repopulate the tree (with HasScannedVersions=true) against the now-invalid path.
            _scanToken++;
            ApplyScanResult([], folderScanned: false);
            LastVersionScan = Task.CompletedTask;
            return;
        }

        LastVersionScan = RunVersionScanAsync(folder);
    }

    private async Task RunVersionScanAsync(string folder)
    {
        int token = ++_scanToken;
        IReadOnlyList<InstalledRARVersion> installed;
        try
        {
            installed = await Task.Run(() => WinRARVersionScanner.Scan(folder)).ConfigureAwait(false);
        }
        catch
        {
            installed = [];
        }

        uiDispatcher.Invoke(() =>
        {
            if (token != _scanToken)
            {
                return;
            }

            ApplyScanResult(installed, folderScanned: installed.Count > 0 || Directory.Exists(folder));
        });
    }

    private void ApplyReconcile()
    {
        HashSet<int> enabledMajors = readMajors();
        HashSet<int> ticked = VersionSelectionReconciler.ComputeTicked(_lastScan, _pendingVersionSelection, enabledMajors);

        // The pending explicit selection is consumed only once a real scan has materialised the tree.
        if (_pendingVersionSelection is not null && hasScannedVersions())
        {
            _pendingVersionSelection = null;
        }

        RebuildVersionGroups(_lastScan, ticked);
        syncMajorsFromTree();
        setShowNoVersionsHint(versionGroups.Count == 0);
    }

    private void RebuildVersionGroups(IReadOnlyList<InstalledRARVersion> installed, HashSet<int> ticked)
    {
        _suppressGroupSync = true;
        foreach (RARVersionGroup group in versionGroups)
        {
            group.SelectionChanged -= OnGroupSelectionChanged;
            group.Detach();
        }

        versionGroups.Clear();
        foreach (IGrouping<int, InstalledRARVersion> majorGroup in installed.GroupBy(v => v.Version / 100).OrderBy(g => g.Key))
        {
            List<RARVersionLeaf> leaves = [.. majorGroup
                .OrderBy(v => v.Version)
                .Select(v => new RARVersionLeaf(v.Version, v.FolderName, v.Tag) { IsChecked = ticked.Contains(v.Version) })];
            RARVersionGroup group = new(majorGroup.Key, leaves);
            group.SelectionChanged += OnGroupSelectionChanged;
            versionGroups.Add(group);
        }

        _suppressGroupSync = false;
    }

    private void OnGroupSelectionChanged(object? sender, EventArgs e)
    {
        if (_suppressGroupSync)
        {
            return;
        }

        syncMajorsFromTree();
    }

}
