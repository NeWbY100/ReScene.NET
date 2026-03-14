using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RARLib;
using ReScene.NET.Models;
using ReScene.NET.Services;
using SRRLib;
using ReScene.Core.Comparison;

namespace ReScene.NET.ViewModels;

public enum CompareNodeType
{
    Root,
    ArchiveInfo,
    RarVolumes,
    RarVolume,
    StoredFiles,
    StoredFile,
    ArchivedFiles,
    ArchivedFile,
    OsoHashes,
    DetailedBlock,
    SrsFileInfo,
    SrsTrack,
    SrsContainerChunks
}

public class CompareNodeData
{
    public CompareNodeType NodeType { get; set; }
    public object? Data { get; set; }
    public string? FileName { get; set; }
    public bool IsLeft { get; set; }
}

public partial class FileCompareViewModel(IFileCompareService compareService, IFileDialogService fileDialog) : ViewModelBase, IDisposable
{
    private readonly IFileCompareService _compareService = compareService;
    private readonly IFileDialogService _fileDialog = fileDialog;

    // Internal state
    private object? _leftData;
    private object? _rightData;
    private string? _leftFilePathInternal;
    private string? _rightFilePathInternal;
    private long _leftFileSize;
    private long _rightFileSize;
    private List<RARDetailedBlock>? _leftDetailedBlocks;
    private List<RARDetailedBlock>? _rightDetailedBlocks;
    private CompareResult? _compareResult;
    private MemoryMappedDataSource? _leftFileSource;
    private MemoryMappedDataSource? _rightFileSource;

    // File paths
    [ObservableProperty]
    private string _leftFilePath = string.Empty;

    [ObservableProperty]
    private string _rightFilePath = string.Empty;

    // Tree views
    public ObservableCollection<TreeNodeViewModel> LeftTreeRoots { get; } = [];
    public ObservableCollection<TreeNodeViewModel> RightTreeRoots { get; } = [];

    [ObservableProperty]
    private TreeNodeViewModel? _selectedLeftTreeNode;

    [ObservableProperty]
    private TreeNodeViewModel? _selectedRightTreeNode;

    // Properties
    public ObservableCollection<PropertyItem> LeftProperties { get; } = [];
    public ObservableCollection<PropertyItem> RightProperties { get; } = [];

    [ObservableProperty]
    private PropertyItem? _selectedLeftProperty;

    [ObservableProperty]
    private PropertyItem? _selectedRightProperty;

    // Hex view - left
    [ObservableProperty]
    private IHexDataSource? _leftHexDataSource;

    [ObservableProperty]
    private long _leftHexBlockOffset;

    [ObservableProperty]
    private long _leftHexBlockLength;

    [ObservableProperty]
    private long _leftHexSelectionOffset = -1;

    [ObservableProperty]
    private long _leftHexSelectionLength;

    // Hex view - right
    [ObservableProperty]
    private IHexDataSource? _rightHexDataSource;

    [ObservableProperty]
    private long _rightHexBlockOffset;

    [ObservableProperty]
    private long _rightHexBlockLength;

    [ObservableProperty]
    private long _rightHexSelectionOffset = -1;

    [ObservableProperty]
    private long _rightHexSelectionLength;

    // Status
    [ObservableProperty]
    private string _statusMessage = "Load files on both sides to compare.";

    [ObservableProperty]
    private int _hexBytesPerLine = 16;

    [ObservableProperty]
    private bool _showHexView = true;

    [ObservableProperty]
    private string _diffSummary = string.Empty;

    [ObservableProperty]
    private bool _hasDiffSummary;

    [ObservableProperty]
    private bool _filesIdentical;

    #region Commands

    [RelayCommand]
    private async Task BrowseLeftAsync()
    {
        string? path = await _fileDialog.OpenFileAsync("Open Left File",
            ["Scene Files|*.rar;*.srr;*.srs", "RAR Files|*.rar", "SRR Files|*.srr", "SRS Files|*.srs", "All Files|*.*"]);

        if (path != null)
            LoadLeftFile(path);
    }

    [RelayCommand]
    private async Task BrowseRightAsync()
    {
        string? path = await _fileDialog.OpenFileAsync("Open Right File",
            ["Scene Files|*.rar;*.srr;*.srs", "RAR Files|*.rar", "SRR Files|*.srr", "SRS Files|*.srs", "All Files|*.*"]);

        if (path != null)
            LoadRightFile(path);
    }

    [RelayCommand]
    private void Swap()
    {
        (_leftData, _rightData) = (_rightData, _leftData);
        (_leftFilePathInternal, _rightFilePathInternal) = (_rightFilePathInternal, _leftFilePathInternal);
        (_leftFileSize, _rightFileSize) = (_rightFileSize, _leftFileSize);
        (_leftDetailedBlocks, _rightDetailedBlocks) = (_rightDetailedBlocks, _leftDetailedBlocks);
        (_leftFileSource, _rightFileSource) = (_rightFileSource, _leftFileSource);

        (LeftFilePath, RightFilePath) = (RightFilePath, LeftFilePath);

        LeftHexDataSource = null;
        RightHexDataSource = null;

        RefreshComparison();
    }

    #endregion

    #region File Loading

    public void LoadLeftFile(string filePath)
    {
        try
        {
            _leftFileSource?.Dispose();
            _leftFileSource = null;

            LeftFilePath = filePath;
            _leftFilePathInternal = filePath;
            _leftFileSize = new FileInfo(filePath).Length;
            _leftData = _compareService.LoadFileData(filePath);
            _leftDetailedBlocks = _compareService.ParseDetailedBlocks(filePath);
            _leftFileSource = new MemoryMappedDataSource(filePath);
            RefreshComparison();
        }
        catch (Exception ex)
        {
            LeftFilePath = string.Empty;
            _leftData = null;
            _leftFilePathInternal = null;
            _leftFileSize = 0;
            _leftDetailedBlocks = null;
            _leftFileSource?.Dispose();
            _leftFileSource = null;
            LeftHexDataSource = null;
            StatusMessage = $"Error loading left file: {ex.Message}";
        }
    }

