using System.Collections.ObjectModel;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ReScene.App.Core.Helpers;
using ReScene.App.Core.Models;
using ReScene.App.Core.Services;
using ReScene.App.Core.ViewModels.Inspection;
using ReScene.Hex;
using ReScene.RAR;
using ReScene.SRR;
using ReScene.SRS;
using EBMLElement = ReScene.Core.Comparison.EBMLElement;
using MKVFileData = ReScene.Core.Comparison.MKVFileData;
namespace ReScene.App.Core.ViewModels;

public partial class InspectorViewModel(IFileDialogService fileDialog, ISRREditingService srrEditingService, ISRRVerifyService verifyService, IPropertyExportService propertyExportService, IImagePreviewService imagePreviewService, IAppSettingsService? settingsService = null) : ViewModelBase, IDisposable
{
    private const int ExportBufferSize = 80 * 1024;

    private readonly IFileDialogService _fileDialog = fileDialog;
    private readonly ISRREditingService _sRREditingService = srrEditingService;
    private readonly ISRRVerifyService _verifyService = verifyService;
    private readonly IPropertyExportService _propertyExportService = propertyExportService;
    private readonly IImagePreviewService _imagePreviewService = imagePreviewService;
    private readonly IAppSettingsService? _settingsService = settingsService;
    private SRRFileData? _sRRData;
    private SRSInspectorData? _sRSData;
    private IReadOnlyList<RARDetailedBlock>? _rarDetailedBlocks;
    private MKVFileData? _mkvData;
    private string? _loadedFilePathInternal;
    private long _fileSize;
    private MemoryMappedDataSource? _fileDataSource;

    // Monotonic counter identifying the current/latest file load. Bumped by each LoadFileAsync
    // and by CloseFile; an off-thread parse applies its result only if its captured value still
    // matches, so overlapping loads and close-during-load can't leave torn state or a leaked source.
    private int _loadGeneration;

    // The path of the most recently REQUESTED load, set before its parse begins — unlike
    // _loadedFilePathInternal, which only becomes that path once the parse has been applied.
    // ReloadAfterEditAsync needs the requested one: while a newly opened file is still parsing the
    // applied path is still the OLD file, so testing against it would call an edit on the old file
    // "current" and reload it, superseding the navigation the user just made.
    private string? _requestedFilePath;

    [ObservableProperty]
    public partial string LoadedFilePath { get; set; } = string.Empty;

    [RelayCommand]
    private async Task BrowseFileAsync()
    {
        string? path = await _fileDialog.OpenFileAsync("Open File to Inspect",
            FileDialogFilters.InspectFiles, LoadedFilePath);

        if (path is not null)
        {
            await LoadFileAsync(path);
        }
    }

    /// <summary>
    /// Loads the file whose path was typed or pasted into the file box (bound to Enter there).
    /// The box is editable so a path can be entered without the OS file dialog —
    /// keyboard-friendly, and the only file-dialog-free load path automation can drive.
    /// </summary>
    [RelayCommand]
    private async Task LoadFromPathAsync()
    {
        // Explorer's "Copy as path" wraps the path in quotes; trim them so a paste loads as-is.
        string path = LoadedFilePath.Trim().Trim('"');
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        if (!File.Exists(path))
        {
            StatusMessage = $"File not found: {path}";
            return;
        }

        await LoadFileAsync(path);
    }

    private bool CanCloseFile() => HasFile;

