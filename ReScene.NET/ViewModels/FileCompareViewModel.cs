using System.Collections.ObjectModel;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ReScene.Core.Comparison;
using ReScene.Hex;
using ReScene.NET.Helpers;
using ReScene.NET.Services;
using ReScene.NET.ViewModels.Comparison;
using ReScene.RAR;
using ReScene.SRR;
using ReScene.SRS;

namespace ReScene.NET.ViewModels;

/// <summary>
/// Identifies the kind of node in the file comparison tree view.
/// </summary>
public enum CompareNodeType
{
    /// <summary>
    /// Root node of the comparison tree.
    /// </summary>
    Root,

    /// <summary>
    /// Archive-level information node.
    /// </summary>
    ArchiveInfo,

    /// <summary>
    /// Container node for RAR volume entries.
    /// </summary>
    RARVolumes,

    /// <summary>
    /// Individual RAR volume entry.
    /// </summary>
    RARVolume,

    /// <summary>
    /// Container node for stored file entries.
    /// </summary>
    StoredFiles,

    /// <summary>
    /// Individual stored file entry.
    /// </summary>
    StoredFile,

    /// <summary>
    /// Container node for archived file entries.
    /// </summary>
    ArchivedFiles,

    /// <summary>
    /// Individual archived file entry.
    /// </summary>
    ArchivedFile,

    /// <summary>
    /// Container node for OSO hash entries.
    /// </summary>
    OSOHashes,

    /// <summary>
    /// Individual OSO hash entry.
    /// </summary>
    OSOHash,

    /// <summary>
    /// Detailed RAR block header entry.
    /// </summary>
    DetailedBlock,

    /// <summary>
    /// SRS file-level information node.
    /// </summary>
    SRSFileInfo,

    /// <summary>
    /// SRS track data entry.
    /// </summary>
    SRSTrack,

    /// <summary>
    /// Container node for SRS container chunk entries.
    /// </summary>
    SRSContainerChunks,

    /// <summary>
    /// Individual EBML element node in an MKV comparison tree.
    /// </summary>
    MKVElement
}

/// <summary>
/// Data attached to each tree node in the comparison view, identifying its type and associated block data.
/// </summary>
public class CompareNodeData
{
    /// <summary>
    /// Gets or sets the type of tree node this data represents.
    /// </summary>
    public CompareNodeType NodeType
    {
        get; set;
    }

    /// <summary>
    /// Gets or sets the underlying block or file data associated with this node.
    /// </summary>
    public object? Data
    {
        get; set;
    }

    /// <summary>
    /// Gets or sets the file name associated with this node, if applicable.
    /// </summary>
    public string? FileName
    {
        get; set;
    }

    /// <summary>
    /// Gets or sets whether this node belongs to the left comparison pane.
    /// </summary>
    public bool IsLeft
    {
        get; set;
    }
}

/// <summary>
/// ViewModel for the file comparison tab, supporting side-by-side diff of SRR, SRS, and RAR files.
/// </summary>
public partial class FileCompareViewModel(IFileCompareService compareService, IFileDialogService fileDialog, IHexDiffComputer diffComputer, IUiDispatcher? uiDispatcher = null) : ViewModelBase, IDisposable
{
    private readonly IFileCompareService _compareService = compareService;
    private readonly IFileDialogService _fileDialog = fileDialog;
    private readonly IHexDiffComputer _diffComputer = diffComputer;
    private readonly IUiDispatcher _uiDispatcher = uiDispatcher ?? new WpfDispatcher();

    // Internal per-side state. Reassigned by reference on Swap; reset on Close/reload.
    private ComparePane _left = new();
    private ComparePane _right = new();
    private CompareResult? _compareResult;

    private ComparePane Pane(bool isLeft) => isLeft ? _left : _right;

    // Diff state — CTS lifecycle is owned by RunDiffAsync's finally block.
#pragma warning disable CA2213
    private CancellationTokenSource? _diffCts;
#pragma warning restore CA2213
    private bool _diffScheduled;

