using System.Collections.ObjectModel;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RAR;
using ReScene.NET.Models;
using ReScene.NET.Services;
using SRR;

namespace ReScene.NET.ViewModels;

public partial class InspectorViewModel(IFileDialogService fileDialog) : ViewModelBase, IDisposable
{
    private readonly IFileDialogService _fileDialog = fileDialog;
    private SrrFileData? _srrData;
    private SrsInspectorData? _srsData;
    private List<RARDetailedBlock>? _rarDetailedBlocks;
    private string? _loadedFilePathInternal;
    private long _fileSize;
    private MemoryMappedDataSource? _fileDataSource;

    [ObservableProperty]
    private string _loadedFilePath = string.Empty;

    [RelayCommand]
    private async Task BrowseFileAsync()
    {
        string? path = await _fileDialog.OpenFileAsync("Open File to Inspect",
            ["Scene Files|*.srr;*.srs;*.rar", "SRR Files|*.srr", "SRS Files|*.srs", "RAR Files|*.rar", "All Files|*.*"]);

        if (path != null)
            LoadFile(path);
    }

    private bool CanCloseFile() => HasFile;

    [RelayCommand(CanExecute = nameof(CanCloseFile))]
    private void CloseFile()
    {
        _fileDataSource?.Dispose();
        _fileDataSource = null;

        _srrData = null;
        _srsData = null;
        _rarDetailedBlocks = null;
        _loadedFilePathInternal = null;
        _fileSize = 0;
        WarningMessage = null;

        TreeRoots.Clear();
        Properties.Clear();

        LoadedFilePath = string.Empty;
        HexDataSource = null;
        HexBlockOffset = 0;
        HexBlockLength = 0;
        HexSelectionOffset = -1;
        HexSelectionLength = 0;
        HasFile = false;
        HasProperties = false;
        StatusMessage = "No file loaded";
    }

    public ObservableCollection<TreeNodeViewModel> TreeRoots { get; } = [];
    public ObservableCollection<PropertyItem> Properties { get; } = [];

    [ObservableProperty]
    private TreeNodeViewModel? _selectedTreeNode;

    [ObservableProperty]
    private PropertyItem? _selectedProperty;

    [ObservableProperty]
    private string _treeFilterText = string.Empty;

    // Hex view properties
    [ObservableProperty]
    private IHexDataSource? _hexDataSource;

    [ObservableProperty]
    private long _hexBlockOffset;

    [ObservableProperty]
    private long _hexBlockLength;

    [ObservableProperty]
    private long _hexSelectionOffset = -1;

    [ObservableProperty]
    private long _hexSelectionLength;

    [ObservableProperty]
    private int _hexBytesPerLine = 16;