    [RelayCommand(CanExecute = nameof(CanCloseFile))]
    private void CloseFile()
    {
        // Supersede any in-flight LoadFileAsync so its continuation won't resurrect the file
        // after we've closed it.
        _loadGeneration++;
        _requestedFilePath = null;

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
        UpdateTextView();
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
        OnPropertyChanged(nameof(IsImagePreviewAvailable));
        PreviewStoredImageCommand.NotifyCanExecuteChanged();
        VerifyIntegrityCommand.NotifyCanExecuteChanged();
        RenameStoredFileCommand.NotifyCanExecuteChanged();
        MoveStoredFileUpCommand.NotifyCanExecuteChanged();
        MoveStoredFileDownCommand.NotifyCanExecuteChanged();
        ExportSelectedPropertiesCommand.NotifyCanExecuteChanged();
        ExportTreeCommand.NotifyCanExecuteChanged();
        // Clear the 'Export…' item's enabled state on close (it gates on HasFile too); the
        // HexBlockLength reset above only re-notifies when the length was non-zero.
        ExportBlockCommand.NotifyCanExecuteChanged();
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

    /// <summary>
    /// Gets whether the selected stored file is a previewable image.
    /// </summary>
    public bool IsImagePreviewAvailable =>
        IsSRRFileLoaded()
        && SelectedTreeNode?.Tag is SRRStoredFileBlock block
        && ImagePreviewSupport.IsSupported(block.FileName);

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

    // ExportBlockCommand's CanExecute gates on HexBlockLength > 0. OnSelectedTreeNodeChanged notifies
    // the command BEFORE SetHexBlock sets the new length, so without this the 'Export…' item was
    // disabled for one selection after opening a file. Re-notify whenever the length changes.
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ExportBlockCommand))]
    public partial long HexBlockLength { get; set; }

    [ObservableProperty]
    public partial long HexSelectionOffset { get; set; } = -1;

    [ObservableProperty]
    public partial long HexSelectionLength { get; set; }

    [ObservableProperty]
    public partial int HexBytesPerLine { get; set; } = 16;

    [ObservableProperty]
    public partial bool ShowHexView { get; set; } = true;

    private const int TextViewMaxBytes = 1024 * 1024; // 1 MB

    /// <summary>The encodings offered by the Text view (UTF-8 first / default).</summary>
    public IReadOnlyList<TextEncodingOption> TextEncodings { get; } = TextEncodingOptions.All;

    [ObservableProperty]
    public partial TextEncodingOption SelectedEncoding { get; set; } = TextEncodingOptions.All[0];

    /// <summary>True when the bottom panel shows the Text view; false shows the Hex view.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsHexViewActive))]
    public partial bool IsTextViewActive { get; set; }

    /// <summary>Convenience inverse of <see cref="IsTextViewActive"/> for the Hex toggle and hex-only chrome.</summary>
    public bool IsHexViewActive => !IsTextViewActive;

    [ObservableProperty]
    public partial bool TextWordWrap { get; set; }

    [ObservableProperty]
    public partial string TextViewContent { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool TextViewTruncated { get; set; }

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

    /// <summary>
    /// One-line verdict for the verify panel's live region. The panel itself toggles
    /// <see cref="IsVerifyResultVisible"/>, so it cannot announce its own arrival.
    /// </summary>
    [ObservableProperty]
    public partial string VerifyAnnouncement { get; set; } = string.Empty;

    public async Task LoadFileAsync(string filePath)
    {
        // A disposed Inspector must not load: everything below allocates a MemoryMappedDataSource
        // that only Dispose releases, and Dispose has already run.
        if (_disposed)
        {
            return;
        }

        // Bump the generation so any in-flight load (or a CloseFile) is superseded: when this
        // load's off-thread parse returns, it applies its result only if it is still the latest.
        // Without this, two overlapping loads race — the loser's continuation would overwrite the
        // winner's _fileDataSource without disposing it (leaking a memory-mapped view + handle).
        int loadGeneration = ++_loadGeneration;
        _requestedFilePath = filePath;

        try
        {
            string ext = Path.GetExtension(filePath);
            bool isSRS = ext.Equals(".srs", StringComparison.OrdinalIgnoreCase);
            bool isRAR = ext.Equals(".rar", StringComparison.OrdinalIgnoreCase);
            bool isMKV = ext.Equals(".mkv", StringComparison.OrdinalIgnoreCase)
                || ext.Equals(".webm", StringComparison.OrdinalIgnoreCase);

            _sRSData = null;
            _sRRData = null;
            _rarDetailedBlocks = null;
            _mkvData = null;
            WarningMessage = null;

            // Dispose previous memory-mapped source
            _fileDataSource?.Dispose();
            _fileDataSource = null;

            // Drop any view over the previous file so a reload never reads the now-disposed
            // source (the Text view's UpdateTextView would otherwise throw on the stale slice).
            // Both views repopulate when a tree node is selected.
            HexDataSource = null;
            HexBlockOffset = 0;
            HexBlockLength = 0;

            int mkvMaxElements = isMKV
                ? (_settingsService?.Load().MKVMaxElements ?? MKVFileData.DefaultMaxElements)
                : MKVFileData.DefaultMaxElements;

            // Parse off the UI thread so a large SRS/RAR/MKV/SRR file does not freeze the UI.
            ParsedFileData parsed = await Task.Run(
                () => ParseFileData(filePath, isSRS, isRAR, isMKV, mkvMaxElements));

            // A newer load (or CloseFile) started while we were parsing — discard this stale
            // result so we don't clobber the current file's state or leak its data source.
            // _disposed is checked alongside it: Dispose now advances the generation too, so this
            // comparison alone would catch it, but reading the flag directly states the intent and
            // holds even if the generation is ever reset rather than incremented.
            if (loadGeneration != _loadGeneration || _disposed)
            {
                return;
            }

            _sRSData = parsed.SRS;
            _rarDetailedBlocks = parsed.RAR;
            _mkvData = parsed.MKV;
            _sRRData = parsed.SRR;

            LoadedFilePath = filePath;
            _loadedFilePathInternal = filePath;
            _fileSize = parsed.FileSize;
            _fileDataSource = new MemoryMappedDataSource(filePath);

            BuildTree();
            HasFile = true;
            OnPropertyChanged(nameof(IsSRRLoaded));
            ExportTreeCommand.NotifyCanExecuteChanged();
            OnPropertyChanged(nameof(IsStoredFileSelected));
            OnPropertyChanged(nameof(IsImagePreviewAvailable));
            PreviewStoredImageCommand.NotifyCanExecuteChanged();
            VerifyIntegrityCommand.NotifyCanExecuteChanged();

            if (isSRS)
            {
                SRSFile srs = _sRSData!.SRSFile;
                int blockCount = (srs.FileData is not null ? 1 : 0) + srs.Tracks.Count + srs.ContainerChunks.Count;
                StatusMessage = $"{Path.GetFileName(filePath)} | {srs.ContainerType} | {blockCount} blocks | {_fileSize:N0} bytes";
            }
            else if (isRAR)
            {
                int blockCount = _rarDetailedBlocks!.Count;
                bool isRAR5 = RARBlockLabel.IsRAR5Signature(_rarDetailedBlocks);
                string format = isRAR5 ? "RAR 5.x" : "RAR 4.x";
                StatusMessage = $"{Path.GetFileName(filePath)} | {format} | {blockCount} blocks | {_fileSize:N0} bytes";

                // Detect custom packer sentinels in RAR file headers
                if (DetectCustomPackerInRARBlocks(_rarDetailedBlocks))
                {
                    WarningMessage = "Custom RAR packer detected — file size fields may be unreliable. Known groups: RELOADED, HI2U, QCF.";
                }
            }
            else if (isMKV)
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

            UpdateTextView();
        }
        catch (Exception ex)
        {
            // A newer load (or CloseFile) superseded this one — don't clobber the current state
            // or pop a spurious error dialog for a file the user has already moved on from.
            // Disposal counts as superseding for the same reason.
            if (loadGeneration != _loadGeneration || _disposed)
            {
                return;
            }

            HasFile = false;

            // Surface the failure directly so the user isn't left wondering why the file
            // didn't open; the status bar alone is easy to miss. The dialog carries the
            // detail, so the status text stays brief.
            StatusMessage = "Could not open file";
            _fileDialog.ShowError(
                "Could not open file",
                $"{Path.GetFileName(filePath)} could not be opened.\n\n{ex.Message}");
        }
    }

    // Runs on a background thread (via Task.Run in LoadFileAsync): all the heavy file parsing,
    // returning an immutable bundle the UI thread then applies. Keeps no VM state so it is safe
    // off-thread.
    private static ParsedFileData ParseFileData(string filePath, bool isSRS, bool isRAR, bool isMKV, int mkvMaxElements)
    {
        long fileSize = new FileInfo(filePath).Length;

        if (isSRS)
        {
            return new ParsedFileData { SRS = SRSInspectorData.Load(filePath), FileSize = fileSize };
        }

        if (isRAR)
        {
            return new ParsedFileData { RAR = RARDetailedParser.Parse(filePath), FileSize = fileSize };
        }

        if (isMKV)
        {
            return new ParsedFileData { MKV = MKVFileData.Load(filePath, mkvMaxElements), FileSize = fileSize };
        }

        return new ParsedFileData { SRR = SRRFileData.Load(filePath), FileSize = fileSize };
    }

    private sealed class ParsedFileData
    {
        public SRSInspectorData? SRS { get; init; }
        public IReadOnlyList<RARDetailedBlock>? RAR { get; init; }
        public MKVFileData? MKV { get; init; }
        public SRRFileData? SRR { get; init; }
        public long FileSize { get; init; }
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
        OnPropertyChanged(nameof(IsImagePreviewAvailable));
        PreviewStoredImageCommand.NotifyCanExecuteChanged();

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
        else if (value?.Tag is SRROSOHashBlock oso)
        {
            SetHexBlock(oso.BlockPosition, oso.HeaderSize);
            HasProperties = true;
        }
        else if (value?.Tag is SRRRARPaddingBlock padding)
        {
            SetHexBlock(padding.BlockPosition, padding.HeaderSize + padding.AddSize);
            HasProperties = true;
        }
        else if (value?.Tag is SRRStoredFileBlock stored)
        {
            SetHexBlock(stored.BlockPosition, stored.HeaderSize + stored.AddSize);
            HasProperties = true;
        }
        else if (value?.Tag is SRRRARFileBlock rar)
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
        UpdateTextView();
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

        // For a stored file, export just the embedded file's payload — not the SRR StoredFile
        // block header that wraps it — so the result is a usable standalone file (e.g. an .srs
        // that opens in the Inspector). Other block types export their raw selected bytes.
        long offset;
        long length;
        if (SelectedTreeNode?.Tag is SRRStoredFileBlock storedExport)
        {
            offset = storedExport.DataOffset;
            length = storedExport.FileLength;
        }
        else
        {
            offset = HexBlockOffset;
            length = HexBlockLength;
        }

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
            FileDialogFilters.AllFiles, LoadedFilePath);

        if (filePath is null)
        {
            return;
        }

        string srrPath = _loadedFilePathInternal!;

        string outcome;
        try
        {
            ReleaseFileHandles();
            string storedName = Path.GetFileName(filePath);
            _sRREditingService.AddStoredFiles(srrPath, [(storedName, filePath)]);
            outcome = $"Added stored file: {storedName}";
        }
        catch (Exception ex)
        {
            outcome = $"Error adding stored file: {ex.Message}";
        }

        // Always re-open: ReleaseFileHandles disposed the data source, so a failed edit would
        // otherwise leave the Hex/Text panes blank until close+reopen. The SUCCESS message used to
        // be set before the reload and was therefore wiped by its status summary, exactly as the
        // error was before that was moved after it — reporting both afterwards fixes the pair.
        await ReloadAfterEditAsync(srrPath, outcome);
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

        if (string.IsNullOrEmpty(_loadedFilePathInternal))
        {
            return;
        }

        // The INTERNAL path, not the bound LoadedFilePath. That property is an editable text box:
        // a user who retypes the path in a different spelling (case, a "\.\" segment, short vs
        // long form) without pressing Enter would make this capture disagree with the path
        // ReloadAfterEditAsync compares against, and the reload would be skipped as "superseded"
        // while the handles released above stayed released. Add and Remove already capture this.
        string srrPath = _loadedFilePathInternal;
        ReleaseFileHandles();

        string outcome;
        try
        {
            await _sRREditingService.MoveStoredFileAsync(srrPath, stored.FileName, offset);
            outcome = $"Moved stored file: {stored.FileName}";
        }
        catch (Exception ex)
        {
            outcome = $"Error moving stored file: {ex.Message}";
        }

        await ReloadAfterEditAsync(srrPath, outcome);
    }

    /// <summary>
    /// Reloads the edited SRR and reports <paramref name="outcome"/> — but only if nothing has
    /// superseded this edit in the meantime.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Two defects live here, and both came from treating <see cref="LoadFileAsync"/> as a
    /// harmless "refresh". It is a NAVIGATION primitive: it bumps <c>_loadGeneration</c> and
    /// claims latest-writer-wins. Calling it unconditionally from an edit's <c>finally</c> meant a
    /// slow rename started before the user opened another file finished LAST, made itself the
    /// newest generation, invalidated the file the user actually asked for, and pulled the
    /// Inspector back to the old one.
    /// </para>
    /// <para>
    /// The second defect is that the reload writes <c>StatusMessage</c> on every path, so setting
    /// the outcome BEFORE it — as the old <c>try</c>/<c>catch</c> did — meant the user never saw
    /// either the confirmation or the error. Reporting after the reload fixes that; reporting only
    /// when still current keeps a stale edit from overwriting the status of a file it does not
    /// belong to.
    /// </para>
    /// </remarks>
    private async Task ReloadAfterEditAsync(string srrPath, string outcome)
    {
        if (_disposed)
        {
            return;
        }

        // "Superseded" means the view has moved to a DIFFERENT file (or closed) — tested by the
        // REQUESTED path, not by the generation counter and not by the applied path.
        //
        // Not the counter: another edit to the SAME file bumps it too, so a counter check skipped
        // the reload there and left the tree showing the file as it was BEFORE this edit's write
        // landed (Move Up followed by Remove reproduced it).
        //
        // Not the applied path: while a newly opened file is still parsing, the APPLIED path is
        // still the old one, so this would call the old file current, reload it, and supersede the
        // navigation the user just made.
        if (!string.Equals(_requestedFilePath, srrPath, StringComparison.Ordinal))
        {
            return;
        }

        await LoadFileAsync(srrPath);

        // Re-checked after the await: the reload itself yields, and a file opened during it can
        // finish first. Writing the outcome unconditionally would stamp this edit's message over
        // the status of a file it has nothing to do with.
        if (!_disposed && string.Equals(_requestedFilePath, srrPath, StringComparison.Ordinal))
        {
            StatusMessage = outcome;
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

        string srrPath = _loadedFilePathInternal!;
        ReleaseFileHandles();

        string outcome;
        try
        {
            await _sRREditingService.RenameStoredFileAsync(srrPath, stored.FileName, newName);
            outcome = $"Renamed stored file: {stored.FileName} → {newName}";
        }
        catch (Exception ex)
        {
            outcome = $"Error renaming stored file: {ex.Message}";
        }

        await ReloadAfterEditAsync(srrPath, outcome);
    }

    [RelayCommand(CanExecute = nameof(CanMoveStoredFileUp))]
    private async Task MoveStoredFileUpAsync() => await MoveStoredFileByOffsetAsync(-1);

    [RelayCommand(CanExecute = nameof(CanMoveStoredFileDown))]
    private async Task MoveStoredFileDownAsync() => await MoveStoredFileByOffsetAsync(+1);

    private bool CanVerifyIntegrity() => IsSRRFileLoaded();

    [RelayCommand(CanExecute = nameof(CanRemoveStoredFile))]
    private async Task RemoveStoredFileFromSRRAsync()
    {
        if (!IsSRRFileLoaded() || SelectedTreeNode?.Tag is not SRRStoredFileBlock stored)
        {
            return;
        }

        string srrPath = _loadedFilePathInternal!;

        string outcome;
        try
        {
            ReleaseFileHandles();
            _sRREditingService.RemoveStoredFiles(srrPath, [stored.FileName]);
            outcome = $"Removed stored file: {stored.FileName}";
        }
        catch (Exception ex)
        {
            outcome = $"Error removing stored file: {ex.Message}";
        }

        // Always re-open: ReleaseFileHandles disposed the data source, so a failed edit would
        // otherwise leave the Hex/Text panes blank until close+reopen. As with Add, the SUCCESS
        // message was previously set before the reload and buried by its status summary.
        await ReloadAfterEditAsync(srrPath, outcome);
    }

    [RelayCommand(CanExecute = nameof(IsImagePreviewAvailable))]
    private async Task PreviewStoredImageAsync()
    {
        if (SelectedTreeNode?.Tag is not SRRStoredFileBlock stored
            || string.IsNullOrEmpty(_loadedFilePathInternal))
        {
            return;
        }

        try
        {
            byte[]? bytes = await _sRREditingService.ReadStoredFileBytesAsync(_loadedFilePathInternal, stored.FileName);
            if (bytes is null)
            {
                StatusMessage = $"Could not read stored file: {stored.FileName}";
                return;
            }

            _imagePreviewService.Preview(bytes, stored.FileName);
        }
        catch (Exception ex)
        {
            StatusMessage = $"Error previewing image: {ex.Message}";
        }
    }

    [RelayCommand(CanExecute = nameof(CanVerifyIntegrity))]
    private async Task VerifyIntegrityAsync()
    {
        if (string.IsNullOrEmpty(LoadedFilePath))
        {
            return;
        }

        // Cleared first, before the await: VerifyAnnouncement drives a live region, and verifying
        // the same file twice produces a byte-identical string, which the generated setter would
        // suppress as an equal value — leaving the second verify silent. Clearing guarantees a real
        // empty->text transition. Same reasoning as SRSReconstructorViewModel.RebuildAsync and
        // SRREditorViewModel.Save().
        VerifyAnnouncement = string.Empty;

        SRRVerifyResult result = await _verifyService.VerifyAsync(LoadedFilePath);
        VerifyResultText = FormatVerifyResult(result);
        VerifyAnnouncement = SummarizeVerifyResult(result);
        IsVerifyResultVisible = true;
    }

    /// <summary>
    /// The one-line verdict a screen reader should hear when the verify panel appears.
    /// <para>
    /// Deliberately NOT <see cref="VerifyResultText"/>: that carries a line per issue, and a polite
    /// live region would read every one of them aloud before the user could act. The detail stays
    /// where it is, in the panel's own text box, reachable whenever it is wanted.
    /// </para>
    /// </summary>
    private static string SummarizeVerifyResult(SRRVerifyResult result) => result.Issues.Count switch
    {
        0 when result.IsValid => "Integrity verify: no errors found.",
        0 => "Integrity verify: errors detected.",
        1 => "Integrity verify: errors detected, 1 issue.",
        int n => $"Integrity verify: errors detected, {n} issues.",
    };

    [RelayCommand]
    private void DismissVerifyResult() => IsVerifyResultVisible = false;

    [RelayCommand]
    private void ShowHexSearch()
    {
        // Search applies to the Hex view, so switch to the Hex tab first — otherwise the
        // search bar (which lives in that tab) would never become visible from the Text tab.
        IsTextViewActive = false;
        IsHexSearchVisible = true;
    }

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
            TreeRoots.Add(InspectorTreeBuilder.BuildSRS(_sRSData.SRSFile));
            return;
        }

        if (_rarDetailedBlocks is not null)
        {
            TreeRoots.Add(InspectorTreeBuilder.BuildRAR(_rarDetailedBlocks));
            return;
        }

        if (_mkvData is not null)
        {
            TreeRoots.Add(InspectorTreeBuilder.BuildMKV(_mkvData));
            return;
        }

        if (_sRRData is null)
        {
            return;
        }

        TreeRoots.Add(InspectorTreeBuilder.BuildSRR(_sRRData));
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

    private static bool DetectCustomPackerInRARBlocks(IReadOnlyList<RARDetailedBlock> blocks)
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

        // HexDataSource is a slice based at HexBlockOffset, so the searcher works in and returns
        // block-relative offsets. Convert the absolute selection seed to slice-relative before the
        // search, then convert the result back to an absolute file offset so the selection, address
        // column, status text, highlight ranges, and Export all use the true coordinate.
        long sliceBase = HexBlockOffset;
        long relSelection = HexSelectionOffset >= sliceBase ? HexSelectionOffset - sliceBase : -1;

        long start = forward
            ? (relSelection >= 0 ? relSelection + 1 : 0)
            : (relSelection >= 0 ? relSelection : HexDataSource.Length);

        long match = forward
            ? HexSearcher.FindForward(HexDataSource, pattern, start)
            : HexSearcher.FindBackward(HexDataSource, pattern, start);

        if (match < 0)
        {
            HexSearchStatus = "Not found.";
            return;
        }

        ApplyHexMatch(match + sliceBase, pattern.Bytes.Length);
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

        // HexDataSource is a slice based at HexBlockOffset; seed with a slice-relative offset and
        // convert the match back to an absolute file offset (see RunHexSearch).
        long sliceBase = HexBlockOffset;
        long start = HexSelectionOffset >= sliceBase ? HexSelectionOffset - sliceBase : 0;
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

        ApplyHexMatch(match + sliceBase, pattern.Bytes.Length);
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

        // FindAll returns block-relative offsets over the slice; the hex view treats highlight
        // ranges as absolute file offsets, so rebase onto HexBlockOffset.
        long sliceBase = HexBlockOffset;
        var ranges = new List<HexMatchRange>(offsets.Count);
        foreach (long offset in offsets)
        {
            ranges.Add(new HexMatchRange(offset + sliceBase, pattern.Bytes.Length));
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

    private void UpdateTextView()
    {
        if (!IsTextViewActive || HexDataSource is null || HexBlockLength <= 0)
        {
            TextViewContent = string.Empty;
            TextViewTruncated = false;
            return;
        }

        (TextViewContent, TextViewTruncated) = TextDecoder.Decode(
            HexDataSource, HexBlockLength, SelectedEncoding.Encoding, TextViewMaxBytes);
    }

    partial void OnIsTextViewActiveChanged(bool value) => UpdateTextView();

    partial void OnSelectedEncodingChanged(TextEncodingOption value)
    {
        if (IsTextViewActive)
        {
            UpdateTextView();
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
            // Advance the generation FIRST. LoadFileAsync captures it before its off-thread parse
            // and compares afterwards; disposal used to leave it untouched, so a load still in
            // flight when the Inspector closed passed that check and went on to construct a fresh
            // MemoryMappedDataSource on an already-disposed view-model. Nothing could ever release
            // it — this method returns early on the next call because _disposed is set — so the
            // mapping and its file handle leaked, keeping the file locked on Windows. The comment
            // on LoadFileAsync's own bump describes exactly this leak; the guard simply did not
            // cover the disposal path.
            _loadGeneration++;

            // dispose managed resources
            _fileDataSource?.Dispose();
            _fileDataSource = null;
        }

        // note: no unmanaged resources to release here

        _disposed = true;
    }
}
