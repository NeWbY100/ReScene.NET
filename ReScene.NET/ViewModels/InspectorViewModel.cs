using System.Collections.ObjectModel;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EBMLElement = ReScene.Core.Comparison.EBMLElement;
using MKVFileData = ReScene.Core.Comparison.MKVFileData;
using ReScene.Hex;
using ReScene.NET.Helpers;
using ReScene.NET.Models;
using ReScene.NET.Services;
using ReScene.NET.ViewModels.Inspection;
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
                bool isRAR5 = RarBlockLabel.IsRar5Signature(_rarDetailedBlocks);
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

        foreach (PropertyItem item in new InspectorPropertyBuilder().Build(value?.Tag))
        {
            Properties.Add(item);
        }

        if (value?.Tag is RARDetailedBlock detailedBlock)
        {
            SetHexBlock(detailedBlock.StartOffset, detailedBlock.TotalSize);
            HasProperties = true;
        }
        else if (value?.Tag is SRRHeaderBlock header)
        {
            SetHexBlock(header.BlockPosition, header.HeaderSize);
            HasProperties = true;
        }
        else if (value?.Tag is SRROsoHashBlock oso)
        {
            SetHexBlock(oso.BlockPosition, oso.HeaderSize);
            HasProperties = true;
        }
        else if (value?.Tag is SRRRarPaddingBlock padding)
        {
            SetHexBlock(padding.BlockPosition, padding.HeaderSize + padding.AddSize);
            HasProperties = true;
        }
        else if (value?.Tag is SRRStoredFileBlock stored)
        {
            SetHexBlock(stored.BlockPosition, stored.HeaderSize + stored.AddSize);
            HasProperties = true;
        }
        else if (value?.Tag is SRRRarFileBlock rar)
        {
            SetHexBlock(rar.BlockPosition, rar.HeaderSize + rar.AddSize);
            HasProperties = true;
        }
        else if (value?.Tag is SRRFile)
        {
            ShowFullHex();
            HasProperties = true;
        }
        else if (value?.Tag is SRSFile)
        {
            ShowFullHex();
            HasProperties = true;
        }
        else if (value?.Tag is SRSFileDataBlock srsFileData)
        {
            SetHexBlock(srsFileData.BlockPosition, srsFileData.BlockSize);
            HasProperties = true;
        }
        else if (value?.Tag is SRSTrackDataBlock srsTrack)
        {
            SetHexBlock(srsTrack.BlockPosition, srsTrack.BlockSize);
            HasProperties = true;
        }
        else if (value?.Tag is SRSContainerChunk srsChunk)
        {
            SetHexBlock(srsChunk.BlockPosition, srsChunk.BlockSize);
            HasProperties = true;
        }
        else if (value?.Tag is EBMLElement ebmlElement)
        {
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
            _fileDialog.ShowInfo(
                "Export Complete",
                $"Exported {Path.GetFileName(outputPath)}\n{length:N0} bytes written.");
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
            TreeRoots.Add(InspectorTreeBuilder.BuildSrs(_sRSData.SRSFile));
            return;
        }

        if (_rarDetailedBlocks is not null)
        {
            TreeRoots.Add(InspectorTreeBuilder.BuildRar(_rarDetailedBlocks));
            return;
        }

        if (_mkvData is not null)
        {
            TreeRoots.Add(InspectorTreeBuilder.BuildMkv(_mkvData));
            return;
        }

        if (_sRRData is null)
        {
            return;
        }

        TreeRoots.Add(InspectorTreeBuilder.BuildSrr(_sRRData));
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