    public void LoadRightFile(string filePath)
    {
        try
        {
            _rightFileSource?.Dispose();
            _rightFileSource = null;

            RightFilePath = filePath;
            _rightFilePathInternal = filePath;
            _rightFileSize = new FileInfo(filePath).Length;
            _rightData = _compareService.LoadFileData(filePath);
            _rightDetailedBlocks = _compareService.ParseDetailedBlocks(filePath);
            _rightFileSource = new MemoryMappedDataSource(filePath);
            RefreshComparison();
        }
        catch (Exception ex)
        {
            RightFilePath = string.Empty;
            _rightData = null;
            _rightFilePathInternal = null;
            _rightFileSize = 0;
            _rightDetailedBlocks = null;
            _rightFileSource?.Dispose();
            _rightFileSource = null;
            RightHexDataSource = null;
            StatusMessage = $"Error loading right file: {ex.Message}";
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

        if (_leftData != null)
            PopulateTree(LeftTreeRoots, _leftData, true);

        if (_rightData != null)
            PopulateTree(RightTreeRoots, _rightData, false);

        if (_leftData != null && _rightData != null)
        {
            _compareResult = _compareService.Compare(_leftData, _rightData,
                _leftDetailedBlocks, _rightDetailedBlocks);
            ApplyComparisonHighlighting();
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
        if (_compareResult == null)
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
            StatusMessage = "Files are identical.";
            DiffSummary = "No differences found - files are identical.";
            HasDiffSummary = true;
            FilesIdentical = true;
        }
        else
        {
            var parts = new List<string>();
            if (archiveDiffs > 0) parts.Add($"{archiveDiffs} archive property change(s)");
            if (added > 0) parts.Add($"{added} file(s) added");
            if (removed > 0) parts.Add($"{removed} file(s) removed");
            if (modified > 0) parts.Add($"{modified} file(s) modified");
            if (storedAdded > 0) parts.Add($"{storedAdded} stored file(s) added");
            if (storedRemoved > 0) parts.Add($"{storedRemoved} stored file(s) removed");

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
            SyncTreeSelection(LeftTreeRoots, RightTreeRoots, nodeData, ref _selectedRightTreeNode);
        }
    }

    partial void OnSelectedRightTreeNodeChanged(TreeNodeViewModel? value)
    {
        if (value?.Tag is CompareNodeData nodeData)
        {
            ShowProperties(nodeData, RightProperties, false);
            HighlightBlock(nodeData, false);
            SyncTreeSelection(RightTreeRoots, LeftTreeRoots, nodeData, ref _selectedLeftTreeNode);
        }
    }

    private void HighlightBlock(CompareNodeData nodeData, bool isLeft)
    {
        long offset;
        long length;

        if (nodeData.NodeType == CompareNodeType.DetailedBlock && nodeData.Data is RARDetailedBlock block)
        {
            offset = block.StartOffset;
            long fileSize = isLeft ? _leftFileSize : _rightFileSize;
            length = Math.Max(0, Math.Min(block.StartOffset + block.TotalSize, fileSize) - offset);
        }
        else if (nodeData.Data is SrsFileDataBlock fd)
        {
            offset = fd.BlockPosition;
            length = fd.BlockSize;
        }
        else if (nodeData.Data is SrsTrackDataBlock track)
        {
            offset = track.BlockPosition;
            length = track.BlockSize;
        }
        else if (nodeData.Data is SrsContainerChunk chunk)
        {
            offset = chunk.BlockPosition;
            length = chunk.BlockSize;
        }
        else
        {
            // Show the entire file for non-block nodes (Archive Info, Root, etc.)
            offset = 0;
            length = isLeft ? _leftFileSize : _rightFileSize;
        }

        var source = isLeft ? _leftFileSource : _rightFileSource;

        if (isLeft)
        {
            LeftHexBlockOffset = offset;
            LeftHexBlockLength = length;
            LeftHexDataSource = source != null && length > 0
                ? new HexDataSourceSlice(source, offset, length)
                : null;
        }
        else
        {
            RightHexBlockOffset = offset;
            RightHexBlockLength = length;
            RightHexDataSource = source != null && length > 0
                ? new HexDataSourceSlice(source, offset, length)
                : null;
        }
    }

    partial void OnSelectedLeftPropertyChanged(PropertyItem? value)
    {
        if (value?.ByteRange != null)
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
        if (value != null)
            SyncPropertySelection(value, RightProperties, false);
    }

    partial void OnSelectedRightPropertyChanged(PropertyItem? value)
    {
        if (value?.ByteRange != null)
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
        if (value != null)
            SyncPropertySelection(value, LeftProperties, true);
    }

    private void SyncPropertySelection(PropertyItem source, ObservableCollection<PropertyItem> targetProperties, bool isLeftTarget)
    {
        // Find matching property by name
        var match = targetProperties.FirstOrDefault(p => p.Name == source.Name);
        if (match != null)
        {
            if (isLeftTarget && SelectedLeftProperty != match)
                SelectedLeftProperty = match;
            else if (!isLeftTarget && SelectedRightProperty != match)
                SelectedRightProperty = match;
        }
    }

    #endregion

    #region Tree Population

    private void PopulateTree(ObservableCollection<TreeNodeViewModel> roots, object data, bool isLeft)
    {
        var detailedBlocks = isLeft ? _leftDetailedBlocks : _rightDetailedBlocks;
        bool hasFileHeaders = detailedBlocks != null &&
                              detailedBlocks.Any(b => b.BlockType is "File Header" or "Service Block");

        if (detailedBlocks != null && detailedBlocks.Count > 0 && hasFileHeaders)
        {
            PopulateDetailedTree(roots, detailedBlocks, isLeft);
        }
        else if (data is SRRFileData srrData)
        {
            PopulateSRRTree(roots, srrData, isLeft);
        }
        else if (data is SRSFile srsData)
        {
            PopulateSRSTree(roots, srsData, isLeft);
        }
        else if (data is RARFileData rar)
        {
            PopulateRARTree(roots, rar, isLeft);
        }
    }

    private static void PopulateDetailedTree(ObservableCollection<TreeNodeViewModel> roots, List<RARDetailedBlock> blocks, bool isLeft)
    {
        bool isRAR5 = blocks.Count > 0 && blocks[0].BlockType == "Signature" &&
                      blocks[0].Fields.Count > 0 && blocks[0].Fields[0].Value.StartsWith("52 61 72 21 1A 07 01");

        string rootName = isRAR5 ? $"RAR 5.x Archive ({blocks.Count} blocks)" : $"RAR 4.x Archive ({blocks.Count} blocks)";

        var rootNode = new TreeNodeViewModel
        {
            Text = rootName,
            Tag = new CompareNodeData { NodeType = CompareNodeType.Root, IsLeft = isLeft },
            IsExpanded = true
        };

        for (int i = 0; i < blocks.Count; i++)
        {
            var block = blocks[i];
            string blockType = block.HasData && block.BlockType.Contains("File") ? "File Data" : block.BlockType;
            string blockLabel = $"[{i}] {blockType}";

            if (!string.IsNullOrEmpty(block.ItemName))
                blockLabel = $"[{i}] {blockType}: {block.ItemName}";

            rootNode.Children.Add(new TreeNodeViewModel
            {
                Text = blockLabel,
                Tag = new CompareNodeData
                {
                    NodeType = CompareNodeType.DetailedBlock,
                    Data = block,
                    FileName = block.ItemName,
                    IsLeft = isLeft
                }
            });
        }

        roots.Add(rootNode);
    }

    private static void PopulateSRRTree(ObservableCollection<TreeNodeViewModel> roots, SRRFileData srrData, bool isLeft)
    {
        var srr = srrData.SrrFile;

        var rootNode = new TreeNodeViewModel
        {
            Text = "SRR File",
            Tag = new CompareNodeData { NodeType = CompareNodeType.Root, Data = srr, IsLeft = isLeft },
            IsExpanded = true
        };

        rootNode.Children.Add(new TreeNodeViewModel
        {
            Text = "Archive Info",
            Tag = new CompareNodeData { NodeType = CompareNodeType.ArchiveInfo, Data = srr, IsLeft = isLeft }
        });

        if (srr.RarFiles.Count > 0)
        {
            var volumesNode = new TreeNodeViewModel
            {
                Text = $"RAR Volumes ({srr.RarFiles.Count})",
                Tag = new CompareNodeData { NodeType = CompareNodeType.RarVolumes, Data = srr.RarFiles, IsLeft = isLeft }
            };

            foreach (var rar in srr.RarFiles)
            {
                var volNode = new TreeNodeViewModel
                {
                    Text = rar.FileName,
                    Tag = new CompareNodeData { NodeType = CompareNodeType.RarVolume, Data = rar, IsLeft = isLeft }
                };

                if (srrData.VolumeDetailedBlocks.TryGetValue(rar.FileName, out var detailedBlocks))
                {
                    for (int i = 0; i < detailedBlocks.Count; i++)
                    {
                        var block = detailedBlocks[i];
                        string blockType = block.HasData && block.BlockType.Contains("File") ? "File Data" : block.BlockType;
                        string blockLabel = $"[{i}] {blockType}";
                        if (!string.IsNullOrEmpty(block.ItemName))
                            blockLabel = $"[{i}] {blockType}: {block.ItemName}";

                        volNode.Children.Add(new TreeNodeViewModel
                        {
                            Text = blockLabel,
                            Tag = new CompareNodeData
                            {
                                NodeType = CompareNodeType.DetailedBlock,
                                Data = block,
                                FileName = block.ItemName,
                                IsLeft = isLeft
                            }
                        });
                    }
                }

                volumesNode.Children.Add(volNode);
            }

            rootNode.Children.Add(volumesNode);
        }

        if (srr.StoredFiles.Count > 0)
        {
            var storedNode = new TreeNodeViewModel
            {
                Text = $"Stored Files ({srr.StoredFiles.Count})",
                Tag = new CompareNodeData { NodeType = CompareNodeType.StoredFiles, Data = srr.StoredFiles, IsLeft = isLeft }
            };

            foreach (var stored in srr.StoredFiles)
            {
                storedNode.Children.Add(new TreeNodeViewModel
                {
                    Text = stored.FileName,
                    Tag = new CompareNodeData { NodeType = CompareNodeType.StoredFile, Data = stored, FileName = stored.FileName, IsLeft = isLeft }
                });
            }

            rootNode.Children.Add(storedNode);
        }

        if (srr.ArchivedFiles.Count > 0)
        {
            var archivedNode = new TreeNodeViewModel
            {
                Text = $"Archived Files ({srr.ArchivedFiles.Count})",
                Tag = new CompareNodeData { NodeType = CompareNodeType.ArchivedFiles, Data = srr, IsLeft = isLeft }
            };

            foreach (var file in srr.ArchivedFiles.OrderBy(f => f))
            {
                string displayName = file;
                if (srr.ArchivedFileCrcs.TryGetValue(file, out var crc))
                    displayName = $"{file} [CRC: {crc}]";

                archivedNode.Children.Add(new TreeNodeViewModel
                {
                    Text = displayName,
                    Tag = new CompareNodeData { NodeType = CompareNodeType.ArchivedFile, Data = srr, FileName = file, IsLeft = isLeft }
                });
            }

            rootNode.Children.Add(archivedNode);
        }

        if (srr.OsoHashBlocks.Count > 0)
        {
            rootNode.Children.Add(new TreeNodeViewModel
            {
                Text = $"OSO Hashes ({srr.OsoHashBlocks.Count})",
                Tag = new CompareNodeData { NodeType = CompareNodeType.OsoHashes, Data = srr.OsoHashBlocks, IsLeft = isLeft }
            });
        }

        roots.Add(rootNode);
    }

    private static void PopulateSRSTree(ObservableCollection<TreeNodeViewModel> roots, SRSFile srs, bool isLeft)
    {
        var rootNode = new TreeNodeViewModel
        {
            Text = $"SRS File ({srs.ContainerType})",
            Tag = new CompareNodeData { NodeType = CompareNodeType.Root, Data = srs, IsLeft = isLeft },
            IsExpanded = true
        };

        if (srs.FileData is { } fd)
        {
            rootNode.Children.Add(new TreeNodeViewModel
            {
                Text = $"File Info: {fd.FileName}",
                Tag = new CompareNodeData { NodeType = CompareNodeType.SrsFileInfo, Data = fd, IsLeft = isLeft }
            });
        }

        if (srs.Tracks.Count > 0)
        {
            var tracksNode = new TreeNodeViewModel
            {
                Text = $"Tracks ({srs.Tracks.Count})",
                Tag = new CompareNodeData { NodeType = CompareNodeType.Root, IsLeft = isLeft }
            };

            foreach (var track in srs.Tracks)
            {
                tracksNode.Children.Add(new TreeNodeViewModel
                {
                    Text = $"Track {track.TrackNumber}",
                    Tag = new CompareNodeData
                    {
                        NodeType = CompareNodeType.SrsTrack,
                        Data = track,
                        FileName = $"Track {track.TrackNumber}",
                        IsLeft = isLeft
                    }
                });
            }

            rootNode.Children.Add(tracksNode);
        }

        if (srs.ContainerChunks.Count > 0)
        {
            var chunksNode = new TreeNodeViewModel
            {
                Text = $"Container Structure ({srs.ContainerChunks.Count})",
                Tag = new CompareNodeData { NodeType = CompareNodeType.SrsContainerChunks, Data = srs.ContainerChunks, IsLeft = isLeft }
            };

            foreach (var chunk in srs.ContainerChunks)
            {
                chunksNode.Children.Add(new TreeNodeViewModel
                {
                    Text = $"{chunk.Label} (0x{chunk.BlockPosition:X}, {chunk.BlockSize:N0} bytes)",
                    Tag = new CompareNodeData { NodeType = CompareNodeType.SrsContainerChunks, Data = chunk, IsLeft = isLeft }
                });
            }

            rootNode.Children.Add(chunksNode);
        }

        roots.Add(rootNode);
    }

    private static void PopulateRARTree(ObservableCollection<TreeNodeViewModel> roots, RARFileData rar, bool isLeft)
    {
        int fileCount = rar.IsRAR5 ? rar.RAR5FileInfos.Count : rar.FileHeaders.Count;
        int blockCount = 2 + fileCount + 1 + (string.IsNullOrEmpty(rar.Comment) ? 0 : 1);

        var rootNode = new TreeNodeViewModel
        {
            Text = rar.IsRAR5 ? $"RAR 5.x Archive (~{blockCount} blocks)" : $"RAR 4.x Archive (~{blockCount} blocks)",
            Tag = new CompareNodeData { NodeType = CompareNodeType.Root, Data = rar, IsLeft = isLeft },
            IsExpanded = true
        };

        int blockIndex = 0;

        rootNode.Children.Add(new TreeNodeViewModel
        {
            Text = $"[{blockIndex++}] Signature",
            Tag = new CompareNodeData { NodeType = CompareNodeType.Root, IsLeft = isLeft }
        });

        rootNode.Children.Add(new TreeNodeViewModel
        {
            Text = $"[{blockIndex++}] Archive Header",
            Tag = new CompareNodeData { NodeType = CompareNodeType.ArchiveInfo, Data = rar, IsLeft = isLeft }
        });

        if (rar.IsRAR5)
        {
            foreach (var file in rar.RAR5FileInfos)
            {
                rootNode.Children.Add(new TreeNodeViewModel
                {
                    Text = $"[{blockIndex++}] File Header: {file.FileName}",
                    Tag = new CompareNodeData { NodeType = CompareNodeType.ArchivedFile, Data = file, FileName = file.FileName, IsLeft = isLeft }
                });
            }
        }
        else
        {
            foreach (var file in rar.FileHeaders)
            {
                rootNode.Children.Add(new TreeNodeViewModel
                {
                    Text = $"[{blockIndex++}] File Header: {file.FileName}",
                    Tag = new CompareNodeData { NodeType = CompareNodeType.ArchivedFile, Data = file, FileName = file.FileName, IsLeft = isLeft }
                });
            }
        }

        if (!string.IsNullOrEmpty(rar.Comment))
        {
            rootNode.Children.Add(new TreeNodeViewModel
            {
                Text = $"[{blockIndex++}] Service Block: CMT",
                Tag = new CompareNodeData { NodeType = CompareNodeType.Root, IsLeft = isLeft }
            });
        }

        rootNode.Children.Add(new TreeNodeViewModel
        {
            Text = $"[{blockIndex}] End Archive",
            Tag = new CompareNodeData { NodeType = CompareNodeType.Root, IsLeft = isLeft }
        });

        roots.Add(rootNode);
    }

    #endregion

    #region Comparison Highlighting

    private void ApplyComparisonHighlighting()
    {
        if (_compareResult == null) return;

        var addedFiles = _compareResult.FileDifferences
            .Where(d => d.Type == DifferenceType.Added)
            .Select(d => d.FileName)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var removedFiles = _compareResult.FileDifferences
            .Where(d => d.Type == DifferenceType.Removed)
            .Select(d => d.FileName)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var modifiedFiles = _compareResult.FileDifferences
            .Where(d => d.Type == DifferenceType.Modified)
            .Select(d => d.FileName)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var addedStoredFiles = _compareResult.StoredFileDifferences
            .Where(d => d.Type == DifferenceType.Added)
            .Select(d => d.FileName)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var removedStoredFiles = _compareResult.StoredFileDifferences
            .Where(d => d.Type == DifferenceType.Removed)
            .Select(d => d.FileName)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        // Apply text annotations to tree nodes
        foreach (var root in LeftTreeRoots)
            ApplyNodeHighlighting(root, removedFiles, addedFiles, modifiedFiles, removedStoredFiles, addedStoredFiles, true);

        foreach (var root in RightTreeRoots)
            ApplyNodeHighlighting(root, addedFiles, removedFiles, modifiedFiles, addedStoredFiles, removedStoredFiles, false);
    }

    private void ApplyNodeHighlighting(TreeNodeViewModel node, HashSet<string> removed, HashSet<string> added,
        HashSet<string> modified, HashSet<string> storedRemoved, HashSet<string> storedAdded, bool isLeft)
    {
        if (node.Tag is CompareNodeData data)
        {
            if (data.NodeType == CompareNodeType.DetailedBlock && data.Data is RARDetailedBlock block)
            {
                var otherBlocks = isLeft ? _rightDetailedBlocks : _leftDetailedBlocks;
                if (otherBlocks != null)
                {
                    var otherBlock = otherBlocks.FirstOrDefault(b =>
                        b.BlockType == block.BlockType && b.ItemName == block.ItemName);
                    if (otherBlock != null && FileComparer.HasFieldDifferences(block, otherBlock))
                    {
                        node.Text = $"{GetBaseNodeText(node.Text)} [DIFF]";
                        node.IsDifferent = true;
                    }
                }
            }
            else if (data.NodeType == CompareNodeType.SrsTrack && data.FileName is not null)
            {
                if (removed.Contains(data.FileName))
                {
                    node.Text = isLeft ? $"{GetBaseNodeText(node.Text)} [REMOVED]" : $"{GetBaseNodeText(node.Text)} [NEW]";
                    node.IsDifferent = true;
                }
                else if (modified.Contains(data.FileName))
                {
                    node.Text = $"{GetBaseNodeText(node.Text)} [DIFF]";
                    node.IsDifferent = true;
                }
            }
            else if (data.NodeType is CompareNodeType.ArchivedFile or CompareNodeType.StoredFile)
            {
                var fileName = data.FileName ?? "";

                if (data.NodeType == CompareNodeType.StoredFile)
                {
                    if (storedRemoved.Contains(fileName))
                    {
                        node.Text = isLeft ? $"{GetBaseNodeText(node.Text)} [REMOVED]" : $"{GetBaseNodeText(node.Text)} [NEW]";
                        node.IsDifferent = true;
                    }
                }
                else
                {
                    if (removed.Contains(fileName))
                    {
                        node.Text = isLeft ? $"{GetBaseNodeText(node.Text)} [REMOVED]" : $"{GetBaseNodeText(node.Text)} [NEW]";
                        node.IsDifferent = true;
                    }
                    else if (modified.Contains(fileName))
                    {
                        node.Text = $"{GetBaseNodeText(node.Text)} [DIFF]";
                        node.IsDifferent = true;
                    }
                }
            }
        }

        foreach (var child in node.Children)
            ApplyNodeHighlighting(child, removed, added, modified, storedRemoved, storedAdded, isLeft);
    }

    private static string GetBaseNodeText(string text)
    {
        int bracketIndex = text.LastIndexOf(" [", StringComparison.Ordinal);
        if (bracketIndex > 0 && (text.EndsWith("[REMOVED]") || text.EndsWith("[NEW]") || text.EndsWith("[DIFF]")))
            return text[..bracketIndex];
        return text;
    }

    #endregion

    #region Tree Sync

    private void SyncTreeSelection(ObservableCollection<TreeNodeViewModel> sourceRoots,
        ObservableCollection<TreeNodeViewModel> targetRoots, CompareNodeData sourceData,
        ref TreeNodeViewModel? targetSelectedField)
    {
        var match = FindMatchingNode(targetRoots, sourceData);
        if (match != null && targetSelectedField != match)
        {
            bool isLeftTarget = targetSelectedField == SelectedLeftTreeNode;

            // Deselect previous node
            if (targetSelectedField != null)
                targetSelectedField.IsSelected = false;

            // Use field to avoid re-triggering the property changed handler
            targetSelectedField = match;
            match.IsSelected = true;

            OnPropertyChanged(isLeftTarget
                ? nameof(SelectedLeftTreeNode)
                : nameof(SelectedRightTreeNode));

            // Populate properties and hex for the synced side
            if (match.Tag is CompareNodeData targetData)
            {
                var properties = isLeftTarget ? LeftProperties : RightProperties;
                ShowProperties(targetData, properties, isLeftTarget);
                HighlightBlock(targetData, isLeftTarget);
            }
        }
    }

    private static TreeNodeViewModel? FindMatchingNode(ObservableCollection<TreeNodeViewModel> roots, CompareNodeData sourceData)
    {
        foreach (var node in roots)
        {
            var result = FindMatchingNodeRecursive(node, sourceData);
            if (result != null) return result;
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
                        return node;
                }
            }
            else if (nodeData.NodeType == sourceData.NodeType && nodeData.FileName == sourceData.FileName)
            {
                return node;
            }
        }