    // File paths
    [ObservableProperty]
    public partial string LeftFilePath { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string RightFilePath { get; set; } = string.Empty;

    // Tree views
    public ObservableCollection<TreeNodeViewModel> LeftTreeRoots { get; } = [];
    public ObservableCollection<TreeNodeViewModel> RightTreeRoots { get; } = [];

    // Kept as field-style [ObservableProperty]: the backing field is passed by ref to
    // SyncTreeSelection to update the opposite tree without re-triggering the changed hook.
    // The C# 14 'field' keyword cannot be passed by ref, so a partial property won't work here.
    [ObservableProperty]
    private TreeNodeViewModel? _selectedLeftTreeNode;

    [ObservableProperty]
    private TreeNodeViewModel? _selectedRightTreeNode;

    // Properties
    public ObservableCollection<PropertyItem> LeftProperties { get; } = [];
    public ObservableCollection<PropertyItem> RightProperties { get; } = [];

    [ObservableProperty]
    public partial PropertyItem? SelectedLeftProperty { get; set; }

    [ObservableProperty]
    public partial PropertyItem? SelectedRightProperty { get; set; }

    // Hex view - left
    [ObservableProperty]
    public partial IHexDataSource? LeftHexDataSource { get; set; }

    [ObservableProperty]
    public partial long LeftHexBlockOffset { get; set; }

    [ObservableProperty]
    public partial long LeftHexBlockLength { get; set; }

    [ObservableProperty]
    public partial long LeftHexSelectionOffset { get; set; } = -1;

    [ObservableProperty]
    public partial long LeftHexSelectionLength { get; set; }

    // Hex view - right
    [ObservableProperty]
    public partial IHexDataSource? RightHexDataSource { get; set; }

    [ObservableProperty]
    public partial long RightHexBlockOffset { get; set; }

    [ObservableProperty]
    public partial long RightHexBlockLength { get; set; }

    [ObservableProperty]
    public partial long RightHexSelectionOffset { get; set; } = -1;

    [ObservableProperty]
    public partial long RightHexSelectionLength { get; set; }

    // Hex view - diff ranges
    [ObservableProperty]
    public partial IReadOnlyList<HexMatchRange>? LeftDiffRanges { get; set; }

    [ObservableProperty]
    public partial IReadOnlyList<HexMatchRange>? RightDiffRanges { get; set; }

    // Status
    [ObservableProperty]
    public partial string StatusMessage { get; set; } = "Load files on both sides to compare.";

    [ObservableProperty]
    public partial int HexBytesPerLine { get; set; } = 16;

    [ObservableProperty]
    public partial bool ShowHexView { get; set; } = true;

    [ObservableProperty]
    public partial string DiffSummary { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool HasDiffSummary { get; set; }

    [ObservableProperty]
    public partial bool FilesIdentical { get; set; }

    #region Commands

    [RelayCommand]
    private async Task BrowseLeftAsync()
    {
        string? path = await _fileDialog.OpenFileAsync("Open Left File",
            FileDialogFilters.CompareFiles);

        if (path is not null)
        {
            LoadLeftFile(path);
        }
    }

    [RelayCommand]
    private async Task BrowseRightAsync()
    {
        string? path = await _fileDialog.OpenFileAsync("Open Right File",
            FileDialogFilters.CompareFiles);

        if (path is not null)
        {
            LoadRightFile(path);
        }
    }

    [RelayCommand]
    private void CloseLeft() => ClosePane(true);

    [RelayCommand]
    private void CloseRight() => ClosePane(false);

    private void ClosePane(bool isLeft)
    {
        CancelDiff();
        // Clear the bound hex source before disposing the mapping it wraps.
        if (isLeft)
        {
            LeftHexDataSource = null;
        }
        else
        {
            RightHexDataSource = null;
        }

        Pane(isLeft).DisposeAndReset();

        if (isLeft)
        {
            LeftFilePath = string.Empty;
        }
        else
        {
            RightFilePath = string.Empty;
        }

        LeftDiffRanges = null;
        RightDiffRanges = null;
        RefreshComparison();
    }

    [RelayCommand]
    private void Swap()
    {
        CancelDiff();
        (_left, _right) = (_right, _left);

        (LeftFilePath, RightFilePath) = (RightFilePath, LeftFilePath);

        LeftHexDataSource = null;
        RightHexDataSource = null;
        LeftDiffRanges = null;
        RightDiffRanges = null;

        RefreshComparison();
    }

    #endregion

    #region File Loading

    /// <summary>
    /// Loads and parses a file into the left comparison pane.
    /// </summary>
    /// <param name="filePath">
    /// Absolute path to the file.
    /// </param>
    public void LoadLeftFile(string filePath) => LoadFile(true, filePath);

    /// <summary>
    /// Loads and parses a file into the right comparison pane.
    /// </summary>
    /// <param name="filePath">
    /// Absolute path to the file.
    /// </param>
    public void LoadRightFile(string filePath) => LoadFile(false, filePath);

    private void LoadFile(bool isLeft, string filePath)
    {
        ComparePane pane = Pane(isLeft);
        try
        {
            CancelDiff();
            LeftDiffRanges = null;
            RightDiffRanges = null;
            // Clear the binding before disposing — a pending render would otherwise
            // hit the disposed MemoryMappedDataSource via the HexDataSourceSlice.
            if (isLeft)
            {
                LeftHexDataSource = null;
            }
            else
            {
                RightHexDataSource = null;
            }

            pane.Source?.Dispose();
            pane.Source = null;

            if (isLeft)
            {
                LeftFilePath = filePath;
            }
            else
            {
                RightFilePath = filePath;
            }

            pane.Path = filePath;
            pane.FileSize = new FileInfo(filePath).Length;
            pane.Data = _compareService.LoadFileData(filePath);
            pane.Blocks = _compareService.ParseDetailedBlocks(filePath);
            pane.Source = new MemoryMappedDataSource(filePath);
            RefreshComparison();
        }
        catch (Exception ex)
        {
            if (isLeft)
            {
                LeftFilePath = string.Empty;
            }
            else
            {
                RightFilePath = string.Empty;
            }

            pane.DisposeAndReset();

            if (isLeft)
            {
                LeftHexDataSource = null;
            }
            else
            {
                RightHexDataSource = null;
            }

            StatusMessage = $"Error loading {(isLeft ? "left" : "right")} file: {ex.Message}";
        }
    }

    #endregion

    #region Comparison

    private void RefreshComparison()
    {
        LeftTreeRoots.Clear();
        RightTreeRoots.Clear();
        LeftProperties.Clear();
        RightProperties.Clear();

        if (_left.Data is not null)
        {
            PopulateTree(LeftTreeRoots, _left.Data, true);
        }

        if (_right.Data is not null)
        {
            PopulateTree(RightTreeRoots, _right.Data, false);
        }

        if (_left.Data is not null && _right.Data is not null)
        {
            _compareResult = _compareService.Compare(_left.Data, _right.Data,
                _left.Blocks, _right.Blocks,
                _left.Source, _right.Source);
            CompareHighlighter.Apply(_compareResult, LeftTreeRoots, RightTreeRoots,
                _left.Blocks, _right.Blocks,
                _left.Source, _right.Source);
            UpdateStatus();
        }
        else
        {
            _compareResult = null;
            HasDiffSummary = false;
            FilesIdentical = false;
            StatusMessage = "Load files on both sides to compare.";
        }
    }

    private void UpdateStatus()
    {
        if (_compareResult is null)
        {
            StatusMessage = "Load files on both sides to compare.";
            HasDiffSummary = false;
            FilesIdentical = false;
            return;
        }

        int archiveDiffs = _compareResult.ArchiveDifferences.Count;
        int added = _compareResult.FileDifferences.Count(d => d.Type == DifferenceType.Added);
        int removed = _compareResult.FileDifferences.Count(d => d.Type == DifferenceType.Removed);
        int modified = _compareResult.FileDifferences.Count(d => d.Type == DifferenceType.Modified);
        int storedAdded = _compareResult.StoredFileDifferences.Count(d => d.Type == DifferenceType.Added);
        int storedRemoved = _compareResult.StoredFileDifferences.Count(d => d.Type == DifferenceType.Removed);

        int totalDiffs = archiveDiffs + added + removed + modified + storedAdded + storedRemoved;

        if (totalDiffs == 0)
        {
            int byteDiffRanges = (LeftDiffRanges?.Count ?? 0) + (RightDiffRanges?.Count ?? 0);
            if (byteDiffRanges > 0)
            {
                StatusMessage = "Byte-level differences detected in current hex view but no structural differences found.";
                DiffSummary = "Byte-level differences detected in current hex view.";
                HasDiffSummary = true;
                FilesIdentical = false;
            }
            else
            {
                StatusMessage = "Files are identical.";
                DiffSummary = "No differences found - files are identical.";
                HasDiffSummary = true;
                FilesIdentical = true;
            }
        }
        else
        {
            var parts = new List<string>();
            if (archiveDiffs > 0)
            {
                parts.Add($"{archiveDiffs} archive property change(s)");
            }

            if (added > 0)
            {
                parts.Add($"{added} file(s) added");
            }

            if (removed > 0)
            {
                parts.Add($"{removed} file(s) removed");
            }

            if (modified > 0)
            {
                parts.Add($"{modified} file(s) modified");
            }

            if (storedAdded > 0)
            {
                parts.Add($"{storedAdded} stored file(s) added");
            }

            if (storedRemoved > 0)
            {
                parts.Add($"{storedRemoved} stored file(s) removed");
            }

            StatusMessage = $"{totalDiffs} difference(s) found: {string.Join(", ", parts)}";
            DiffSummary = $"{totalDiffs} difference(s): {string.Join(" | ", parts)}";
            HasDiffSummary = true;
            FilesIdentical = false;
        }
    }

    #endregion

    #region Tree Selection Handlers

    partial void OnSelectedLeftTreeNodeChanged(TreeNodeViewModel? value)
    {
        if (value?.Tag is CompareNodeData nodeData)
        {
            ShowProperties(nodeData, LeftProperties, true);
            HighlightBlock(nodeData, true);
            SyncTreeSelection(RightTreeRoots, nodeData, ref _selectedRightTreeNode);
        }
    }

    partial void OnSelectedRightTreeNodeChanged(TreeNodeViewModel? value)
    {
        if (value?.Tag is CompareNodeData nodeData)
        {
            ShowProperties(nodeData, RightProperties, false);
            HighlightBlock(nodeData, false);
            SyncTreeSelection(LeftTreeRoots, nodeData, ref _selectedLeftTreeNode);
        }
    }

    private void HighlightBlock(CompareNodeData nodeData, bool isLeft)
    {
        long offset;
        long length;

        if (nodeData.NodeType == CompareNodeType.DetailedBlock && nodeData.Data is RARDetailedBlock block)
        {
            offset = block.StartOffset;
            long fileSize = Pane(isLeft).FileSize;
            length = Math.Max(0, Math.Min(block.StartOffset + block.TotalSize, fileSize) - offset);
        }
        else if (nodeData.Data is SRSFileDataBlock fd)
        {
            offset = fd.BlockPosition;
            length = fd.BlockSize;
        }
        else if (nodeData.Data is SRSTrackDataBlock track)
        {
            offset = track.BlockPosition;
            length = track.BlockSize;
        }
        else if (nodeData.Data is SRSContainerChunk chunk)
        {
            offset = chunk.BlockPosition;
            length = chunk.BlockSize;
        }
        else if (nodeData.Data is SRROsoHashBlock oso)
        {
            offset = oso.BlockPosition;
            length = oso.HeaderSize;
        }
        else if (nodeData.Data is EBMLElement el)
        {
            offset = el.Position;
            long fileSize = Pane(isLeft).FileSize;
            // Clamp to the file: a malformed element may claim to extend past EOF.
            length = Math.Max(0, Math.Min(el.Position + el.TotalSize, fileSize) - offset);
        }
        else
        {
            // Show the entire file for non-block nodes (Archive Info, Root, etc.)
            offset = 0;
            length = Pane(isLeft).FileSize;
        }

        MemoryMappedDataSource? source = Pane(isLeft).Source;

        if (isLeft)
        {
            LeftHexBlockOffset = offset;
            LeftHexBlockLength = length;
            LeftHexDataSource = source is not null && length > 0
                ? new HexDataSourceSlice(source, offset, length)
                : null;
        }
        else
        {
            RightHexBlockOffset = offset;
            RightHexBlockLength = length;
            RightHexDataSource = source is not null && length > 0
                ? new HexDataSourceSlice(source, offset, length)
                : null;
        }

        ScheduleDiff();
    }

    private void ScheduleDiff()
    {
        if (_diffScheduled)
        {
            return;
        }

        _diffScheduled = true;
        _uiDispatcher.Post(() =>
        {
            _diffScheduled = false;
            StartDiffNow();
        }, DispatcherPriority.Background);
    }

    private void StartDiffNow()
    {
        CancelDiff();

        MemoryMappedDataSource? leftSrc = _left.Source;
        MemoryMappedDataSource? rightSrc = _right.Source;
        long leftLen = LeftHexBlockLength;
        long rightLen = RightHexBlockLength;

        if (leftSrc is null || rightSrc is null || leftLen <= 0 || rightLen <= 0)
        {
            LeftDiffRanges = null;
            RightDiffRanges = null;
            return;
        }

        long leftOff = LeftHexBlockOffset;
        long rightOff = RightHexBlockOffset;

        var cts = new CancellationTokenSource();
        _diffCts = cts;
        CancellationToken token = cts.Token;

        var progress = new Progress<HexDiffProgress>(p =>
        {
            if (_diffCts != cts)
            {
                return;
            }

            LeftDiffRanges = p.Left;
            RightDiffRanges = p.Right;
            if (p.Percent < 100.0)
            {
                StatusMessage = $"Computing byte diff... {p.Percent:F0}%";
            }
        });

        _ = RunDiffAsync(leftSrc, leftOff, leftLen, rightSrc, rightOff, rightLen, progress, cts, token);
    }

    private async Task RunDiffAsync(
        MemoryMappedDataSource leftSrc, long leftOff, long leftLen,
        MemoryMappedDataSource rightSrc, long rightOff, long rightLen,
        IProgress<HexDiffProgress> progress,
        CancellationTokenSource cts,
        CancellationToken token)
    {
        try
        {
            HexDiffResult result = await _diffComputer.ComputeAsync(
                leftSrc, leftOff, leftLen,
                rightSrc, rightOff, rightLen,
                progress, token);

            if (_diffCts != cts)
            {
                return;
            }

            LeftDiffRanges = result.Left;
            RightDiffRanges = result.Right;
            UpdateStatus();
        }
        catch (OperationCanceledException)
        {
            // Cancelled — newer diff (or close) is responsible for next state.
        }
        catch (Exception ex)
        {
            if (_diffCts == cts)
            {
                StatusMessage = $"Byte diff failed: {ex.Message}";
            }
        }
        finally
        {
            if (_diffCts == cts)
            {
                _diffCts = null;
            }

            cts.Dispose();
        }
    }

    private void CancelDiff()
    {
        CancellationTokenSource? cts = _diffCts;
        _diffCts = null;
        cts?.Cancel();
    }

    partial void OnSelectedLeftPropertyChanged(PropertyItem? value)
    {
        if (value?.ByteRange is not null)
        {
            LeftHexSelectionOffset = value.ByteRange.Offset;
            LeftHexSelectionLength = value.ByteRange.Length;
        }
        else
        {
            LeftHexSelectionOffset = -1;
            LeftHexSelectionLength = 0;
        }

        // Sync to right
        if (value is not null)
        {
            SyncPropertySelection(value, RightProperties, false);
        }
    }

    partial void OnSelectedRightPropertyChanged(PropertyItem? value)
    {
        if (value?.ByteRange is not null)
        {
            RightHexSelectionOffset = value.ByteRange.Offset;
            RightHexSelectionLength = value.ByteRange.Length;
        }
        else
        {
            RightHexSelectionOffset = -1;
            RightHexSelectionLength = 0;
        }

        // Sync to left
        if (value is not null)
        {
            SyncPropertySelection(value, LeftProperties, true);
        }
    }

    private void SyncPropertySelection(PropertyItem source, ObservableCollection<PropertyItem> targetProperties, bool isLeftTarget)
    {
        // Find matching property by name
        PropertyItem? match = targetProperties.FirstOrDefault(p => p.Name == source.Name);
        if (match is not null)
        {
            if (isLeftTarget && SelectedLeftProperty != match)
            {
                SelectedLeftProperty = match;
            }
            else if (!isLeftTarget && SelectedRightProperty != match)
            {
                SelectedRightProperty = match;
            }
        }
    }

    #endregion

    #region Tree Population

    private void PopulateTree(ObservableCollection<TreeNodeViewModel> roots, object data, bool isLeft)
    {
        IReadOnlyList<RARDetailedBlock>? detailedBlocks = Pane(isLeft).Blocks;
        bool hasFileHeaders = detailedBlocks is not null &&
                              detailedBlocks.Any(b => b.BlockType is "File Header" or "Service Block");

        if (detailedBlocks is not null && detailedBlocks.Count > 0 && hasFileHeaders)
        {
            roots.Add(FileCompareTreeBuilder.BuildDetailed(detailedBlocks, isLeft));
        }
        else if (data is ReScene.Core.Comparison.SRRFileData srrData)
        {
            roots.Add(FileCompareTreeBuilder.BuildSrr(srrData, isLeft));
        }
        else if (data is SRSFile srsData)
        {
            roots.Add(FileCompareTreeBuilder.BuildSrs(srsData, isLeft));
        }
        else if (data is MKVFileData mkv)
        {
            roots.Add(FileCompareTreeBuilder.BuildMkv(mkv, isLeft));
        }
        else if (data is RARFileData rar)
        {
            roots.Add(FileCompareTreeBuilder.BuildRar(rar, isLeft));
        }
    }

    #endregion

    #region Tree Sync

    private void SyncTreeSelection(ObservableCollection<TreeNodeViewModel> targetRoots,
        CompareNodeData sourceData, ref TreeNodeViewModel? targetSelectedField)
    {
        TreeNodeViewModel? match = FindMatchingNode(targetRoots, sourceData);
        if (match is not null && targetSelectedField != match)
        {
            bool isLeftTarget = targetSelectedField == SelectedLeftTreeNode;

            // Deselect previous node
            if (targetSelectedField is not null)
            {
                targetSelectedField.IsSelected = false;
            }

            // Use field to avoid re-triggering the property changed handler
            targetSelectedField = match;
            match.IsSelected = true;

            OnPropertyChanged(isLeftTarget
                ? nameof(SelectedLeftTreeNode)
                : nameof(SelectedRightTreeNode));

            // Populate properties and hex for the synced side
            if (match.Tag is CompareNodeData targetData)
            {
                ObservableCollection<PropertyItem> properties = isLeftTarget ? LeftProperties : RightProperties;
                ShowProperties(targetData, properties, isLeftTarget);
                HighlightBlock(targetData, isLeftTarget);
            }
        }
    }

    private static TreeNodeViewModel? FindMatchingNode(ObservableCollection<TreeNodeViewModel> roots, CompareNodeData sourceData)
    {
        foreach (TreeNodeViewModel node in roots)
        {
            TreeNodeViewModel? result = FindMatchingNodeRecursive(node, sourceData);
            if (result is not null)
            {
                return result;
            }
        }

        return null;
    }

    private static TreeNodeViewModel? FindMatchingNodeRecursive(TreeNodeViewModel node, CompareNodeData sourceData)
    {
        if (node.Tag is CompareNodeData nodeData)
        {
            if (nodeData.NodeType == CompareNodeType.DetailedBlock && sourceData.NodeType == CompareNodeType.DetailedBlock)
            {
                if (nodeData.Data is RARDetailedBlock nodeBlock && sourceData.Data is RARDetailedBlock sourceBlock)
                {
                    if (nodeBlock.BlockType == sourceBlock.BlockType && nodeBlock.ItemName == sourceBlock.ItemName)
                    {
                        return node;
                    }
                }
            }
            else if (nodeData.NodeType == CompareNodeType.SRSContainerChunks
                  && sourceData.NodeType == CompareNodeType.SRSContainerChunks)
            {
                // SRSContainerChunks NodeType is shared by both the "Container
                // Structure" parent (Data = List<SRSContainerChunk>) and every
                // chunk child (Data = SRSContainerChunk). Match parent-to-parent
                // and chunk-to-chunk by Label so a clicked Cluster lights up the
                // corresponding Cluster on the other side rather than the first
                // SRSContainerChunks node encountered.
                if (nodeData.Data is IReadOnlyList<SRSContainerChunk> && sourceData.Data is IReadOnlyList<SRSContainerChunk>)
                {
                    return node;
                }

                if (nodeData.Data is SRSContainerChunk nodeChunk
                    && sourceData.Data is SRSContainerChunk sourceChunk
                    && nodeChunk.Label == sourceChunk.Label)
                {
                    return node;
                }
            }
            else if (nodeData.NodeType == sourceData.NodeType && nodeData.FileName == sourceData.FileName)
            {
                return node;
            }
        }

        foreach (TreeNodeViewModel child in node.Children)
        {
            TreeNodeViewModel? result = FindMatchingNodeRecursive(child, sourceData);
            if (result is not null)
            {
                return result;
            }
        }

        return null;
    }

    #endregion

    #region Property Display

    private void ShowProperties(CompareNodeData nodeData, ObservableCollection<PropertyItem> properties, bool isLeft)
    {
        properties.Clear();

        var builder = new CompareNodePropertyBuilder(
            _compareResult,
            _left.Blocks, _right.Blocks,
            _left.Data, _right.Data,
            _left.Path, _right.Path);

        foreach (PropertyItem item in builder.Build(nodeData, isLeft))
        {
            properties.Add(item);
        }
    }

    #endregion

    public void Dispose()
    {
        CancelDiff();
        _left.Source?.Dispose();
        _left.Source = null;
        _right.Source?.Dispose();
        _right.Source = null;
        GC.SuppressFinalize(this);
    }
}
