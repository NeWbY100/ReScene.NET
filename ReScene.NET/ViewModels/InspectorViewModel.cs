using System.Collections.ObjectModel;
using System.Text;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EBMLElement = ReScene.Core.Comparison.EBMLElement;
using MKVFileData = ReScene.Core.Comparison.MKVFileData;
using ReScene.Hex;
using ReScene.NET.Helpers;
using ReScene.NET.Models;
using ReScene.NET.Services;
using ReScene.RAR;
using ReScene.SRR;
using ReScene.SRS;

namespace ReScene.NET.ViewModels;

public partial class InspectorViewModel(IFileDialogService fileDialog, ISrrEditingService srrEditingService, ISrrVerifyService verifyService, IPropertyExportService propertyExportService, IAppSettingsService? settingsService = null) : ViewModelBase, IDisposable
{
    private const int ExportBufferSize = 80 * 1024;

    private readonly IFileDialogService _fileDialog = fileDialog;
    private readonly ISrrEditingService _sRREditingService = srrEditingService;
    private readonly ISrrVerifyService _verifyService = verifyService;
    private readonly IPropertyExportService _propertyExportService = propertyExportService;
    private readonly IAppSettingsService? _settingsService = settingsService;
    private SRRFileData? _sRRData;
    private SRSInspectorData? _sRSData;
    private IReadOnlyList<RARDetailedBlock>? _rarDetailedBlocks;
    private MKVFileData? _mkvData;
    private string? _loadedFilePathInternal;
    private long _fileSize;
    private MemoryMappedDataSource? _fileDataSource;

    [ObservableProperty]
    public partial string LoadedFilePath { get; set; } = string.Empty;

    [RelayCommand]
    private async Task BrowseFileAsync()
    {
        string? path = await _fileDialog.OpenFileAsync("Open File to Inspect",
            FileDialogFilters.InspectFiles);

        if (path is not null)
        {
            LoadFile(path);
        }
    }

    private bool CanCloseFile() => HasFile;

    [RelayCommand(CanExecute = nameof(CanCloseFile))]
    private void CloseFile()
    {
        _fileDataSource?.Dispose();
        _fileDataSource = null;

        _sRRData = null;
        _sRSData = null;
        _rarDetailedBlocks = null;
        _mkvData = null;
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
        IsVerifyResultVisible = false;
        StatusMessage = "No file loaded";
        OnPropertyChanged(nameof(IsSRRLoaded));
        OnPropertyChanged(nameof(IsStoredFileSelected));
        VerifyIntegrityCommand.NotifyCanExecuteChanged();
        RenameStoredFileCommand.NotifyCanExecuteChanged();
        MoveStoredFileUpCommand.NotifyCanExecuteChanged();
        MoveStoredFileDownCommand.NotifyCanExecuteChanged();
        ExportSelectedPropertiesCommand.NotifyCanExecuteChanged();
        ExportTreeCommand.NotifyCanExecuteChanged();
    }

    public ObservableCollection<TreeNodeViewModel> TreeRoots { get; } = [];
    public ObservableCollection<PropertyItem> Properties { get; } = [];

    /// <summary>
    /// Gets whether the currently loaded file is an SRR file.
    /// </summary>
    public bool IsSRRLoaded => IsSRRFileLoaded();

    /// <summary>
    /// Gets whether the selected tree node is a stored file block.
    /// </summary>
    public bool IsStoredFileSelected => IsSRRFileLoaded() && SelectedTreeNode?.Tag is SRRStoredFileBlock;

    [ObservableProperty]
    public partial TreeNodeViewModel? SelectedTreeNode { get; set; }

    [ObservableProperty]
    public partial PropertyItem? SelectedProperty { get; set; }

    [ObservableProperty]
    public partial string TreeFilterText { get; set; } = string.Empty;

    // Hex view properties
    [ObservableProperty]
    public partial IHexDataSource? HexDataSource { get; set; }

    [ObservableProperty]
    public partial long HexBlockOffset { get; set; }

    [ObservableProperty]
    public partial long HexBlockLength { get; set; }

    [ObservableProperty]
    public partial long HexSelectionOffset { get; set; } = -1;

    [ObservableProperty]
    public partial long HexSelectionLength { get; set; }

    [ObservableProperty]
    public partial int HexBytesPerLine { get; set; } = 16;