        foreach (var child in node.Children)
        {
            var result = FindMatchingNodeRecursive(child, sourceData);
            if (result != null) return result;
        }
        return null;
    }

    #endregion

    #region Property Display

    private void ShowProperties(CompareNodeData nodeData, ObservableCollection<PropertyItem> properties, bool isLeft)
    {
        properties.Clear();

        switch (nodeData.NodeType)
        {
            case CompareNodeType.DetailedBlock:
                if (nodeData.Data is RARDetailedBlock detailedBlock)
                    ShowDetailedBlockProperties(properties, detailedBlock, isLeft);
                break;

            case CompareNodeType.ArchiveInfo:
                if (nodeData.Data is SRRFile srr)
                    ShowSRRArchiveProperties(properties, srr);
                else if (nodeData.Data is RARFileData rar)
                    ShowRARArchiveProperties(properties, rar);
                break;

            case CompareNodeType.ArchivedFile:
                if (nodeData.Data is RARFileHeader fileHeader)
                    ShowRAR4FileProperties(properties, fileHeader);
                else if (nodeData.Data is RAR5FileInfo fileInfo)
                    ShowRAR5FileProperties(properties, fileInfo);
                else if (nodeData.Data is SRRFile srrFile && nodeData.FileName != null)
                    ShowSRRArchivedFileProperties(properties, srrFile, nodeData.FileName);
                break;

            case CompareNodeType.StoredFile:
                if (nodeData.Data is SrrStoredFileBlock stored)
                    ShowStoredFileProperties(properties, stored);
                break;

            case CompareNodeType.RarVolume:
                if (nodeData.Data is SrrRarFileBlock rarFile)
                    ShowRarVolumeProperties(properties, rarFile);
                break;

            case CompareNodeType.SrsFileInfo:
                if (nodeData.Data is SrsFileDataBlock fd)
                    ShowSrsFileInfoProperties(properties, fd);
                break;

            case CompareNodeType.SrsTrack:
                if (nodeData.Data is SrsTrackDataBlock track)
                    ShowSrsTrackProperties(properties, track);
                break;

            case CompareNodeType.SrsContainerChunks:
                if (nodeData.Data is SrsContainerChunk chunk)
                    ShowSrsContainerChunkProperties(properties, chunk);
                break;
        }
    }

    private void ShowDetailedBlockProperties(ObservableCollection<PropertyItem> properties, RARDetailedBlock block, bool isLeft)
    {
        properties.Add(new PropertyItem { Name = "Block Type", Value = block.BlockType });
        properties.Add(new PropertyItem { Name = "Start Offset", Value = $"0x{block.StartOffset:X8}" });
        properties.Add(new PropertyItem { Name = "Header Size", Value = $"{block.HeaderSize} bytes" });
        properties.Add(new PropertyItem { Name = "Total Size", Value = $"{block.TotalSize:N0} bytes" });

        if (block.HasData)
            properties.Add(new PropertyItem { Name = "Data Size", Value = $"{block.DataSize:N0} bytes" });

        properties.Add(new PropertyItem { Name = "--- Fields ---", Value = "", IsSeparator = true });

        var otherBlock = _compareResult != null ? FindMatchingDetailedBlock(block, isLeft) : null;

        foreach (var field in block.Fields)
        {
            string value = field.Value;
            if (!string.IsNullOrEmpty(field.Description) && field.Description != field.Value)
                value = $"{field.Value} ({field.Description})";

            bool isDiff = false;
            if (_compareResult != null && otherBlock != null)
            {
                var otherField = otherBlock.Fields.FirstOrDefault(f => f.Name == field.Name);
                if (otherField != null)
                    isDiff = otherField.Value != field.Value;
            }

            properties.Add(new PropertyItem
            {
                Name = field.Name,
                Value = value,
                IsDifferent = isDiff,
                ByteRange = new ByteRange
                {
                    PropertyName = field.Name,
                    Offset = field.Offset,
                    Length = field.Length
                }
            });

            foreach (var child in field.Children)
            {
                bool childDiff = false;
                if (_compareResult != null && otherBlock != null)
                {
                    var otherParent = otherBlock.Fields.FirstOrDefault(f => f.Name == field.Name);
                    var otherChild = otherParent?.Children.FirstOrDefault(c => c.Name == child.Name);
                    if (otherChild != null)
                        childDiff = otherChild.Value != child.Value;
                }

                long childOffset = child.Length > 0 ? child.Offset : field.Offset;
                int childLength = child.Length > 0 ? child.Length : field.Length;

                properties.Add(new PropertyItem
                {
                    Name = child.Name,
                    Value = child.Value,
                    IsIndented = true,
                    IsDifferent = childDiff,
                    ByteRange = childLength > 0
                        ? new ByteRange { PropertyName = child.Name, Offset = childOffset, Length = childLength }
                        : null
                });
            }
        }

        if (block.HasData && block.DataSize > 0)
        {
            long dataOffset = block.StartOffset + block.HeaderSize;
            bool dataDiff = false;

            if (_compareResult != null && otherBlock is { HasData: true })
            {
                var thisFilePath = isLeft ? _leftFilePathInternal : _rightFilePathInternal;
                var otherFilePath = isLeft ? _rightFilePathInternal : _leftFilePathInternal;
                if (thisFilePath != null && otherFilePath != null)
                {
                    long otherDataOffset = otherBlock.StartOffset + otherBlock.HeaderSize;
                    if (block.DataSize != otherBlock.DataSize)
                    {
                        dataDiff = true;
                    }
                    else if (block.DataSize <= 10 * 1024 * 1024) // Compare up to 10 MB
                    {
                        var thisData = ReadFileSlice(thisFilePath, dataOffset, (int)block.DataSize);
                        var otherData = ReadFileSlice(otherFilePath, otherDataOffset, (int)otherBlock.DataSize);
                        dataDiff = thisData == null || otherData == null ||
                            !thisData.AsSpan().SequenceEqual(otherData.AsSpan());
                    }
                }
            }

            properties.Add(new PropertyItem
            {
                Name = "Data",
                Value = $"{block.DataSize:N0} bytes (offset 0x{dataOffset:X8})",
                IsDifferent = dataDiff,
                ByteRange = new ByteRange
                {
                    PropertyName = "Data",
                    Offset = dataOffset,
                    Length = (int)Math.Min(block.DataSize, int.MaxValue)
                }
            });
        }
    }

    private RARDetailedBlock? FindMatchingDetailedBlock(RARDetailedBlock block, bool isLeft)
    {
        var otherBlocks = isLeft ? _rightDetailedBlocks : _leftDetailedBlocks;
        if (otherBlocks != null)
        {
            var match = otherBlocks.FirstOrDefault(b =>
                b.BlockType == block.BlockType && b.ItemName == block.ItemName);
            if (match != null) return match;
        }

        var otherData = isLeft ? _rightData : _leftData;
        if (otherData is SRRFileData otherSrrData)
        {
            foreach (var volumeBlocks in otherSrrData.VolumeDetailedBlocks.Values)
            {
                var match = volumeBlocks.FirstOrDefault(b =>
                    b.BlockType == block.BlockType && b.ItemName == block.ItemName);
                if (match != null) return match;
            }
        }

        return null;
    }

    private void ShowSRRArchiveProperties(ObservableCollection<PropertyItem> properties, SRRFile srr)
    {
        AddComparedProperty(properties, "App Name", srr.HeaderBlock?.AppName ?? "N/A", "App Name");
        AddComparedProperty(properties, "RAR Version", FileComparer.FormatRARVersion(srr.RARVersion), "RAR Version");
        AddComparedProperty(properties, "Compression Method", FileComparer.GetCompressionMethodName(srr.CompressionMethod), "Compression Method");
        AddComparedProperty(properties, "Dictionary Size", FileComparer.FormatDictionarySize(srr.DictionarySize), "Dictionary Size");
        AddComparedProperty(properties, "Solid Archive", FileComparer.FormatBool(srr.IsSolidArchive), "Solid Archive");
        AddComparedProperty(properties, "Volume Archive", FileComparer.FormatBool(srr.IsVolumeArchive), "Volume Archive");
        AddComparedProperty(properties, "Recovery Record", FileComparer.FormatBool(srr.HasRecoveryRecord), "Recovery Record");
        AddComparedProperty(properties, "Encrypted Headers", FileComparer.FormatBool(srr.HasEncryptedHeaders), "Encrypted Headers");
        AddComparedProperty(properties, "RAR Volumes", srr.RarFiles.Count.ToString(), "RAR Volumes Count");
        AddComparedProperty(properties, "Stored Files", srr.StoredFiles.Count.ToString(), "Stored Files Count");
        AddComparedProperty(properties, "Archived Files", srr.ArchivedFiles.Count.ToString(), "Archived Files Count");
        AddComparedProperty(properties, "Header CRC Errors", srr.HeaderCrcMismatches.ToString(), "Header CRC Errors");
        AddComparedProperty(properties, "Has Comment", FileComparer.FormatBool(!string.IsNullOrEmpty(srr.ArchiveComment)), "Has Comment");

        properties.Add(new PropertyItem { Name = "--- Reconstruction Hints ---", Value = "", IsSeparator = true });

        if (srr.DetectedHostOS.HasValue)
            AddComparedProperty(properties, "Host OS (files)", $"{srr.DetectedHostOSName} (0x{srr.DetectedHostOS:X2})", "Host OS");

        if (srr.CmtHostOS.HasValue)
            AddComparedProperty(properties, "CMT Host OS", $"{srr.CmtHostOSName} (0x{srr.CmtHostOS:X2})", "CMT Host OS");

        if (srr.CmtFileTimeDOS.HasValue)
        {
            string timeMode = srr.CmtHasZeroedFileTime
                ? "Zeroed (0x00000000)"
                : $"0x{srr.CmtFileTimeDOS:X8}";
            AddComparedProperty(properties, "CMT Timestamp", timeMode, "CMT Timestamp");
            AddComparedProperty(properties, "CMT Time Mode", srr.CmtTimestampMode, "CMT Time Mode");
        }

        if (srr.CmtFileAttributes.HasValue)
            AddComparedProperty(properties, "CMT Attributes", $"0x{srr.CmtFileAttributes:X8}", "CMT Attributes");
    }

    private void ShowRARArchiveProperties(ObservableCollection<PropertyItem> properties, RARFileData rar)
    {
        AddComparedProperty(properties, "Format", rar.IsRAR5 ? "RAR 5.x" : "RAR 4.x", "Format");

        if (rar.IsRAR5 && rar.RAR5ArchiveInfo != null)
        {
            AddComparedProperty(properties, "Volume", FileComparer.FormatBool(rar.RAR5ArchiveInfo.IsVolume), "Volume");
            AddComparedProperty(properties, "Solid", FileComparer.FormatBool(rar.RAR5ArchiveInfo.IsSolid), "Solid");
            AddComparedProperty(properties, "Recovery Record", FileComparer.FormatBool(rar.RAR5ArchiveInfo.HasRecoveryRecord), "Recovery Record");
            AddComparedProperty(properties, "Locked", FileComparer.FormatBool(rar.RAR5ArchiveInfo.IsLocked), "Locked");
            AddComparedProperty(properties, "File Count", rar.RAR5FileInfos.Count.ToString(), "File Count");
        }
        else if (!rar.IsRAR5 && rar.ArchiveHeader != null)
        {
            AddComparedProperty(properties, "Volume", FileComparer.FormatBool(rar.ArchiveHeader.IsVolume), "Volume");
            AddComparedProperty(properties, "Solid", FileComparer.FormatBool(rar.ArchiveHeader.IsSolid), "Solid");
            AddComparedProperty(properties, "Recovery Record", FileComparer.FormatBool(rar.ArchiveHeader.HasRecoveryRecord), "Recovery Record");
            AddComparedProperty(properties, "Locked", FileComparer.FormatBool(rar.ArchiveHeader.IsLocked), "Locked");
            AddComparedProperty(properties, "Encrypted Headers", FileComparer.FormatBool(rar.ArchiveHeader.HasEncryptedHeaders), "Encrypted Headers");
            AddComparedProperty(properties, "File Count", rar.FileHeaders.Count.ToString(), "File Count");
        }

        AddComparedProperty(properties, "Has Comment", FileComparer.FormatBool(!string.IsNullOrEmpty(rar.Comment)), "Has Comment");
    }

    private void ShowRAR4FileProperties(ObservableCollection<PropertyItem> properties, RARFileHeader header)
    {
        properties.Add(new PropertyItem { Name = "File Name", Value = header.FileName });
        properties.Add(new PropertyItem { Name = "Type", Value = header.IsDirectory ? "Directory" : "File" });
        AddComparedProperty(properties, "Unpacked Size", $"{header.UnpackedSize:N0} bytes", "Unpacked Size");
        AddComparedProperty(properties, "Packed Size", $"{header.PackedSize:N0} bytes", "Packed Size");
        AddComparedProperty(properties, "CRC32", header.FileCrc.ToString("X8"), "CRC");
        AddComparedProperty(properties, "Compression Method", FileComparer.GetCompressionMethodName((int?)header.CompressionMethod), "Compression Method");
        properties.Add(new PropertyItem { Name = "Dictionary Size", Value = $"{header.DictionarySizeKB} KB" });
        AddComparedProperty(properties, "Modified Time", header.ModifiedTime?.ToString("yyyy-MM-dd HH:mm:ss") ?? "N/A", "Modified Time");
        properties.Add(new PropertyItem { Name = "Split Before", Value = FileComparer.FormatBool(header.IsSplitBefore) });
        properties.Add(new PropertyItem { Name = "Split After", Value = FileComparer.FormatBool(header.IsSplitAfter) });
    }

    private void ShowRAR5FileProperties(ObservableCollection<PropertyItem> properties, RAR5FileInfo info)
    {
        properties.Add(new PropertyItem { Name = "File Name", Value = info.FileName });
        properties.Add(new PropertyItem { Name = "Type", Value = info.IsDirectory ? "Directory" : "File" });
        AddComparedProperty(properties, "Unpacked Size", $"{info.UnpackedSize:N0} bytes", "Unpacked Size");
        AddComparedProperty(properties, "CRC32", info.FileCrc?.ToString("X8") ?? "N/A", "CRC");
        AddComparedProperty(properties, "Compression Method", FileComparer.GetCompressionMethodName((int?)info.CompressionMethod), "Compression Method");
        properties.Add(new PropertyItem { Name = "Dictionary Size", Value = $"{info.DictionarySizeKB} KB" });
        if (info.ModificationTime.HasValue)
        {
            var dt = DateTimeOffset.FromUnixTimeSeconds(info.ModificationTime.Value).LocalDateTime;
            properties.Add(new PropertyItem { Name = "Modified Time", Value = dt.ToString("yyyy-MM-dd HH:mm:ss") });
        }
        properties.Add(new PropertyItem { Name = "Split Before", Value = FileComparer.FormatBool(info.IsSplitBefore) });
        properties.Add(new PropertyItem { Name = "Split After", Value = FileComparer.FormatBool(info.IsSplitAfter) });
    }

    private void ShowSRRArchivedFileProperties(ObservableCollection<PropertyItem> properties, SRRFile srr, string fileName)
    {
        properties.Add(new PropertyItem { Name = "File Name", Value = fileName });

        if (srr.ArchivedFileCrcs.TryGetValue(fileName, out var crc))
            AddComparedProperty(properties, "CRC32", crc, "CRC");

        if (srr.ArchivedFileTimestamps.TryGetValue(fileName, out var modTime))
            AddComparedProperty(properties, "Modified Time", modTime.ToString("yyyy-MM-dd HH:mm:ss"), "Modified Time");

        if (srr.ArchivedFileCreationTimes.TryGetValue(fileName, out var createTime))
            properties.Add(new PropertyItem { Name = "Creation Time", Value = createTime.ToString("yyyy-MM-dd HH:mm:ss") });

        if (srr.ArchivedFileAccessTimes.TryGetValue(fileName, out var accessTime))
            properties.Add(new PropertyItem { Name = "Access Time", Value = accessTime.ToString("yyyy-MM-dd HH:mm:ss") });
    }

    private static void ShowStoredFileProperties(ObservableCollection<PropertyItem> properties, SrrStoredFileBlock stored)
    {
        properties.Add(new PropertyItem { Name = "File Name", Value = stored.FileName });
        properties.Add(new PropertyItem { Name = "File Size", Value = $"{stored.FileLength:N0} bytes" });
        properties.Add(new PropertyItem { Name = "Data Offset", Value = $"0x{stored.DataOffset:X8}" });
    }

    private static void ShowRarVolumeProperties(ObservableCollection<PropertyItem> properties, SrrRarFileBlock rarFile)
    {
        properties.Add(new PropertyItem { Name = "Volume Name", Value = rarFile.FileName });
        properties.Add(new PropertyItem { Name = "Block Position", Value = $"0x{rarFile.BlockPosition:X8}" });
        properties.Add(new PropertyItem { Name = "Header CRC", Value = $"0x{rarFile.Crc:X4}" });
    }

    private void ShowSrsFileInfoProperties(ObservableCollection<PropertyItem> properties, SrsFileDataBlock fd)
    {
        AddComparedProperty(properties, "App Name", fd.AppName, "App Name");

        properties.Add(new PropertyItem
        {
            Name = "File Name",
            Value = fd.FileName,
            ByteRange = new ByteRange { PropertyName = "File Name", Offset = fd.FileNameOffset, Length = fd.FileNameSize }
        });

        AddComparedProperty(properties, "Sample Size", $"{fd.SampleSize:N0} bytes", "Sample Size");
        AddComparedProperty(properties, "CRC32", fd.Crc32.ToString("X8"), "CRC32");
        AddComparedProperty(properties, "Flags", $"0x{fd.Flags:X4}", "Flags");

        properties.Add(new PropertyItem
        {
            Name = "Block Position",
            Value = $"0x{fd.BlockPosition:X8}"
        });

        properties.Add(new PropertyItem
        {
            Name = "Block Size",
            Value = $"{fd.BlockSize:N0} bytes"
        });
    }

    private void ShowSrsTrackProperties(ObservableCollection<PropertyItem> properties, SrsTrackDataBlock track)
    {
        properties.Add(new PropertyItem
        {
            Name = "Track Number",
            Value = track.TrackNumber.ToString(),
            ByteRange = new ByteRange { PropertyName = "Track Number", Offset = track.TrackNumberOffset, Length = track.TrackNumberFieldSize }
        });

        properties.Add(new PropertyItem
        {
            Name = "Data Length",
            Value = $"{track.DataLength:N0} bytes",
            ByteRange = new ByteRange { PropertyName = "Data Length", Offset = track.DataLengthOffset, Length = track.DataLengthFieldSize }
        });

        properties.Add(new PropertyItem
        {
            Name = "Match Offset",
            Value = $"0x{track.MatchOffset:X}",
            ByteRange = new ByteRange { PropertyName = "Match Offset", Offset = track.MatchOffsetOffset, Length = 8 }
        });

        properties.Add(new PropertyItem
        {
            Name = "Signature Size",
            Value = track.SignatureSize.ToString(),
            ByteRange = new ByteRange { PropertyName = "Signature Size", Offset = track.SignatureSizeOffset, Length = 2 }
        });

        properties.Add(new PropertyItem
        {
            Name = "Flags",
            Value = $"0x{track.Flags:X4}",
            ByteRange = new ByteRange { PropertyName = "Flags", Offset = track.FlagsOffset, Length = 2 }
        });

        if (track.Signature.Length > 0)
        {
            string sigHex = Convert.ToHexString(track.Signature.AsSpan(0, Math.Min(32, track.Signature.Length)));
            if (track.Signature.Length > 32)
                sigHex += "...";

            properties.Add(new PropertyItem
            {
                Name = "Signature",
                Value = sigHex,
                ByteRange = new ByteRange { PropertyName = "Signature", Offset = track.SignatureOffset, Length = track.SignatureSize }
            });
        }

        properties.Add(new PropertyItem
        {
            Name = "Block Position",
            Value = $"0x{track.BlockPosition:X8}"
        });

        properties.Add(new PropertyItem
        {
            Name = "Block Size",
            Value = $"{track.BlockSize:N0} bytes"
        });
    }

    private static void ShowSrsContainerChunkProperties(ObservableCollection<PropertyItem> properties, SrsContainerChunk chunk)
    {
        properties.Add(new PropertyItem { Name = "Label", Value = chunk.Label });
        properties.Add(new PropertyItem { Name = "Chunk ID", Value = chunk.ChunkId });
        properties.Add(new PropertyItem { Name = "Position", Value = $"0x{chunk.BlockPosition:X8}" });
        properties.Add(new PropertyItem { Name = "Header Size", Value = $"{chunk.HeaderSize} bytes" });
        properties.Add(new PropertyItem { Name = "Payload Size", Value = $"{chunk.PayloadSize:N0} bytes" });
        properties.Add(new PropertyItem { Name = "Total Size", Value = $"{chunk.BlockSize:N0} bytes" });
    }

    private static byte[]? ReadFileSlice(string? filePath, long offset, int length)
    {
        if (filePath == null || length <= 0)
            return null;

        using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (offset >= fs.Length)
            return null;

        fs.Seek(offset, SeekOrigin.Begin);
        int toRead = (int)Math.Min(length, fs.Length - offset);
        byte[] buffer = new byte[toRead];
        int totalRead = 0;
        while (totalRead < toRead)
        {
            int read = fs.Read(buffer, totalRead, toRead - totalRead);
            if (read == 0) break;
            totalRead += read;
        }
        if (totalRead < toRead)
            Array.Resize(ref buffer, totalRead);
        return buffer;
    }

    private void AddComparedProperty(ObservableCollection<PropertyItem> properties, string name, string value, string? diffPropertyName)
    {
        bool isDiff = false;
        if (diffPropertyName != null && _compareResult != null)
        {
            isDiff = _compareResult.ArchiveDifferences.Any(d => d.PropertyName == diffPropertyName);
        }

        properties.Add(new PropertyItem
        {
            Name = name,
            Value = value,
            IsDifferent = isDiff
        });
    }

    #endregion

    public void Dispose()
    {
        _leftFileSource?.Dispose();
        _leftFileSource = null;
        _rightFileSource?.Dispose();
        _rightFileSource = null;
        GC.SuppressFinalize(this);
    }
}