    [ObservableProperty]
    private bool _showHexView = true;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CloseFileCommand))]
    private bool _hasFile;

    [ObservableProperty]
    private bool _hasProperties;

    [ObservableProperty]
    private bool _isExporting;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasWarning))]
    private string? _warningMessage;

    public bool HasWarning => !string.IsNullOrEmpty(WarningMessage);

    // Status info
    [ObservableProperty]
    private string _statusMessage = "No file loaded";

    public void LoadFile(string filePath)
    {
        try
        {
            string ext = Path.GetExtension(filePath);
            bool isSrs = ext.Equals(".srs", StringComparison.OrdinalIgnoreCase);
            bool isRar = ext.Equals(".rar", StringComparison.OrdinalIgnoreCase);

            _srsData = null;
            _srrData = null;
            _rarDetailedBlocks = null;
            WarningMessage = null;

            // Dispose previous memory-mapped source
            _fileDataSource?.Dispose();
            _fileDataSource = null;

            if (isSrs)
            {
                _srsData = SrsInspectorData.Load(filePath);
            }
            else if (isRar)
            {
                _rarDetailedBlocks = RARDetailedParser.Parse(filePath);
            }
            else
            {
                _srrData = SrrFileData.Load(filePath);
            }

            LoadedFilePath = filePath;
            _loadedFilePathInternal = filePath;
            _fileSize = new FileInfo(filePath).Length;
            _fileDataSource = new MemoryMappedDataSource(filePath);

            BuildTree();
            HasFile = true;

            if (isSrs)
            {
                var srs = _srsData!.SrsFile;
                int blockCount = (srs.FileData != null ? 1 : 0) + srs.Tracks.Count + srs.ContainerChunks.Count;
                StatusMessage = $"{Path.GetFileName(filePath)} | {srs.ContainerType} | {blockCount} blocks | {_fileSize:N0} bytes";
            }
            else if (isRar)
            {
                int blockCount = _rarDetailedBlocks!.Count;
                bool isRAR5 = blockCount > 0 && _rarDetailedBlocks[0].BlockType == "Signature" &&
                              _rarDetailedBlocks[0].Fields.Count > 0 && _rarDetailedBlocks[0].Fields[0].Value.StartsWith("52 61 72 21 1A 07 01");
                string format = isRAR5 ? "RAR 5.x" : "RAR 4.x";
                StatusMessage = $"{Path.GetFileName(filePath)} | {format} | {blockCount} blocks | {_fileSize:N0} bytes";

                // Detect custom packer sentinels in RAR file headers
                if (DetectCustomPackerInRarBlocks(_rarDetailedBlocks))
                    WarningMessage = "Custom RAR packer detected — file size fields may be unreliable. Known groups: RELOADED, HI2U, QCF.";
            }
            else
            {
                int blockCount = 0;
                var srr = _srrData!.SrrFile;
                if (srr.HeaderBlock != null) blockCount++;
                blockCount += srr.OsoHashBlocks.Count + srr.RarPaddingBlocks.Count
                            + srr.RarFiles.Count + srr.StoredFiles.Count;
                StatusMessage = $"{Path.GetFileName(filePath)} | {blockCount} blocks | {_fileSize:N0} bytes";

                // SRRFile already detects custom packer headers during Load
                if (srr.HasCustomPackerHeaders)
                {
                    string groups = srr.CustomPackerDetected == CustomPackerType.AllOnesWithLargeFlag
                        ? "RELOADED, HI2U" : "QCF";
                    WarningMessage = $"Custom RAR packer detected ({srr.CustomPackerDetected}) — file size fields may be unreliable. Known groups: {groups}.";
                }
            }
        }
        catch (Exception ex)
        {
            StatusMessage = $"Error: {ex.Message}";
            HasFile = false;
        }
    }

    partial void OnSelectedTreeNodeChanged(TreeNodeViewModel? value)
    {
        Properties.Clear();
        HasProperties = false;
        HexSelectionOffset = -1;
        HexSelectionLength = 0;
        ExportBlockCommand.NotifyCanExecuteChanged();

        if (value?.Tag is RARDetailedBlock detailedBlock)
        {
            ShowDetailedBlockProperties(detailedBlock);
            SetHexBlock(detailedBlock.StartOffset, detailedBlock.TotalSize);
            HasProperties = true;
        }
        else if (value?.Tag is SrrHeaderBlock header)
        {
            ShowSrrHeaderProperties(header);
            SetHexBlock(header.BlockPosition, header.HeaderSize);
            HasProperties = true;
        }
        else if (value?.Tag is SrrOsoHashBlock oso)
        {
            ShowOsoHashProperties(oso);
            SetHexBlock(oso.BlockPosition, oso.HeaderSize);
            HasProperties = true;
        }
        else if (value?.Tag is SrrRarPaddingBlock padding)
        {
            ShowRarPaddingProperties(padding);
            SetHexBlock(padding.BlockPosition, padding.HeaderSize + padding.AddSize);
            HasProperties = true;
        }
        else if (value?.Tag is SrrStoredFileBlock stored)
        {
            ShowStoredFileProperties(stored);
            SetHexBlock(stored.BlockPosition, stored.HeaderSize + stored.AddSize);
            HasProperties = true;
        }
        else if (value?.Tag is SrrRarFileBlock rar)
        {
            ShowRarFileProperties(rar);
            SetHexBlock(rar.BlockPosition, rar.HeaderSize + rar.AddSize);
            HasProperties = true;
        }
        else if (value?.Tag is SRRFile srr)
        {
            ShowArchiveInfoProperties(srr);
            ShowFullHex();
            HasProperties = true;
        }
        else if (value?.Tag is SRSFile srsFile)
        {
            ShowSrsSummaryProperties(srsFile);
            ShowFullHex();
            HasProperties = true;
        }
        else if (value?.Tag is SrsFileDataBlock srsFileData)
        {
            ShowSrsFileDataProperties(srsFileData);
            SetHexBlock(srsFileData.BlockPosition, srsFileData.BlockSize);
            HasProperties = true;
        }
        else if (value?.Tag is SrsTrackDataBlock srsTrack)
        {
            ShowSrsTrackDataProperties(srsTrack);
            SetHexBlock(srsTrack.BlockPosition, srsTrack.BlockSize);
            HasProperties = true;
        }
        else if (value?.Tag is SrsContainerChunk srsChunk)
        {
            ShowSrsChunkProperties(srsChunk);
            SetHexBlock(srsChunk.BlockPosition, srsChunk.BlockSize);
            HasProperties = true;
        }
        else
        {
            ShowFullHex();
        }
    }

    partial void OnSelectedPropertyChanged(PropertyItem? value)
    {
        if (value?.ByteRange is { } range)
        {
            HexSelectionOffset = range.Offset;
            HexSelectionLength = range.Length;
        }
        else
        {
            HexSelectionOffset = -1;
            HexSelectionLength = 0;
        }
    }

    partial void OnTreeFilterTextChanged(string value)
    {
        foreach (var root in TreeRoots)
            root.ApplyFilter(value);
    }

    private bool CanExportBlock() => HasFile && HexBlockLength > 0;

    [RelayCommand(CanExecute = nameof(CanExportBlock))]
    private async Task ExportBlockAsync()
    {
        if (!HasFile || HexBlockLength <= 0 || string.IsNullOrEmpty(_loadedFilePathInternal))
            return;

        // Pick a sensible default filename from the selected node
        string defaultName = SelectedTreeNode?.Tag switch
        {
            SrrStoredFileBlock stored => Path.GetFileName(stored.FileName),
            RARDetailedBlock { ItemName: { } name } => name,
            _ => "block.bin"
        };

        string? outputPath = await _fileDialog.SaveFileAsync(
            "Export Block Data",
            Path.GetExtension(defaultName),
            ["All Files|*.*"],
            defaultName);

        if (outputPath == null)
            return;

        long offset = HexBlockOffset;
        long length = HexBlockLength;

        IsExporting = true;
        StatusMessage = $"Exporting {length:N0} bytes...";
        try
        {
            await Task.Run(() =>
            {
                using var input = new FileStream(_loadedFilePathInternal!, FileMode.Open, FileAccess.Read, FileShare.Read);
                using var output = File.Create(outputPath);
                input.Seek(offset, SeekOrigin.Begin);
                byte[] buffer = new byte[81920];
                long remaining = length;
                while (remaining > 0)
                {
                    int toRead = (int)Math.Min(buffer.Length, remaining);
                    int read = input.Read(buffer, 0, toRead);
                    if (read == 0) break;
                    output.Write(buffer, 0, read);
                    remaining -= read;
                }
            });

            StatusMessage = $"Exported: {Path.GetFileName(outputPath)} ({length:N0} bytes)";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Export error: {ex.Message}";
        }
        finally
        {
            IsExporting = false;
        }
    }

    private void BuildTree()
    {
        TreeRoots.Clear();

        if (_srsData != null)
        {
            BuildSrsTree();
            return;
        }

        if (_rarDetailedBlocks != null)
        {
            BuildRarTree();
            return;
        }

        if (_srrData == null) return;
        BuildSrrTree();
    }

    private static bool DetectCustomPackerInRarBlocks(List<RARDetailedBlock> blocks)
    {
        foreach (var block in blocks)
        {
            if (block.BlockType != "File Header") continue;

            // Check for sentinel descriptions added by the detailed parser
            foreach (var field in block.Fields)
            {
                if (field.Description != null && field.Description.Contains("Custom packer sentinel"))
                    return true;
            }
        }
        return false;
    }

    private void BuildRarTree()
    {
        var blocks = _rarDetailedBlocks!;
        bool isRAR5 = blocks.Count > 0 && blocks[0].BlockType == "Signature" &&
                      blocks[0].Fields.Count > 0 && blocks[0].Fields[0].Value.StartsWith("52 61 72 21 1A 07 01");

        string rootName = isRAR5 ? $"RAR 5.x Archive ({blocks.Count} blocks)" : $"RAR 4.x Archive ({blocks.Count} blocks)";

        var root = new TreeNodeViewModel { Text = rootName, Tag = "root", IsExpanded = true };

        for (int i = 0; i < blocks.Count; i++)
        {
            var block = blocks[i];
            string blockType = block.HasData && block.BlockType.Contains("File") ? "File Data" : block.BlockType;
            string blockLabel = $"[{i}] {blockType}";

            if (!string.IsNullOrEmpty(block.ItemName))
                blockLabel = $"[{i}] {blockType}: {block.ItemName}";

            root.Children.Add(new TreeNodeViewModel { Text = blockLabel, Tag = block });
        }

        TreeRoots.Add(root);
    }

    private void BuildSrrTree()
    {
        var srr = _srrData!.SrrFile;

        var root = new TreeNodeViewModel { Text = "SRR File", Tag = "root", IsExpanded = true };

        if (srr.HeaderBlock != null)
        {
            root.Children.Add(new TreeNodeViewModel { Text = "SRR Header", Tag = srr.HeaderBlock });
        }

        if (srr.RarFiles.Count > 0)
        {
            root.Children.Add(new TreeNodeViewModel { Text = "RAR Archive Info", Tag = srr });
        }

        if (srr.OsoHashBlocks.Count > 0)
        {
            var osoNode = new TreeNodeViewModel
            {
                Text = $"OSO Hashes ({srr.OsoHashBlocks.Count})",
                Tag = "container",
                IsExpanded = true
            };
            foreach (var oso in srr.OsoHashBlocks)
            {
                osoNode.Children.Add(new TreeNodeViewModel { Text = oso.FileName, Tag = oso });
            }
            root.Children.Add(osoNode);
        }

        if (srr.RarPaddingBlocks.Count > 0)
        {
            var paddingNode = new TreeNodeViewModel
            {
                Text = $"RAR Padding ({srr.RarPaddingBlocks.Count})",
                Tag = "container"
            };
            foreach (var padding in srr.RarPaddingBlocks)
            {
                paddingNode.Children.Add(new TreeNodeViewModel { Text = padding.RarFileName, Tag = padding });
            }
            root.Children.Add(paddingNode);
        }

        if (srr.RarFiles.Count > 0)
        {
            var volumesNode = new TreeNodeViewModel
            {
                Text = $"RAR Volumes ({srr.RarFiles.Count})",
                Tag = "container",
                IsExpanded = true
            };
            foreach (var rar in srr.RarFiles)
            {
                var volNode = new TreeNodeViewModel { Text = rar.FileName, Tag = rar };

                if (_srrData.VolumeDetailedBlocks.TryGetValue(rar.FileName, out var detailedBlocks))
                {
                    for (int i = 0; i < detailedBlocks.Count; i++)
                    {
                        var block = detailedBlocks[i];
                        string blockType = block.HasData && block.BlockType.Contains("File") ? "File Data" : block.BlockType;
                        string blockLabel = $"[{i}] {blockType}";
                        if (!string.IsNullOrEmpty(block.ItemName))
                            blockLabel = $"[{i}] {blockType}: {block.ItemName}";

                        volNode.Children.Add(new TreeNodeViewModel { Text = blockLabel, Tag = block });
                    }
                }

                volumesNode.Children.Add(volNode);
            }
            root.Children.Add(volumesNode);
        }

        if (srr.StoredFiles.Count > 0)
        {
            var storedNode = new TreeNodeViewModel
            {
                Text = $"Stored Files ({srr.StoredFiles.Count})",
                Tag = "container",
                IsExpanded = true
            };
            foreach (var stored in srr.StoredFiles)
            {
                storedNode.Children.Add(new TreeNodeViewModel { Text = stored.FileName, Tag = stored });
            }
            root.Children.Add(storedNode);
        }

        if (srr.ArchivedFiles.Count > 0)
        {
            var archivedNode = new TreeNodeViewModel
            {
                Text = $"Archived Files ({srr.ArchivedFiles.Count})",
                Tag = "container"
            };
            foreach (var file in srr.ArchivedFiles.OrderBy(f => f))
            {
                string label = file;
                if (srr.ArchivedFileCrcs.TryGetValue(file, out var crc))
                    label = $"{file} [CRC: {crc}]";
                archivedNode.Children.Add(new TreeNodeViewModel { Text = label, Tag = "archived" });
            }
            root.Children.Add(archivedNode);
        }

        TreeRoots.Add(root);
    }

    private void BuildSrsTree()
    {
        var srs = _srsData!.SrsFile;
        var root = new TreeNodeViewModel
        {
            Text = $"SRS File ({srs.ContainerType})",
            Tag = srs,
            IsExpanded = true
        };

        if (srs.FileData != null)
        {
            root.Children.Add(new TreeNodeViewModel
            {
                Text = $"FileData: {srs.FileData.FileName}",
                Tag = srs.FileData
            });
        }

        if (srs.Tracks.Count > 0)
        {
            var tracksNode = new TreeNodeViewModel
            {
                Text = $"Tracks ({srs.Tracks.Count})",
                Tag = "container",
                IsExpanded = true
            };
            foreach (var track in srs.Tracks)
            {
                tracksNode.Children.Add(new TreeNodeViewModel
                {
                    Text = $"Track {track.TrackNumber} ({FormatSize((long)track.DataLength)})",
                    Tag = track
                });
            }
            root.Children.Add(tracksNode);
        }

        if (srs.ContainerChunks.Count > 0)
        {
            var chunksNode = new TreeNodeViewModel
            {
                Text = $"Container Chunks ({srs.ContainerChunks.Count})",
                Tag = "container"
            };
            foreach (var chunk in srs.ContainerChunks)
            {
                chunksNode.Children.Add(new TreeNodeViewModel
                {
                    Text = $"{chunk.Label} (0x{chunk.BlockPosition:X}, {chunk.BlockSize:N0} B)",
                    Tag = chunk
                });
            }
            root.Children.Add(chunksNode);
        }

        TreeRoots.Add(root);
    }

    private void ShowSrsSummaryProperties(SRSFile srs)
    {
        AddProperty("Container Type", srs.ContainerType.ToString());

        if (srs.FileData != null)
        {
            AddProperty("Sample File", srs.FileData.FileName);
            AddProperty("Sample Size", $"{srs.FileData.SampleSize:N0} bytes ({FormatSize((long)srs.FileData.SampleSize)})");
            AddProperty("Sample CRC32", $"0x{srs.FileData.Crc32:X8}");
            if (!string.IsNullOrEmpty(srs.FileData.AppName))
                AddProperty("App Name", srs.FileData.AppName);
        }

        AddProperty("Track Count", srs.Tracks.Count.ToString());
        AddProperty("Container Chunks", srs.ContainerChunks.Count.ToString());
    }

    private void ShowSrsFileDataProperties(SrsFileDataBlock block)
    {
        long p = block.FrameOffset;
        AddProperty("Frame Offset", $"0x{block.FrameOffset:X8}",
            new ByteRange { Offset = p, Length = block.FrameHeaderSize });
        AddProperty("Frame Header Size", $"{block.FrameHeaderSize} bytes");
        AddProperty("Block Size", $"{block.BlockSize:N0} bytes");

        AddProperty("Flags", $"0x{block.Flags:X4}",
            new ByteRange { Offset = block.FlagsOffset, Length = 2 });
        AddProperty("App Name Size", $"{block.AppNameSize}",
            new ByteRange { Offset = block.AppNameSizeOffset, Length = 2 });
        if (block.AppNameSize > 0)
            AddProperty("App Name", block.AppName,
                new ByteRange { Offset = block.AppNameOffset, Length = block.AppNameSize });
        AddProperty("File Name Size", $"{block.FileNameSize}",
            new ByteRange { Offset = block.FileNameSizeOffset, Length = 2 });
        AddProperty("File Name", block.FileName,
            new ByteRange { Offset = block.FileNameOffset, Length = block.FileNameSize });
        AddProperty("Sample Size", $"{block.SampleSize:N0} bytes ({FormatSize((long)block.SampleSize)})",
            new ByteRange { Offset = block.SampleSizeOffset, Length = 8 });
        AddProperty("CRC-32", $"0x{block.Crc32:X8}",
            new ByteRange { Offset = block.Crc32Offset, Length = 4 });
    }

    private void ShowSrsTrackDataProperties(SrsTrackDataBlock block)
    {
        long p = block.FrameOffset;
        AddProperty("Frame Offset", $"0x{block.FrameOffset:X8}",
            new ByteRange { Offset = p, Length = block.FrameHeaderSize });
        AddProperty("Frame Header Size", $"{block.FrameHeaderSize} bytes");
        AddProperty("Block Size", $"{block.BlockSize:N0} bytes");

        AddProperty("Flags", $"0x{block.Flags:X4}",
            new ByteRange { Offset = block.FlagsOffset, Length = 2 });

        string trackLabel = (block.Flags & 0x8) != 0 ? "Track Number (32-bit)" : "Track Number (16-bit)";
        AddProperty(trackLabel, block.TrackNumber.ToString(),
            new ByteRange { Offset = block.TrackNumberOffset, Length = block.TrackNumberFieldSize });

        string dataLabel = (block.Flags & 0x4) != 0 ? "Data Length (64-bit)" : "Data Length (32-bit)";
        AddProperty(dataLabel, $"{block.DataLength:N0} bytes ({FormatSize((long)block.DataLength)})",
            new ByteRange { Offset = block.DataLengthOffset, Length = block.DataLengthFieldSize });

        AddProperty("Match Offset", $"0x{block.MatchOffset:X}",
            new ByteRange { Offset = block.MatchOffsetOffset, Length = 8 });
        AddProperty("Signature Size", $"{block.SignatureSize} bytes",
            new ByteRange { Offset = block.SignatureSizeOffset, Length = 2 });

        if (block.SignatureSize > 0)
        {
            string sigHex = BitConverter.ToString(block.Signature).Replace("-", " ");
            if (sigHex.Length > 80) sigHex = sigHex[..80] + "...";
            AddProperty("Signature", sigHex,
                new ByteRange { Offset = block.SignatureOffset, Length = block.SignatureSize });
        }
    }

    private void ShowSrsChunkProperties(SrsContainerChunk chunk)
    {
        AddProperty("Label", chunk.Label);
        AddProperty("Chunk ID", chunk.ChunkId);
        AddProperty("Position", $"0x{chunk.BlockPosition:X8}",
            new ByteRange { Offset = chunk.BlockPosition, Length = chunk.HeaderSize });
        AddProperty("Total Size", $"{chunk.BlockSize:N0} bytes");
        AddProperty("Header Size", $"{chunk.HeaderSize} bytes");
        AddProperty("Payload Size", $"{chunk.PayloadSize:N0} bytes");
    }

    private void SetHexBlock(long offset, long size)
    {
        // Clamp to actual file data so we don't show empty rows
        // (e.g. RAR headers in SRR reference data that isn't stored)
        long end = Math.Min(offset + size, _fileSize);
        long clampedSize = Math.Max(0, end - offset);

        HexBlockOffset = offset;
        HexBlockLength = clampedSize;
        HexDataSource = _fileDataSource != null
            ? new HexDataSourceSlice(_fileDataSource, offset, clampedSize)
            : null;
    }

    private const long MaxHexSliceSize = 100L * 1024 * 1024; // 100 MB

    private void ShowFullHex()
    {
        long len = Math.Min(_fileSize, MaxHexSliceSize);
        HexBlockOffset = 0;
        HexBlockLength = len;
        HexDataSource = _fileDataSource != null
            ? new HexDataSourceSlice(_fileDataSource, 0, len)
            : null;
    }

    private bool _disposed;

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    protected virtual void Dispose(bool disposing)
    {
        if (_disposed) return;

        if (disposing)
        {
            // dispose managed resources
            _fileDataSource?.Dispose();
            _fileDataSource = null;
        }

        // note: no unmanaged resources to release here

        _disposed = true;
    }

    private void AddProperty(string name, string value, ByteRange? range = null, bool indented = false, bool warning = false)
    {
        Properties.Add(new PropertyItem
        {
            Name = name,
            Value = value,
            ByteRange = range,
            IsIndented = indented,
            IsWarning = warning
        });
    }

    private void ShowSrrHeaderProperties(SrrHeaderBlock header)
    {
        long pos = header.BlockPosition;

        AddProperty("Header CRC", $"0x{header.Crc:X4}",
            new ByteRange { Offset = pos, Length = 2 });
        AddProperty("Block Type", $"0x{(byte)header.BlockType:X2} ({header.BlockType})",
            new ByteRange { Offset = pos + 2, Length = 1 });
        AddProperty("Flags", $"0x{header.Flags:X4}",
            new ByteRange { Offset = pos + 3, Length = 2 });
        AddProperty("Header Size", $"{header.HeaderSize} bytes",
            new ByteRange { Offset = pos + 5, Length = 2 });

        if (!string.IsNullOrEmpty(header.AppName))
        {
            AddProperty("App Name", header.AppName,
                new ByteRange { Offset = pos + 7, Length = header.AppName.Length + 2 });
        }
    }

    private void ShowStoredFileProperties(SrrStoredFileBlock stored)
    {
        long p = stored.BlockPosition;

        AddProperty("Header CRC", $"0x{stored.Crc:X4}",
            new ByteRange { Offset = p, Length = 2 });
        AddProperty("Block Type", $"0x{(byte)stored.BlockType:X2} ({stored.BlockType})",
            new ByteRange { Offset = p + 2, Length = 1 });
        AddProperty("Flags", $"0x{stored.Flags:X4}",
            new ByteRange { Offset = p + 3, Length = 2 });
        AddProperty("Header Size", $"{stored.HeaderSize} bytes",
            new ByteRange { Offset = p + 5, Length = 2 });
        p += 7;

        AddProperty("Add Size", $"{stored.AddSize} bytes",
            new ByteRange { Offset = p, Length = 4 });
        p += 4;

        int nameLen = Encoding.UTF8.GetByteCount(stored.FileName);
        AddProperty("Name Length", $"{nameLen} bytes",
            new ByteRange { Offset = p, Length = 2 });
        p += 2;
        AddProperty("File Name", stored.FileName,
            new ByteRange { Offset = p, Length = nameLen });

        if (stored.FileLength > 0)
        {
            AddProperty("File Data", $"{stored.FileLength:N0} bytes ({FormatSize(stored.FileLength)})",
                new ByteRange { Offset = stored.DataOffset, Length = (int)Math.Min(stored.FileLength, int.MaxValue) });
        }
    }

    private void ShowRarFileProperties(SrrRarFileBlock rar)
    {
        long p = rar.BlockPosition;

        AddProperty("Header CRC", $"0x{rar.Crc:X4}",
            new ByteRange { Offset = p, Length = 2 });
        AddProperty("Block Type", $"0x{(byte)rar.BlockType:X2} ({rar.BlockType})",
            new ByteRange { Offset = p + 2, Length = 1 });
        AddProperty("Flags", $"0x{rar.Flags:X4}",
            new ByteRange { Offset = p + 3, Length = 2 });
        AddProperty("Header Size", $"{rar.HeaderSize} bytes",
            new ByteRange { Offset = p + 5, Length = 2 });
        p += 7;

        if ((rar.Flags & (ushort)SRRBlockFlags.LongBlock) != 0)
        {
            AddProperty("Add Size", $"{rar.AddSize} bytes",
                new ByteRange { Offset = p, Length = 4 });
            p += 4;
        }

        int nameLen = Encoding.UTF8.GetByteCount(rar.FileName);
        AddProperty("Name Length", $"{nameLen} bytes",
            new ByteRange { Offset = p, Length = 2 });
        p += 2;
        AddProperty("RAR Volume", rar.FileName,
            new ByteRange { Offset = p, Length = nameLen });
    }

    private void ShowOsoHashProperties(SrrOsoHashBlock oso)
    {
        long p = oso.BlockPosition;

        AddProperty("Header CRC", $"0x{oso.Crc:X4}",
            new ByteRange { Offset = p, Length = 2 });
        AddProperty("Block Type", $"0x{(byte)oso.BlockType:X2} ({oso.BlockType})",
            new ByteRange { Offset = p + 2, Length = 1 });
        AddProperty("Flags", $"0x{oso.Flags:X4}",
            new ByteRange { Offset = p + 3, Length = 2 });
        AddProperty("Header Size", $"{oso.HeaderSize} bytes",
            new ByteRange { Offset = p + 5, Length = 2 });
        p += 7;

        if ((oso.Flags & (ushort)SRRBlockFlags.LongBlock) != 0)
        {
            AddProperty("Add Size", $"{oso.AddSize} bytes",
                new ByteRange { Offset = p, Length = 4 });
            p += 4;
        }

        // Binary order: FileSize(8), OsoHash(8), NameLen(2), FileName(var)
        AddProperty("File Size", $"{oso.FileSize:N0} bytes ({FormatSize((long)oso.FileSize)})",
            new ByteRange { Offset = p, Length = 8 });
        p += 8;
        AddProperty("OSO Hash", BitConverter.ToString(oso.OsoHash).Replace("-", ""),
            new ByteRange { Offset = p, Length = 8 });
        p += 8;

        int nameLen = Encoding.UTF8.GetByteCount(oso.FileName);
        AddProperty("Name Length", $"{nameLen} bytes",
            new ByteRange { Offset = p, Length = 2 });
        p += 2;
        AddProperty("File Name", oso.FileName,
            new ByteRange { Offset = p, Length = nameLen });
    }

    private void ShowRarPaddingProperties(SrrRarPaddingBlock padding)
    {
        long p = padding.BlockPosition;

        AddProperty("Header CRC", $"0x{padding.Crc:X4}",
            new ByteRange { Offset = p, Length = 2 });
        AddProperty("Block Type", $"0x{(byte)padding.BlockType:X2} ({padding.BlockType})",
            new ByteRange { Offset = p + 2, Length = 1 });
        AddProperty("Flags", $"0x{padding.Flags:X4}",
            new ByteRange { Offset = p + 3, Length = 2 });
        AddProperty("Header Size", $"{padding.HeaderSize} bytes",
            new ByteRange { Offset = p + 5, Length = 2 });
        p += 7;

        if ((padding.Flags & (ushort)SRRBlockFlags.LongBlock) != 0)
        {
            AddProperty("Add Size", $"{padding.AddSize} bytes",
                new ByteRange { Offset = p, Length = 4 });
            p += 4;
        }

        int nameLen = Encoding.UTF8.GetByteCount(padding.RarFileName);
        AddProperty("Name Length", $"{nameLen} bytes",
            new ByteRange { Offset = p, Length = 2 });
        p += 2;
        AddProperty("RAR File Name", padding.RarFileName,
            new ByteRange { Offset = p, Length = nameLen });
        AddProperty("Padding Size", $"{padding.PaddingSize:N0} bytes");
    }

    private void ShowDetailedBlockProperties(RARDetailedBlock block)
    {
        foreach (var field in block.Fields)
        {
            string value = field.Value;
            bool isWarning = field.Description != null && field.Description.Contains("Custom packer sentinel");
            if (!string.IsNullOrEmpty(field.Description) && field.Description != field.Value)
                value = $"{field.Value} ({field.Description})";

            ByteRange? range = field.Length > 0
                ? new ByteRange { PropertyName = field.Name, Offset = field.Offset, Length = field.Length }
                : null;

            AddProperty(field.Name, value, range, warning: isWarning);

            foreach (var child in field.Children)
            {
                long childOffset = child.Length > 0 ? child.Offset : field.Offset;
                int childLength = child.Length > 0 ? child.Length : field.Length;

                ByteRange? childRange = childLength > 0
                    ? new ByteRange { PropertyName = child.Name, Offset = childOffset, Length = childLength }
                    : null;

                AddProperty($"  {child.Name}", child.Value, childRange, indented: true);
            }
        }

        // Add data row if not already present
        if (block.HasData && block.DataSize > 0)
        {
            bool hasData = false;
            foreach (var p in Properties)
            {
                if (p.Name == "Data") { hasData = true; break; }
            }
            if (!hasData)
            {
                long dataOffset = block.StartOffset + block.HeaderSize;
                AddProperty("Data", $"{block.DataSize:N0} bytes (offset 0x{dataOffset:X8})",
                    new ByteRange { PropertyName = "Data", Offset = dataOffset, Length = (int)Math.Min(block.DataSize, int.MaxValue) });
            }
        }
    }

    private void ShowArchiveInfoProperties(SRRFile srr)
    {
        AddProperty("RAR Version", srr.RARVersion.HasValue
            ? (srr.RARVersion == 50 ? "RAR 5.0" : $"RAR {srr.RARVersion.Value / 10}.{srr.RARVersion.Value % 10}")
            : "Unknown");

        if (srr.CompressionMethod.HasValue)
            AddProperty("Compression Method", GetCompressionMethodName((byte)srr.CompressionMethod.Value));
        if (srr.DictionarySize.HasValue)
            AddProperty("Dictionary Size", $"{srr.DictionarySize.Value} KB");

        AddProperty("Solid Archive", FormatBool(srr.IsSolidArchive));
        AddProperty("Volume Archive", FormatBool(srr.IsVolumeArchive));
        AddProperty("Recovery Record", FormatBool(srr.HasRecoveryRecord));
        AddProperty("Encrypted Headers", FormatBool(srr.HasEncryptedHeaders));
        AddProperty("New Volume Naming", FormatBool(srr.HasNewVolumeNaming));
        AddProperty("First Volume Flag", FormatBool(srr.HasFirstVolumeFlag));
        AddProperty("Large Files (64-bit)", FormatBool(srr.HasLargeFiles));
        AddProperty("Unicode Names", FormatBool(srr.HasUnicodeNames));
        AddProperty("Extended Time", FormatBool(srr.HasExtendedTime));

        if (srr.VolumeSizeBytes.HasValue)
            AddProperty("Volume Size", $"{srr.VolumeSizeBytes.Value:N0} bytes ({FormatSize(srr.VolumeSizeBytes.Value)})");
        if (srr.RarVolumeSizes.Count > 0)
        {
            AddProperty("Volume Sizes Count", srr.RarVolumeSizes.Count.ToString());
            var uniqueSizes = srr.RarVolumeSizes.Distinct().OrderByDescending(s => s).ToList();
            for (int i = 0; i < Math.Min(uniqueSizes.Count, 5); i++)
            {
                AddProperty($"  Unique Size {i + 1}",
                    $"{uniqueSizes[i]:N0} bytes ({FormatSize(uniqueSizes[i])})", indented: true);
            }
            if (uniqueSizes.Count > 5)
                AddProperty("  ...", $"({uniqueSizes.Count - 5} more)", indented: true);
        }

        AddProperty("RAR Volumes", srr.RarFiles.Count.ToString());
        AddProperty("Stored Files", srr.StoredFiles.Count.ToString());
        AddProperty("Archived Files", srr.ArchivedFiles.Count.ToString());
        AddProperty("Archived Directories", srr.ArchivedDirectories.Count.ToString());

        AddProperty("File Timestamps", srr.ArchivedFileTimestamps.Count.ToString());
        AddProperty("File Creation Times", srr.ArchivedFileCreationTimes.Count.ToString());
        AddProperty("File Access Times", srr.ArchivedFileAccessTimes.Count.ToString());
        AddProperty("Dir Timestamps", srr.ArchivedDirectoryTimestamps.Count.ToString());
        AddProperty("Dir Creation Times", srr.ArchivedDirectoryCreationTimes.Count.ToString());
        AddProperty("Dir Access Times", srr.ArchivedDirectoryAccessTimes.Count.ToString());

        AddProperty("File CRCs", srr.ArchivedFileCrcs.Count.ToString());
        AddProperty("Header CRC Errors", srr.HeaderCrcMismatches.ToString());
        AddProperty("Has Comment", !string.IsNullOrEmpty(srr.ArchiveComment) ? "Yes" : "No");

        if (!string.IsNullOrEmpty(srr.ArchiveComment))
            AddProperty("Comment", srr.ArchiveComment);
    }

    private static string FormatBool(bool? value) =>
        value.HasValue ? (value.Value ? "Yes" : "No") : "Unknown";

    private static string FormatSize(long bytes)
    {
        string[] suffixes = ["B", "KB", "MB", "GB", "TB"];
        int i = 0;
        double size = bytes;
        while (size >= 1024 && i < suffixes.Length - 1)
        {
            size /= 1024;
            i++;
        }
        return $"{size:0.##} {suffixes[i]}";
    }

    private static string GetCompressionMethodName(byte method) => method switch
    {
        0x00 or 0x30 => "Store",
        0x01 or 0x31 => "Fastest",
        0x02 or 0x32 => "Fast",
        0x03 or 0x33 => "Normal",
        0x04 or 0x34 => "Good",
        0x05 or 0x35 => "Best",
        _ => $"Unknown (0x{method:X2})"
    };
}