    [ObservableProperty]
    public partial bool ShowHexView { get; set; } = true;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CloseFileCommand))]
    public partial bool HasFile { get; set; }

    [ObservableProperty]
    public partial bool HasProperties { get; set; }

    [ObservableProperty]
    public partial bool IsExporting { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasWarning))]
    public partial string? WarningMessage { get; set; }

    public bool HasWarning => !string.IsNullOrEmpty(WarningMessage);

    // Status info
    [ObservableProperty]
    public partial string StatusMessage { get; set; } = "No file loaded";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(FindNextCommand))]
    [NotifyCanExecuteChangedFor(nameof(FindPreviousCommand))]
    public partial string HexSearchText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool HexSearchAsHex { get; set; } = true;

    [ObservableProperty]
    public partial bool IsHexSearchVisible { get; set; }

    [ObservableProperty]
    public partial string HexSearchStatus { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool HighlightAllMatches { get; set; }

    [ObservableProperty]
    public partial IReadOnlyList<HexMatchRange>? HexMatchRanges { get; set; }

    [ObservableProperty]
    public partial string VerifyResultText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsVerifyResultVisible { get; set; }

    public void LoadFile(string filePath)
    {
        try
        {
            string ext = Path.GetExtension(filePath);
            bool isSRS = ext.Equals(".srs", StringComparison.OrdinalIgnoreCase);
            bool isRar = ext.Equals(".rar", StringComparison.OrdinalIgnoreCase);
            bool isMkv = ext.Equals(".mkv", StringComparison.OrdinalIgnoreCase)
                || ext.Equals(".webm", StringComparison.OrdinalIgnoreCase);

            _sRSData = null;
            _sRRData = null;
            _rarDetailedBlocks = null;
            _mkvData = null;
            WarningMessage = null;

            // Dispose previous memory-mapped source
            _fileDataSource?.Dispose();
            _fileDataSource = null;

            if (isSRS)
            {
                _sRSData = SRSInspectorData.Load(filePath);
            }
            else if (isRar)
            {
                _rarDetailedBlocks = RARDetailedParser.Parse(filePath);
            }
            else if (isMkv)
            {
                _mkvData = MKVFileData.Load(filePath,
                    _settingsService?.Load().MkvMaxElements ?? MKVFileData.DefaultMaxElements);
            }
            else
            {
                _sRRData = SRRFileData.Load(filePath);
            }

            LoadedFilePath = filePath;
            _loadedFilePathInternal = filePath;
            _fileSize = new FileInfo(filePath).Length;
            _fileDataSource = new MemoryMappedDataSource(filePath);

            BuildTree();
            HasFile = true;
            OnPropertyChanged(nameof(IsSRRLoaded));
            ExportTreeCommand.NotifyCanExecuteChanged();
            OnPropertyChanged(nameof(IsStoredFileSelected));
            VerifyIntegrityCommand.NotifyCanExecuteChanged();

            if (isSRS)
            {
                SRSFile srs = _sRSData!.SRSFile;
                int blockCount = (srs.FileData is not null ? 1 : 0) + srs.Tracks.Count + srs.ContainerChunks.Count;
                StatusMessage = $"{Path.GetFileName(filePath)} | {srs.ContainerType} | {blockCount} blocks | {_fileSize:N0} bytes";
            }
            else if (isRar)
            {
                int blockCount = _rarDetailedBlocks!.Count;
                bool isRAR5 = blockCount > 0 && _rarDetailedBlocks[0].BlockType == "Signature" &&
                              _rarDetailedBlocks[0].Fields.Count > 0 && _rarDetailedBlocks[0].Fields[0].Value.StartsWith("52 61 72 21 1A 07 01", StringComparison.Ordinal);
                string format = isRAR5 ? "RAR 5.x" : "RAR 4.x";
                StatusMessage = $"{Path.GetFileName(filePath)} | {format} | {blockCount} blocks | {_fileSize:N0} bytes";

                // Detect custom packer sentinels in RAR file headers
                if (DetectCustomPackerInRarBlocks(_rarDetailedBlocks))
                {
                    WarningMessage = "Custom RAR packer detected — file size fields may be unreliable. Known groups: RELOADED, HI2U, QCF.";
                }
            }
            else if (isMkv)
            {
                int elementCount = CountElements(_mkvData!.Elements);
                StatusMessage = $"{Path.GetFileName(filePath)} | MKV | {_mkvData.TrackCount} track(s) | {elementCount:N0} elements | {_fileSize:N0} bytes";
            }
            else
            {
                int blockCount = 0;
                SRRFile srr = _sRRData!.SRRFile;
                if (srr.HeaderBlock is not null)
                {
                    blockCount++;
                }
                blockCount += srr.OSOHashBlocks.Count + srr.RARPaddingBlocks.Count
                            + srr.RARFiles.Count + srr.StoredFiles.Count;
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
        RemoveStoredFileFromSRRCommand.NotifyCanExecuteChanged();
        RenameStoredFileCommand.NotifyCanExecuteChanged();
        MoveStoredFileUpCommand.NotifyCanExecuteChanged();
        MoveStoredFileDownCommand.NotifyCanExecuteChanged();
        VerifyIntegrityCommand.NotifyCanExecuteChanged();
        ExportSelectedPropertiesCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(IsStoredFileSelected));

        if (value?.Tag is RARDetailedBlock detailedBlock)
        {
            ShowDetailedBlockProperties(detailedBlock);
            SetHexBlock(detailedBlock.StartOffset, detailedBlock.TotalSize);
            HasProperties = true;
        }
        else if (value?.Tag is SRRHeaderBlock header)
        {
            ShowSRRHeaderProperties(header);
            SetHexBlock(header.BlockPosition, header.HeaderSize);
            HasProperties = true;
        }
        else if (value?.Tag is SRROsoHashBlock oso)
        {
            ShowOSOHashProperties(oso);
            SetHexBlock(oso.BlockPosition, oso.HeaderSize);
            HasProperties = true;
        }
        else if (value?.Tag is SRRRarPaddingBlock padding)
        {
            ShowRarPaddingProperties(padding);
            SetHexBlock(padding.BlockPosition, padding.HeaderSize + padding.AddSize);
            HasProperties = true;
        }
        else if (value?.Tag is SRRStoredFileBlock stored)
        {
            ShowStoredFileProperties(stored);
            SetHexBlock(stored.BlockPosition, stored.HeaderSize + stored.AddSize);
            HasProperties = true;
        }
        else if (value?.Tag is SRRRarFileBlock rar)
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
            ShowSRSSummaryProperties(srsFile);
            ShowFullHex();
            HasProperties = true;
        }
        else if (value?.Tag is SRSFileDataBlock srsFileData)
        {
            ShowSRSFileDataProperties(srsFileData);
            SetHexBlock(srsFileData.BlockPosition, srsFileData.BlockSize);
            HasProperties = true;
        }
        else if (value?.Tag is SRSTrackDataBlock srsTrack)
        {
            ShowSRSTrackDataProperties(srsTrack);
            SetHexBlock(srsTrack.BlockPosition, srsTrack.BlockSize);
            HasProperties = true;
        }
        else if (value?.Tag is SRSContainerChunk srsChunk)
        {
            ShowSRSChunkProperties(srsChunk);
            SetHexBlock(srsChunk.BlockPosition, srsChunk.BlockSize);
            HasProperties = true;
        }
        else if (value?.Tag is EBMLElement ebmlElement)
        {
            ShowEBMLElementProperties(ebmlElement);
            SetHexBlock(ebmlElement.Position, ebmlElement.TotalSize);
            HasProperties = true;
        }
        else
        {
            ShowFullHex();
        }

        ExportSelectedPropertiesCommand.NotifyCanExecuteChanged();
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
        {
            root.ApplyFilter(value);
        }
    }

    private bool CanExportBlock() => HasFile && HexBlockLength > 0;

    [RelayCommand(CanExecute = nameof(CanExportBlock))]
    private async Task ExportBlockAsync()
    {
        if (!HasFile || HexBlockLength <= 0 || string.IsNullOrEmpty(_loadedFilePathInternal))
        {
            return;
        }

        // Pick a sensible default filename from the selected node
        string defaultName = SelectedTreeNode?.Tag switch
        {
            SRRStoredFileBlock stored => Path.GetFileName(stored.FileName),
            RARDetailedBlock { ItemName: { } name } => name,
            EBMLElement el => $"{SafeFileName(el.Name)}.bin",
            _ => "block.bin"
        };

        string? outputPath = await _fileDialog.SaveFileAsync(
            "Export Block Data",
            Path.GetExtension(defaultName),
            FileDialogFilters.AllFiles,
            defaultName);

        if (outputPath is null)
        {
            return;
        }

        long offset = HexBlockOffset;
        long length = HexBlockLength;

        IsExporting = true;
        StatusMessage = $"Exporting {length:N0} bytes...";
        try
        {
            await Task.Run(() =>
            {
                using var input = new FileStream(_loadedFilePathInternal, FileMode.Open, FileAccess.Read, FileShare.Read);
                using FileStream output = File.Create(outputPath);
                input.Seek(offset, SeekOrigin.Begin);
                byte[] buffer = new byte[ExportBufferSize];
                long remaining = length;
                while (remaining > 0)
                {
                    int toRead = (int)Math.Min(buffer.Length, remaining);
                    int read = input.Read(buffer, 0, toRead);
                    if (read == 0)
                    {
                        break;
                    }
                    output.Write(buffer, 0, read);
                    remaining -= read;
                }
            });

            StatusMessage = $"Exported: {Path.GetFileName(outputPath)} ({length:N0} bytes)";
            MessageBox.Show(
                $"Exported {Path.GetFileName(outputPath)}\n{length:N0} bytes written.",
                "Export Complete",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
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

    private bool CanExportSelectedProperties() => SelectedTreeNode is not null && Properties.Count > 0;

    [RelayCommand(CanExecute = nameof(CanExportSelectedProperties))]
    private async Task ExportSelectedPropertiesAsync()
    {
        if (SelectedTreeNode is null)
        {
            return;
        }

        string defaultName = $"{SafeFileName(SelectedTreeNode.Text)}.json";
        string? path = await _fileDialog.SaveFileAsync(
            "Export properties", ".json", ["JSON Files|*.json"], defaultName);

        if (path is null)
        {
            return;
        }

        try
        {
            await _propertyExportService.ExportSelectedAsync(path, SelectedTreeNode, Properties);
            StatusMessage = $"Exported properties to {Path.GetFileName(path)}";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Error exporting properties: {ex.Message}";
        }
    }

    private bool CanExportTree() => TreeRoots.Count > 0;

    [RelayCommand(CanExecute = nameof(CanExportTree))]
    private async Task ExportTreeAsync()
    {
        if (TreeRoots.Count == 0)
        {
            return;
        }

        string defaultName = $"{Path.GetFileNameWithoutExtension(LoadedFilePath ?? "tree")}.tree.json";
        string? path = await _fileDialog.SaveFileAsync(
            "Export tree", ".json", ["JSON Files|*.json"], defaultName);

        if (path is null)
        {
            return;
        }

        try
        {
            await _propertyExportService.ExportTreeAsync(path, TreeRoots);
            StatusMessage = $"Exported tree to {Path.GetFileName(path)}";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Error exporting tree: {ex.Message}";
        }
    }

    private bool IsSRRFileLoaded()
    {
        if (string.IsNullOrEmpty(_loadedFilePathInternal))
        {
            return false;
        }

        return Path.GetExtension(_loadedFilePathInternal)
            .Equals(".srr", StringComparison.OrdinalIgnoreCase);
    }

    private bool CanAddStoredFile() => IsSRRFileLoaded();

    [RelayCommand(CanExecute = nameof(CanAddStoredFile))]
    private async Task AddStoredFileToSRRAsync()
    {
        if (!IsSRRFileLoaded())
        {
            return;
        }

        string? filePath = await _fileDialog.OpenFileAsync("Select File to Add",
            FileDialogFilters.AllFiles);

        if (filePath is null)
        {
            return;
        }

        try
        {
            ReleaseFileHandles();
            string storedName = Path.GetFileName(filePath);
            _sRREditingService.AddStoredFiles(_loadedFilePathInternal!,
                [(storedName, filePath)]);

            StatusMessage = $"Added stored file: {storedName}";
            LoadFile(_loadedFilePathInternal!);
        }
        catch (Exception ex)
        {
            StatusMessage = $"Error adding stored file: {ex.Message}";
        }
    }

    private void ReleaseFileHandles()
    {
        HexDataSource = null;
        _fileDataSource?.Dispose();
        _fileDataSource = null;
    }

    private async Task MoveStoredFileByOffsetAsync(int offset)
    {
        if (SelectedTreeNode?.Tag is not SRRStoredFileBlock stored)
        {
            return;
        }

        if (string.IsNullOrEmpty(LoadedFilePath))
        {
            return;
        }

        string srrPath = LoadedFilePath;
        ReleaseFileHandles();

        try
        {
            await _sRREditingService.MoveStoredFileAsync(srrPath, stored.FileName, offset);
        }
        catch (Exception ex)
        {
            StatusMessage = $"Error moving stored file: {ex.Message}";
        }
        finally
        {
            LoadFile(srrPath);
        }
    }

    private bool CanRemoveStoredFile()
        => IsSRRFileLoaded() && SelectedTreeNode?.Tag is SRRStoredFileBlock;

    private bool CanRenameStoredFile() => IsStoredFileSelected;
    private bool CanMoveStoredFileUp() => IsStoredFileSelected;
    private bool CanMoveStoredFileDown() => IsStoredFileSelected;

    [RelayCommand(CanExecute = nameof(CanRenameStoredFile))]
    private async Task RenameStoredFileAsync()
    {
        if (SelectedTreeNode?.Tag is not SRRStoredFileBlock stored)
        {
            return;
        }

        if (string.IsNullOrEmpty(LoadedFilePath))
        {
            return;
        }

        string? newName = await _fileDialog.PromptForTextAsync(
            "Rename stored file", "New name:", stored.FileName);

        if (string.IsNullOrWhiteSpace(newName) || newName == stored.FileName)
        {
            return;
        }

        string srrPath = LoadedFilePath;
        ReleaseFileHandles();

        try
        {
            await _sRREditingService.RenameStoredFileAsync(srrPath, stored.FileName, newName);
            StatusMessage = $"Renamed stored file: {stored.FileName} → {newName}";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Error renaming stored file: {ex.Message}";
        }
        finally
        {
            LoadFile(srrPath);
        }
    }

    [RelayCommand(CanExecute = nameof(CanMoveStoredFileUp))]
    private async Task MoveStoredFileUpAsync() => await MoveStoredFileByOffsetAsync(-1);

    [RelayCommand(CanExecute = nameof(CanMoveStoredFileDown))]
    private async Task MoveStoredFileDownAsync() => await MoveStoredFileByOffsetAsync(+1);

    private bool CanVerifyIntegrity() => IsSRRFileLoaded();

    [RelayCommand(CanExecute = nameof(CanRemoveStoredFile))]
    private void RemoveStoredFileFromSRR()
    {
        if (!IsSRRFileLoaded() || SelectedTreeNode?.Tag is not SRRStoredFileBlock stored)
        {
            return;
        }

        try
        {
            ReleaseFileHandles();
            _sRREditingService.RemoveStoredFiles(_loadedFilePathInternal!,
                [stored.FileName]);

            StatusMessage = $"Removed stored file: {stored.FileName}";
            LoadFile(_loadedFilePathInternal!);
        }
        catch (Exception ex)
        {
            StatusMessage = $"Error removing stored file: {ex.Message}";
        }
    }

    [RelayCommand(CanExecute = nameof(CanVerifyIntegrity))]
    private async Task VerifyIntegrityAsync()
    {
        if (string.IsNullOrEmpty(LoadedFilePath))
        {
            return;
        }

        SRRVerifyResult result = await _verifyService.VerifyAsync(LoadedFilePath);
        VerifyResultText = FormatVerifyResult(result);
        IsVerifyResultVisible = true;
    }

    [RelayCommand]
    private void DismissVerifyResult() => IsVerifyResultVisible = false;

    [RelayCommand]
    private void ShowHexSearch() => IsHexSearchVisible = true;

    [RelayCommand]
    private void HideHexSearch()
    {
        IsHexSearchVisible = false;
        HexSearchStatus = string.Empty;
    }

    [RelayCommand(CanExecute = nameof(CanRunHexSearch))]
    private void FindNext() => RunHexSearch(forward: true);

    [RelayCommand(CanExecute = nameof(CanRunHexSearch))]
    private void FindPrevious() => RunHexSearch(forward: false);

    private void BuildTree()
    {
        TreeRoots.Clear();

        if (_sRSData is not null)
        {
            BuildSRSTree();
            return;
        }

        if (_rarDetailedBlocks is not null)
        {
            BuildRarTree();
            return;
        }

        if (_mkvData is not null)
        {
            BuildMKVTree();
            return;
        }

        if (_sRRData is null)
        {
            return;
        }

        BuildSRRTree();
    }

    private void BuildMKVTree()
    {
        MKVFileData mkv = _mkvData!;
        var root = new TreeNodeViewModel
        {
            Text = $"MKV File ({mkv.TrackCount} track{(mkv.TrackCount == 1 ? "" : "s")})",
            Tag = "root",
            IsExpanded = true
        };

        AddMKVElements(root, mkv.Elements, depth: 0);
        TreeRoots.Add(root);
    }

    private static void AddMKVElements(TreeNodeViewModel parentNode, IReadOnlyList<EBMLElement> elements, int depth)
    {
        foreach (EBMLElement element in elements)
        {
            string text = element.Value is { Length: > 0 } && element.Children.Count == 0
                ? $"{element.Name}: {element.Value}"
                : element.Name;

            var node = new TreeNodeViewModel
            {
                Text = text,
                Tag = element,
                IsExpanded = depth < 2
            };

            if (element.Children.Count > 0)
            {
                AddMKVElements(node, element.Children, depth + 1);
            }

            parentNode.Children.Add(node);
        }
    }

    private static int CountElements(IReadOnlyList<EBMLElement> elements)
    {
        int count = 0;
        foreach (EBMLElement element in elements)
        {
            count += 1 + CountElements(element.Children);
        }

        return count;
    }

    private static bool DetectCustomPackerInRarBlocks(IReadOnlyList<RARDetailedBlock> blocks)
    {
        foreach (RARDetailedBlock block in blocks)
        {
            if (block.BlockType != "File Header")
            {
                continue;
            }

            // Check for sentinel descriptions added by the detailed parser
            foreach (RARHeaderField field in block.Fields)
            {
                if (field.Description is not null && field.Description.Contains("Custom packer sentinel", StringComparison.Ordinal))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private void BuildRarTree()
    {
        IReadOnlyList<RARDetailedBlock> blocks = _rarDetailedBlocks!;
        bool isRAR5 = blocks.Count > 0 && blocks[0].BlockType == "Signature" &&
                      blocks[0].Fields.Count > 0 && blocks[0].Fields[0].Value.StartsWith("52 61 72 21 1A 07 01", StringComparison.Ordinal);

        string rootName = isRAR5 ? $"RAR 5.x Archive ({blocks.Count} blocks)" : $"RAR 4.x Archive ({blocks.Count} blocks)";

        var root = new TreeNodeViewModel { Text = rootName, Tag = "root", IsExpanded = true };

        for (int i = 0; i < blocks.Count; i++)
        {
            RARDetailedBlock block = blocks[i];
            string blockType = block.HasData && block.BlockType.Contains("File", StringComparison.Ordinal) ? "File Data" : block.BlockType;
            string blockLabel = $"[{i}] {blockType}";

            if (!string.IsNullOrEmpty(block.ItemName))
            {
                blockLabel = $"[{i}] {blockType}: {block.ItemName}";
            }

            root.Children.Add(new TreeNodeViewModel { Text = blockLabel, Tag = block });
        }

        TreeRoots.Add(root);
    }

    private void BuildSRRTree()
    {
        SRRFile srr = _sRRData!.SRRFile;

        var root = new TreeNodeViewModel { Text = "SRR File", Tag = "root", IsExpanded = true };

        if (srr.HeaderBlock is not null)
        {
            root.Children.Add(new TreeNodeViewModel { Text = "SRR Header", Tag = srr.HeaderBlock });
        }

        if (srr.RARFiles.Count > 0)
        {
            root.Children.Add(new TreeNodeViewModel { Text = "RAR Archive Info", Tag = srr });
        }

        if (srr.OSOHashBlocks.Count > 0)
        {
            var osoNode = new TreeNodeViewModel
            {
                Text = $"OSO Hashes ({srr.OSOHashBlocks.Count})",
                Tag = "container"
            };
            foreach (SRROsoHashBlock oso in srr.OSOHashBlocks)
            {
                osoNode.Children.Add(new TreeNodeViewModel { Text = oso.FileName, Tag = oso });
            }

            root.Children.Add(osoNode);
        }

        if (srr.RARPaddingBlocks.Count > 0)
        {
            var paddingNode = new TreeNodeViewModel
            {
                Text = $"RAR Padding ({srr.RARPaddingBlocks.Count})",
                Tag = "container"
            };
            foreach (SRRRarPaddingBlock padding in srr.RARPaddingBlocks)
            {
                paddingNode.Children.Add(new TreeNodeViewModel { Text = padding.RARFileName, Tag = padding });
            }

            root.Children.Add(paddingNode);
        }

        if (srr.RARFiles.Count > 0)
        {
            var volumesNode = new TreeNodeViewModel
            {
                Text = $"RAR Volumes ({srr.RARFiles.Count})",
                Tag = "container"
            };
            foreach (SRRRarFileBlock rar in srr.RARFiles)
            {
                var volNode = new TreeNodeViewModel { Text = rar.FileName, Tag = rar };

                if (_sRRData.VolumeDetailedBlocks.TryGetValue(rar.FileName, out List<RARDetailedBlock>? detailedBlocks))
                {
                    for (int i = 0; i < detailedBlocks.Count; i++)
                    {
                        RARDetailedBlock block = detailedBlocks[i];
                        string blockType = block.HasData && block.BlockType.Contains("File", StringComparison.Ordinal) ? "File Data" : block.BlockType;
                        string blockLabel = $"[{i}] {blockType}";
                        if (!string.IsNullOrEmpty(block.ItemName))
                        {
                            blockLabel = $"[{i}] {blockType}: {block.ItemName}";
                        }

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
                Tag = "container"
            };
            foreach (SRRStoredFileBlock stored in srr.StoredFiles)
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
            foreach (string? file in srr.ArchivedFiles.OrderBy(f => f))
            {
                string label = file;
                if (srr.ArchivedFileCrcs.TryGetValue(file, out string? crc))
                {
                    label = $"{file} [CRC: {crc}]";
                }

                archivedNode.Children.Add(new TreeNodeViewModel { Text = label, Tag = "archived" });
            }

            root.Children.Add(archivedNode);
        }

        TreeRoots.Add(root);
    }

    private void BuildSRSTree()
    {
        SRSFile srs = _sRSData!.SRSFile;
        var root = new TreeNodeViewModel
        {
            Text = $"SRS File ({srs.ContainerType})",
            Tag = srs,
            IsExpanded = true
        };

        if (srs.FileData is not null)
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
                Tag = "container"
            };
            foreach (SRSTrackDataBlock track in srs.Tracks)
            {
                tracksNode.Children.Add(new TreeNodeViewModel
                {
                    Text = $"Track {track.TrackNumber} ({FormatUtilities.FormatSize((long)track.DataLength)})",
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
            BuildChunkHierarchy(chunksNode, srs.ContainerChunks);
            root.Children.Add(chunksNode);
        }

        TreeRoots.Add(root);
    }

    private void ShowSRSSummaryProperties(SRSFile srs)
    {
        AddProperty("Container Type", srs.ContainerType.ToString());

        if (srs.FileData is not null)
        {
            AddProperty("Sample File", srs.FileData.FileName);
            AddProperty("Sample Size", $"{srs.FileData.SampleSize:N0} bytes ({FormatUtilities.FormatSize((long)srs.FileData.SampleSize)})");
            AddProperty("Sample CRC32", $"0x{srs.FileData.CRC32:X8}");
            if (!string.IsNullOrEmpty(srs.FileData.AppName))
            {
                AddProperty("App Name", srs.FileData.AppName);
            }
        }

        AddProperty("Track Count", srs.Tracks.Count.ToString());
        AddProperty("Container Chunks", srs.ContainerChunks.Count.ToString());
    }

    private void ShowSRSFileDataProperties(SRSFileDataBlock block)
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
        {
            AddProperty("App Name", block.AppName,
                new ByteRange { Offset = block.AppNameOffset, Length = block.AppNameSize });
        }

        AddProperty("File Name Size", $"{block.FileNameSize}",
            new ByteRange { Offset = block.FileNameSizeOffset, Length = 2 });
        AddProperty("File Name", block.FileName,
            new ByteRange { Offset = block.FileNameOffset, Length = block.FileNameSize });
        AddProperty("Sample Size", $"{block.SampleSize:N0} bytes ({FormatUtilities.FormatSize((long)block.SampleSize)})",
            new ByteRange { Offset = block.SampleSizeOffset, Length = 8 });
        AddProperty("CRC-32", $"0x{block.CRC32:X8}",
            new ByteRange { Offset = block.CRC32Offset, Length = 4 });
    }

    private void ShowSRSTrackDataProperties(SRSTrackDataBlock block)
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
        AddProperty(dataLabel, $"{block.DataLength:N0} bytes ({FormatUtilities.FormatSize((long)block.DataLength)})",
            new ByteRange { Offset = block.DataLengthOffset, Length = block.DataLengthFieldSize });

        AddProperty("Match Offset", $"0x{block.MatchOffset:X}",
            new ByteRange { Offset = block.MatchOffsetOffset, Length = 8 });
        AddProperty("Signature Size", $"{block.SignatureSize} bytes",
            new ByteRange { Offset = block.SignatureSizeOffset, Length = 2 });

        if (block.SignatureSize > 0)
        {
            string sigHex = BitConverter.ToString(block.Signature.ToArray()).Replace("-", " ", StringComparison.Ordinal);
            const int maxSignatureDisplayLength = 80;
            if (sigHex.Length > maxSignatureDisplayLength)
            {
                sigHex = sigHex[..maxSignatureDisplayLength] + "...";
            }
            AddProperty("Signature", sigHex,
                new ByteRange { Offset = block.SignatureOffset, Length = block.SignatureSize });
        }
    }

    private void ShowEBMLElementProperties(EBMLElement element)
    {
        AddProperty("Element", element.Name);
        AddProperty("Element ID", $"0x{element.ElementId:X}",
            new ByteRange { Offset = element.Position, Length = element.HeaderSize });
        AddProperty("Type", element.ValueType.ToString());

        if (element.Value is { Length: > 0 })
        {
            AddProperty("Value", element.Value,
                new ByteRange { Offset = element.Position + element.HeaderSize, Length = element.DataSize });
        }

        AddProperty("Position", $"0x{element.Position:X}");
        AddProperty("Header Size", $"{element.HeaderSize} bytes");
        AddProperty("Data Size", $"{element.DataSize:N0} bytes ({FormatUtilities.FormatSize(element.DataSize)})",
            new ByteRange { Offset = element.Position + element.HeaderSize, Length = element.DataSize });
        AddProperty("Total Size", $"{element.TotalSize:N0} bytes");

        if (element.Children.Count > 0)
        {
            AddProperty("Children", element.Children.Count.ToString());
        }
    }

    private void ShowSRSChunkProperties(SRSContainerChunk chunk)
    {
        AddProperty("Label", chunk.Label);
        AddProperty("Chunk ID", chunk.ChunkId);
        AddProperty("Position", $"0x{chunk.BlockPosition:X8}",
            new ByteRange { Offset = chunk.BlockPosition, Length = chunk.HeaderSize });
        AddProperty("Total Size", $"{chunk.BlockSize:N0} bytes ({FormatUtilities.FormatSize(chunk.BlockSize)})");
        AddProperty("Header Size", $"{chunk.HeaderSize:N0} bytes ({FormatUtilities.FormatSize(chunk.HeaderSize)})");
        AddProperty("Payload Size", $"{chunk.PayloadSize:N0} bytes ({FormatUtilities.FormatSize(chunk.PayloadSize)})");
    }

    private bool CanRunHexSearch()
        => HexDataSource is not null && !string.IsNullOrWhiteSpace(HexSearchText);

    private void RunHexSearch(bool forward)
    {
        if (HexDataSource is null)
        {
            HexSearchStatus = "No file loaded.";
            return;
        }

        var pattern = HexSearchPattern.TryParse(HexSearchText, HexSearchAsHex);

        if (pattern is null)
        {
            HexSearchStatus = HexSearchAsHex ? "Invalid hex (need pairs)." : "Empty pattern.";
            return;
        }

        long start = forward
            ? (HexSelectionOffset >= 0 ? HexSelectionOffset + 1 : 0)
            : (HexSelectionOffset >= 0 ? HexSelectionOffset : HexDataSource.Length);

        long match = forward
            ? HexSearcher.FindForward(HexDataSource, pattern, start)
            : HexSearcher.FindBackward(HexDataSource, pattern, start);

        if (match < 0)
        {
            HexSearchStatus = "Not found.";
            return;
        }

        ApplyHexMatch(match, pattern.Bytes.Length);
        UpdateHexMatchRanges(pattern);
    }

    private void RunLiveHexSearch()
    {
        if (!IsHexSearchVisible || HexDataSource is null)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(HexSearchText))
        {
            HexSearchStatus = string.Empty;
            HexMatchRanges = null;
            return;
        }

        var pattern = HexSearchPattern.TryParse(HexSearchText, HexSearchAsHex);

        if (pattern is null)
        {
            // Stay quiet during typing — error message only on explicit Next/Prev.
            HexSearchStatus = string.Empty;
            HexMatchRanges = null;
            return;
        }

        long start = HexSelectionOffset >= 0 ? HexSelectionOffset : 0;
        long match = HexSearcher.FindForward(HexDataSource, pattern, start);

        if (match < 0 && start > 0)
        {
            match = HexSearcher.FindForward(HexDataSource, pattern, 0);
        }

        if (match < 0)
        {
            HexSearchStatus = "Not found.";
            UpdateHexMatchRanges(pattern);
            return;
        }

        ApplyHexMatch(match, pattern.Bytes.Length);
        UpdateHexMatchRanges(pattern);
    }

    private void ApplyHexMatch(long match, int length)
    {
        if (HexDataSource is null)
        {
            return;
        }

        long matchEnd = match + length;
        if (match < HexBlockOffset || matchEnd > HexBlockOffset + HexBlockLength)
        {
            HexBlockOffset = 0;
            HexBlockLength = HexDataSource.Length;
        }

        HexSelectionOffset = match;
        HexSelectionLength = length;
        HexSearchStatus = $"Match at 0x{match:X}.";
    }

    private void UpdateHexMatchRanges(HexSearchPattern? pattern)
    {
        if (!HighlightAllMatches || pattern is null || HexDataSource is null)
        {
            HexMatchRanges = null;
            return;
        }

        IReadOnlyList<long> offsets = HexSearcher.FindAll(HexDataSource, pattern);
        if (offsets.Count == 0)
        {
            HexMatchRanges = null;
            return;
        }

        var ranges = new List<HexMatchRange>(offsets.Count);
        foreach (long offset in offsets)
        {
            ranges.Add(new HexMatchRange(offset, pattern.Bytes.Length));
        }

        HexMatchRanges = ranges;
    }

    partial void OnHexSearchTextChanged(string value)
    {
        RunLiveHexSearch();
    }

    partial void OnHexSearchAsHexChanged(bool value)
    {
        RunLiveHexSearch();
    }

    partial void OnHighlightAllMatchesChanged(bool value)
    {
        if (!value)
        {
            HexMatchRanges = null;
            return;
        }

        UpdateHexMatchRanges(HexSearchPattern.TryParse(HexSearchText, HexSearchAsHex));
    }

    partial void OnIsHexSearchVisibleChanged(bool value)
    {
        if (!value)
        {
            HexMatchRanges = null;
        }
    }

    private void SetHexBlock(long offset, long size)
    {
        // Clamp to actual file data so we don't show empty rows
        // (e.g. RAR headers in SRR reference data that isn't stored)
        long end = Math.Min(offset + size, _fileSize);
        long clampedSize = Math.Max(0, end - offset);

        HexBlockOffset = offset;
        HexBlockLength = clampedSize;
        HexDataSource = _fileDataSource is not null
            ? new HexDataSourceSlice(_fileDataSource, offset, clampedSize)
            : null;
    }

    private const long MaxHexSliceSize = 100L * 1024 * 1024; // 100 MB

    private void ShowFullHex()
    {
        long len = Math.Min(_fileSize, MaxHexSliceSize);
        HexBlockOffset = 0;
        HexBlockLength = len;
        HexDataSource = _fileDataSource is not null
            ? new HexDataSourceSlice(_fileDataSource, 0, len)
            : null;
    }

    private static string FormatVerifyResult(SRRVerifyResult result)
    {
        var sb = new StringBuilder();
        sb.AppendLine(result.IsValid ? "OK — no errors found." : "Errors detected.");
        sb.AppendLine($"Blocks scanned: {result.BlocksScanned:N0}");
        sb.AppendLine($"File size: {result.FileSize:N0} bytes");

        foreach (SRRVerifyIssue issue in result.Issues)
        {
            sb.AppendLine($"[{issue.Severity}] 0x{issue.Offset:X}: {issue.Message}");
        }

        return sb.ToString();
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

    private void ShowSRRHeaderProperties(SRRHeaderBlock header)
    {
        long pos = header.BlockPosition;

        AddProperty("Header CRC", $"0x{header.CRC:X4}",
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

    private void ShowStoredFileProperties(SRRStoredFileBlock stored)
    {
        long p = stored.BlockPosition;

        AddProperty("Header CRC", $"0x{stored.CRC:X4}",
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
            AddProperty("File Data", $"{stored.FileLength:N0} bytes ({FormatUtilities.FormatSize(stored.FileLength)})",
                new ByteRange { Offset = stored.DataOffset, Length = (int)Math.Min(stored.FileLength, int.MaxValue) });
        }
    }

    private void ShowRarFileProperties(SRRRarFileBlock rar)
    {
        long p = rar.BlockPosition;

        AddProperty("Header CRC", $"0x{rar.CRC:X4}",
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

    private void ShowOSOHashProperties(SRROsoHashBlock oso)
    {
        long p = oso.BlockPosition;

        AddProperty("Header CRC", $"0x{oso.CRC:X4}",
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

        // Binary order: FileSize(8), OSOHash(8), NameLen(2), FileName(var)
        AddProperty("File Size", $"{oso.FileSize:N0} bytes ({FormatUtilities.FormatSize((long)oso.FileSize)})",
            new ByteRange { Offset = p, Length = 8 });
        p += 8;
        AddProperty("OSO Hash", Convert.ToHexString(oso.OSOHash.Span),
            new ByteRange { Offset = p, Length = 8 });
        p += 8;

        int nameLen = Encoding.UTF8.GetByteCount(oso.FileName);
        AddProperty("Name Length", $"{nameLen} bytes",
            new ByteRange { Offset = p, Length = 2 });
        p += 2;
        AddProperty("File Name", oso.FileName,
            new ByteRange { Offset = p, Length = nameLen });
    }

    private void ShowRarPaddingProperties(SRRRarPaddingBlock padding)
    {
        long p = padding.BlockPosition;

        AddProperty("Header CRC", $"0x{padding.CRC:X4}",
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

        int nameLen = Encoding.UTF8.GetByteCount(padding.RARFileName);
        AddProperty("Name Length", $"{nameLen} bytes",
            new ByteRange { Offset = p, Length = 2 });
        p += 2;
        AddProperty("RAR File Name", padding.RARFileName,
            new ByteRange { Offset = p, Length = nameLen });
        AddProperty("Padding Size", $"{padding.PaddingSize:N0} bytes");
    }

    private void ShowDetailedBlockProperties(RARDetailedBlock block)
    {
        foreach (RARHeaderField field in block.Fields)
        {
            string value = field.Value;
            bool isWarning = field.Description is not null && field.Description.Contains("Custom packer sentinel", StringComparison.Ordinal);
            if (!string.IsNullOrEmpty(field.Description) && field.Description != field.Value)
            {
                value = $"{field.Value} ({field.Description})";
            }

            ByteRange? range = field.Length > 0
                ? new ByteRange { PropertyName = field.Name, Offset = field.Offset, Length = field.Length }
                : null;

            AddProperty(field.Name, value, range, warning: isWarning);

            foreach (RARHeaderField child in field.Children)
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
            foreach (PropertyItem p in Properties)
            {
                if (p.Name == "Data")
                {
                    hasData = true;
                    break;
                }
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
        {
            AddProperty("Compression Method", GetCompressionMethodName((byte)srr.CompressionMethod.Value));
        }

        if (srr.DictionarySize.HasValue)
        {
            AddProperty("Dictionary Size", $"{srr.DictionarySize.Value} KB");
        }

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
        {
            AddProperty("Volume Size", $"{srr.VolumeSizeBytes.Value:N0} bytes ({FormatUtilities.FormatSize(srr.VolumeSizeBytes.Value)})");
        }

        if (srr.RARVolumeSizes.Count > 0)
        {
            AddProperty("Volume Sizes Count", srr.RARVolumeSizes.Count.ToString());
            var uniqueSizes = srr.RARVolumeSizes.Distinct().OrderByDescending(s => s).ToList();
            for (int i = 0; i < Math.Min(uniqueSizes.Count, 5); i++)
            {
                AddProperty($"  Unique Size {i + 1}",
                    $"{uniqueSizes[i]:N0} bytes ({FormatUtilities.FormatSize(uniqueSizes[i])})", indented: true);
            }

            if (uniqueSizes.Count > 5)
            {
                AddProperty("  ...", $"({uniqueSizes.Count - 5} more)", indented: true);
            }
        }

        AddProperty("RAR Volumes", srr.RARFiles.Count.ToString());
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
        AddProperty("Header CRC Errors", srr.HeaderCRCMismatches.ToString());
        AddProperty("Has Comment", !string.IsNullOrEmpty(srr.ArchiveComment) ? "Yes" : "No");

        if (!string.IsNullOrEmpty(srr.ArchiveComment))
        {
            AddProperty("Comment", srr.ArchiveComment);
        }
    }

    private static string FormatBool(bool? value) =>
        value.HasValue ? (value.Value ? "Yes" : "No") : "Unknown";

    private static void BuildChunkHierarchy(TreeNodeViewModel root, IReadOnlyList<SRSContainerChunk> chunks)
    {
        var nodeStack = new Stack<TreeNodeViewModel>();
        var endStack = new Stack<long>();
        nodeStack.Push(root);
        endStack.Push(long.MaxValue);

        foreach (SRSContainerChunk chunk in chunks)
        {
            long chunkEnd = chunk.BlockPosition + chunk.BlockSize;

            while (endStack.Count > 1 && chunk.BlockPosition >= endStack.Peek())
            {
                nodeStack.Pop();
                endStack.Pop();
            }

            var node = new TreeNodeViewModel
            {
                Text = $"{chunk.Label} (0x{chunk.BlockPosition:X}, {FormatUtilities.FormatSize(chunk.BlockSize)})",
                Tag = chunk
            };
            nodeStack.Peek().Children.Add(node);

            nodeStack.Push(node);
            endStack.Push(chunkEnd);
        }
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

    private static string SafeFileName(string text)
    {
        char[] invalid = Path.GetInvalidFileNameChars();
        var sb = new StringBuilder(text.Length);
        foreach (char c in text)
        {
            sb.Append(invalid.Contains(c) ? '_' : c);
        }

        string trimmed = sb.ToString().Trim();
        if (trimmed.Length > 200)
        {
            trimmed = trimmed[..200];
        }

        return string.IsNullOrEmpty(trimmed) ? "node" : trimmed;
    }

    private bool _disposed;

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    protected virtual void Dispose(bool disposing)
    {
        if (_disposed)
        {
            return;
        }

        if (disposing)
        {
            // dispose managed resources
            _fileDataSource?.Dispose();
            _fileDataSource = null;
        }

        // note: no unmanaged resources to release here

        _disposed = true;
    }
}
