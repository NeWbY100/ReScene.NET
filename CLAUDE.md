# ReScene.NET — Coding Guidelines

## Project Overview

WPF desktop app (.NET 10) for inspecting, creating, and reconstructing ReScene (SRR/SRS) files. Uses MVVM with CommunityToolkit.Mvvm 8.4. The shared library (`ReScene.Lib`) is a Git submodule at `ReScene.Lib/` containing RAR, SRR, and Core modules in a single project.

## Build & Test

```bash
dotnet build                              # Build entire solution
dotnet test ReScene.Lib/ReScene.Lib.Tests  # Run all library tests
dotnet test                               # Run all tests
dotnet run --project ReScene.NET          # Run the app
```

## Project Structure

```
ReScene.NET/                    # Solution root
├── ReScene.NET/                # WPF app project (net10.0-windows)
│   ├── Views/                  # XAML views + code-behind (.xaml + .xaml.cs)
│   ├── ViewModels/             # CommunityToolkit.Mvvm partial classes
│   ├── Models/                 # Plain data classes (DTOs)
│   ├── Services/               # Business logic, interface + implementation pairs
│   ├── Controls/               # Custom WPF controls (DependencyProperty-based)
│   ├── Converters/             # IValueConverter implementations
│   ├── Helpers/                # Static utility classes (e.g., DarkTitleBar)
│   └── Resources/              # Tokens.xaml (design tokens), icons
├── ReScene.Lib/                # Git submodule
│   ├── ReScene.Lib/            # Single library project
│   │   ├── RAR/                # RAR 4.x/5.x header parsing, patching
│   │   ├── SRR/                # SRR/SRS file format reading and writing
│   │   └── Core/               # Brute-force orchestration, reconstruction
│   └── ReScene.Lib.Tests/      # xUnit tests
└── docs/resources/             # Screenshots for README
```

## .csproj Configuration

```xml
<TargetFramework>net10.0-windows</TargetFramework>
<ImplicitUsings>enable</ImplicitUsings>
<Nullable>enable</Nullable>
<UseWPF>true</UseWPF>
```

- Implicit usings are enabled — do not add `using System;`, `using System.Collections.Generic;`, etc.
- `System.IO` is explicitly added via `<Using Include="System.IO" />` in the csproj
- Nullable reference types are enabled globally — always use `?` for nullable types

---

## C# Conventions

### Formatting

- **4-space indentation** in all `.cs` files (no tabs)
- **2-space indentation** in all `.xaml` files (no tabs)
- **Allman brace style** — opening brace on its own line:

```csharp
// CORRECT — Allman style
if (path != null)
{
    LoadFile(path);
}

// WRONG — K&R style
if (path != null) {
    LoadFile(path);
}
```

- **Always use braces** for `if`/`else`/`for`/`while`/`foreach` bodies, even single-statement ones:

```csharp
// CORRECT — always use braces
if (path is null)
{
    return;
}

if (path is not null)
{
    OutputPath = path;
}

// WRONG — braceless bodies
if (path is null) return;
if (path is not null)
    OutputPath = path;
```

- **No trailing commas** in object initializers or collection expressions:

```csharp
// CORRECT
var entry = new VersionEntry
{
    VersionName = label,
    Arguments = args
};

// WRONG — no trailing comma
var entry = new VersionEntry
{
    VersionName = label,
    Arguments = args,
};
```

### XML Doc Comments

Use multi-line format for all XML doc comments. Single-line `/// <summary>Text</summary>` is acceptable only for very short property descriptions:

```csharp
// CORRECT — multi-line format (preferred)
/// <summary>
/// Gets or sets the block CRC value.
/// </summary>
public ushort Crc { get; set; }

// ACCEPTABLE — single-line for short property docs
/// <summary>Gets or sets the absolute path to the file on disk.</summary>
public string FullPath { get; set; } = string.Empty;
```

- `<param>` and `<returns>` tags go on their own lines
- Blank line before each XML doc block
- Do not add XML doc comments to private members

### Blank Lines

- **Between methods**: Always one blank line
- **Between property groups**: One blank line between logical groups
- **After opening brace**: No blank line after `{` for class/method bodies
- **After closing brace**: Always a blank line after `}` before the next statement, unless the next line is `}`, `else`, `catch`, `finally`, or `while` (do-while):

```csharp
// CORRECT — blank line after }
if (condition)
{
    DoSomething();
}

NextStatement();

// CORRECT — no blank line before else/catch/finally
if (condition)
{
    DoSomething();
}
else
{
    DoOther();
}

// CORRECT — no blank line before closing }
if (condition)
{
    DoSomething();
}
```

- **Between logical sections**: Blank line between logical chunks in long methods, typically with a comment

### Region Directives

Use `#region` / `#endregion` to group related methods in large files (e.g., `#region Commands`, `#region File Loading`). Blank line before `#region` and after `#endregion`.

### Method Parameters

Keep parameters on one line when they fit within ~120 characters. When wrapping, each parameter goes on its own indented line:

```csharp
// Same line — short enough
public Task<string?> OpenFileAsync(string title, IReadOnlyList<string> filters)

// Wrapped — each on own line
public async Task<SrrCreationResult> CreateAsync(
    string outputPath,
    IReadOnlyList<string> rarVolumePaths,
    IReadOnlyDictionary<string, string>? storedFiles = null,
    SrrCreationOptions? options = null,
    CancellationToken ct = default)
```

### Naming

| Element               | Convention             | Example                                    |
|-----------------------|------------------------|--------------------------------------------|
| Private fields        | `_camelCase`           | `_manager`, `_srrData`, `_cts`             |
| Public properties     | `PascalCase`           | `WindowTitle`, `StatusMessage`             |
| Methods               | `PascalCase`           | `LoadFile`, `ShowProperties`               |
| Async methods         | `PascalCase` + `Async` | `CreateSrrAsync`, `BrowseInputAsync`       |
| Constants             | `PascalCase`           | `MaxCopyBytes`, `CharWidth`                |
| Win32/P/Invoke const  | `UPPER_SNAKE_CASE`     | `DWMWA_USE_IMMERSIVE_DARK_MODE`            |
| Interfaces            | `IPascalCase`          | `IFileDialogService`, `IHexDataSource`     |
| Local variables       | `camelCase`            | `dialog`, `bytesRead`, `elapsed`           |
| Enum values           | `PascalCase`           | `LogTarget.System`, `LogTarget.Phase1`     |
| Event handlers        | `OnEventName`          | `OnProgress`, `OnFileCopyProgress`         |
| Boolean properties    | `Is`/`Has`/`Can` prefix| `IsCreating`, `HasWarning`, `CanCreate`    |
| Observable fields     | `_camelCase`           | `_windowTitle`, `_isCreating`              |
| EventArgs classes     | `PascalCase` + `EventArgs` | `FileCopyProgressEventArgs`            |

### Namespaces

Always use file-scoped namespaces:

```csharp
namespace ReScene.NET.ViewModels;
```

Never use block-scoped namespaces.

### Using Directives

Sorted by category: System → third-party frameworks → project/domain. Implicit usings cover most System namespaces, so explicit `using` directives are only needed for non-implicit ones:

```csharp
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RAR;
using SRR;
using ReScene.NET.Models;
using ReScene.NET.Services;
```

### Type Usage and `var`

Use `var` when the type is obvious or when it reduces noise. Use explicit types when the type is not immediately clear:

```csharp
// var — type is obvious from new, cast, or well-known methods
var dialog = new OpenFileDialog { Title = "Select File" };
var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
var args = Environment.GetCommandLineArgs();
var files = e.Data.GetData(DataFormats.FileDrop) as string[];
var sb = new StringBuilder();

// Explicit type — type is not obvious from the method name
ProcessingResult result = GetProcessingResult();
IReadOnlyList<RARDetailedBlock> blocks = parser.ReadBlocks();
```

Use target-typed `new` when the type is declared on the left side:

```csharp
byte[] buffer = new byte[1048576 * 32];
FileStream destStream = new(destPath, FileMode.Create, FileAccess.Write);
```

Use collection expressions `[]` for empty collections and small inline arrays:

```csharp
public ObservableCollection<string> LogEntries { get; } = [];
string[] suffixes = ["B", "KB", "MB", "GB", "TB"];
```

### Access Modifiers

Always use explicit access modifiers. Never rely on defaults:

```csharp
public partial class HomeViewModel : ViewModelBase
{
    private readonly IFileDialogService _fileDialog;
    internal static class DarkTitleBar { }
}
```

### Expression-Bodied Members

Use for simple one-line members:

```csharp
public bool HasWarning => !string.IsNullOrEmpty(WarningMessage);
private bool CanCreateSrr() => !IsCreating && !string.IsNullOrWhiteSpace(InputPath);
private void FireProgress(EventArgs e) => Progress?.Invoke(this, e);
```

Use block bodies for anything with multiple statements or conditionals.

### Switch Expressions

Use switch expressions for multi-branch conditional assignments:

```csharp
ProgressMessage = e.CompletionStatus switch
{
    OperationCompletionStatus.Success => "Completed successfully!",
    OperationCompletionStatus.Error => "Failed.",
    OperationCompletionStatus.Cancelled => "Cancelled.",
    _ => "Completed."
};

string defaultName = SelectedTreeNode?.Tag switch
{
    SrrStoredFileBlock stored => Path.GetFileName(stored.FileName),
    RARDetailedBlock { ItemName: { } name } => name,
    _ => "block.bin"
};
```

### Pattern Matching

Use `is { }` for non-null checks with destructuring. Use property patterns for concise type + state checks:

```csharp
if (value?.ByteRange is { } range)
{
    HexSelectionOffset = range.Offset;
    HexSelectionLength = range.Length;
}

if (sender is ReconstructorViewModel { IsRunning: true })
    ShowProgressWindow();

if (DataContext is MainWindowViewModel vm)
    vm.OpenSceneFile(file);
```

### String Formatting

Use string interpolation. Never use string concatenation for building display strings:

```csharp
$"{e.OperationProgressed:N0} of {e.OperationSize:N0}"    // "1,234 of 5,678"
$"{e.Progress:F1}%"                                       // "45.3%"
$"{size:0.##} {suffixes[i]}"                              // "1.5 GB"
$"{bytesPerSec / (1024 * 1024):F1} MB/s"                 // "123.4 MB/s"
$"{DateTime.Now:HH:mm:ss} {message}"                     // "14:30:05 message"
$"{ts.Hours:D2}:{ts.Minutes:D2}:{ts.Seconds:D2}"         // "01:23:45"
$"0x{value:X8}"                                           // "0x0000FF00"
$"{b:X2}"                                                 // "4A"
```

File sizes use **binary** divisions (1024), not decimal (1000).

Use `StringBuilder` for string building in tight loops (e.g., hex rendering), not interpolation.

### Null Handling

Use `is null` / `is not null` instead of `== null` / `!= null`:

```csharp
// CORRECT
if (files is null) return;
if (files is not null) { ... }

// WRONG
if (files == null) return;
if (files != null) { ... }
```

Use null-conditional and null-coalescing operators:

```csharp
string dir = Path.GetDirectoryName(inputPath) ?? ".";     // Null-coalescing
Progress?.Invoke(this, e);                                 // Null-conditional
_cts?.Cancel();
```

Use null-forgiving `!` only when guaranteed non-null by surrounding context.

### String Defaults

Use `string.Empty` for default string values, not `""`:

```csharp
[ObservableProperty]
private string _inputPath = string.Empty;

public string Name { get; set; } = string.Empty;
```

### LINQ

Use method syntax exclusively. Do not use query syntax (`from x in ...`):

```csharp
// CORRECT — method syntax
var sorted = files.OrderBy(f => f.Name).ToList();
var matches = blocks.Where(b => b.Type == BlockType.File).Select(b => b.Name);

// WRONG — query syntax (not used in this codebase)
var sorted = from f in files orderby f.Name select f;
```

### Discards for Unused Parameters

Use `_` for unused parameters, especially `sender` in event handlers and code-behind click handlers:

```csharp
// Event handler — sender not used
private void OnProgress(object? _, BruteForceProgressEventArgs e)
{
    ProgressPercent = e.Progress;
}

// Code-behind click handler — sender not used
private void OnExitClick(object _, RoutedEventArgs e)
{
    Close();
}

// Lambda — both parameters unused
SourceInitialized += (_, _) => DarkTitleBar.Enable(this);

// Lambda — only sender unused
_srrService.Progress += (_, e) => LogMessage?.Invoke(_, e);  // WRONG — forward sender
logger.Logged += (s, e) => LogMessage?.Invoke(s, e);         // OK — sender is forwarded
```

Note: In code-behind, WPF event handler signatures require `object sender` (not `object? sender`), so use `object _` there.

### Disposable Resources

Always use `using` statements for disposable resources:

```csharp
using FileStream sourceStream = File.OpenRead(sourcePath);
using (FileStream destStream = new(destPath, FileMode.Create, FileAccess.Write))
{
    // ...
}
```

### Generated Regex

Use `[GeneratedRegex]` for compile-time regex generation:

```csharp
[GeneratedRegex(@"(?:win)?(?:rar|wr)(?:-x64|-x32)?-?(\d+)(b\d+)?", RegexOptions.IgnoreCase)]
private static partial Regex VersionLabelRegex();
```

---

## MVVM Patterns (CommunityToolkit.Mvvm)

### ViewModel Base

All ViewModels inherit from `ViewModelBase` and must be `partial` classes:

```csharp
public abstract class ViewModelBase : ObservableObject { }

public partial class HomeViewModel : ViewModelBase { }
```

### Member Ordering Within ViewModels

Follow this order within a ViewModel class:

1. **Constants and static fields**
2. **Private readonly fields** (dependencies, services)
3. **Private fields** (Stopwatch, CancellationTokenSource, etc.)
4. **Constructor(s)**
5. **Observable properties** (`[ObservableProperty]` fields)
6. **Computed properties** (read-only `=>` expressions)
7. **Observable collections** (`ObservableCollection<T>` properties)
8. **Commands** (`[RelayCommand]` methods) and their CanExecute methods
9. **Property change handlers** (`partial void On<Property>Changed`)
10. **Event handlers** (`OnProgress`, `OnStatusChanged`, etc.)
11. **Private helper methods**
12. **IDisposable implementation** (if applicable)

### Observable Properties

Declare as private fields with `[ObservableProperty]`. The source generator creates a public PascalCase property:

```csharp
// Field _windowTitle → generated public property WindowTitle
[ObservableProperty]
private string _windowTitle = "ReScene.NET";

// Notify a command to re-evaluate CanExecute when this property changes
[ObservableProperty]
[NotifyCanExecuteChangedFor(nameof(CreateSrrCommand))]
private bool _isCreating;

// Notify dependent computed properties when this property changes
[ObservableProperty]
[NotifyPropertyChangedFor(nameof(HasWarning))]
private string? _warningMessage;

// Computed property (read-only, no backing field, no attribute)
public bool HasWarning => !string.IsNullOrEmpty(WarningMessage);
```

### Property Change Handlers

The MVVM Toolkit generates `partial void On<PropertyName>Changed` hooks. Implement to handle side effects:

```csharp
partial void OnInputPathChanged(string value)
{
    if (!string.IsNullOrWhiteSpace(value))
        IsSfvInput = Path.GetExtension(value).Equals(".sfv", StringComparison.OrdinalIgnoreCase);
}

partial void OnSelectedTreeNodeChanged(TreeNodeViewModel? value)
{
    Properties.Clear();
    if (value?.Tag is RARDetailedBlock detailedBlock)
    {
        ShowDetailedBlockProperties(detailedBlock);
        SetHexBlock(detailedBlock.StartOffset, detailedBlock.TotalSize);
    }
}
```

### Relay Commands

Use `[RelayCommand]` on private methods. The generated command property name is `{MethodName}Command`:

```csharp
// Generates OpenInspectCommand
[RelayCommand]
private async Task OpenInspectAsync() { ... }

// Generates CreateSrrCommand with CanExecute check
[RelayCommand(CanExecute = nameof(CanCreateSrr))]
private async Task CreateSrrAsync() { ... }

private bool CanCreateSrr() => !IsCreating
    && !string.IsNullOrWhiteSpace(InputPath)
    && !string.IsNullOrWhiteSpace(OutputPath);

// Generates OpenRecentFileCommand with parameter
[RelayCommand]
private void OpenRecentFile(RecentFileEntry entry) { ... }
```

### Collections

Always use `ObservableCollection<T>` for bindable lists. Initialize with `= []`:

```csharp
public ObservableCollection<string> StoredFiles { get; } = [];
public ObservableCollection<PropertyItem> Properties { get; } = [];
```

### Nested/Inner Classes in ViewModels

Use public partial nested classes inheriting from `ObservableObject` for tightly-coupled display items:

```csharp
public partial class VersionEntry : ObservableObject
{
    [ObservableProperty] private string _versionName = "";
    [ObservableProperty] private string _status = "Testing";
    [ObservableProperty] private string _arguments = "";
    [ObservableProperty] private string _result = "";
}
```

---

## Dependency Injection

No DI container is used. Services are instantiated manually in `App.xaml.cs` and passed to the root ViewModel:

```csharp
// App.xaml.cs
MainWindow = new MainWindow
{
    DataContext = new MainWindowViewModel(
        new SrrCreationService(), new SrsCreationService(), new BruteForceService(),
        new FileCompareService(), new FileDialogService(), new RecentFilesService())
};
```

Root ViewModel creates child ViewModels and passes dependencies down.

ViewModels receive dependencies via constructor injection and store as `private readonly` fields:

```csharp
public partial class CreatorViewModel : ViewModelBase
{
    private readonly ISrrCreationService _srrService;
    private readonly IFileDialogService _fileDialog;

    public CreatorViewModel(ISrrCreationService srrService, IFileDialogService fileDialog)
    {
        _srrService = srrService;
        _fileDialog = fileDialog;
        _srrService.Progress += OnProgress;
    }
}
```

Primary constructor syntax is also used in some ViewModels:

```csharp
public partial class InspectorViewModel(IFileDialogService fileDialog) : ViewModelBase, IDisposable
{
    private readonly IFileDialogService _fileDialog = fileDialog;
}
```

MainWindowViewModel has a parameterless constructor that chains to the full constructor for XAML designer support:

```csharp
public MainWindowViewModel()
    : this(new SrrCreationService(), new SrsCreationService(), ...) { }

public MainWindowViewModel(ISrrCreationService srrService, ...) { }
```

---

## Async & Cancellation

### Async Rules

- All async methods end with `Async` suffix
- Never use `async void` — always return `async Task`
- Do not use `ConfigureAwait(false)` in UI code (WPF needs the synchronization context)

### Cancellation Pattern

```csharp
private CancellationTokenSource? _cts;

[RelayCommand(CanExecute = nameof(CanCreateSrr))]
private async Task CreateSrrAsync()
{
    IsCreating = true;
    _cts = new CancellationTokenSource();

    try
    {
        var result = await _srrService.CreateFromSfvAsync(
            OutputPath, InputPath, options, _cts.Token);
    }
    catch (OperationCanceledException)
    {
        Log("Cancelled.");
    }
    catch (Exception ex)
    {
        Log($"Error: {ex.Message}");
    }
    finally
    {
        IsCreating = false;
        _cts?.Dispose();
        _cts = null;
    }
}

[RelayCommand]
private void CancelCreation()
{
    _cts?.Cancel();
    Log("Cancellation requested...");
}
```

---

## Error Handling

- Catch `OperationCanceledException` specifically for cancellation — log a simple "Cancelled." message
- Catch general `Exception ex` for everything else — log `ex.Message` to the UI
- Do not silently swallow exceptions — always log or display them
- Use try-catch in all `[RelayCommand]` async methods that call services

```csharp
try
{
    await _service.DoWorkAsync(_cts.Token);
}
catch (OperationCanceledException)
{
    Log("Cancelled.");
}
catch (Exception ex)
{
    Log($"Error: {ex.Message}");
}
finally
{
    IsProcessing = false;
    _cts?.Dispose();
    _cts = null;
}
```

---

## IDisposable Pattern

ViewModels that hold disposable resources (e.g., `MemoryMappedDataSource`) implement `IDisposable`. Always call `GC.SuppressFinalize(this)` in `Dispose()` — even when there is no finalizer. This is a .NET best practice (CA1816) that protects against future changes and subclasses.

**Non-inheritable classes** — simple `Dispose()`:

```csharp
public partial class FileCompareViewModel(...) : ViewModelBase, IDisposable
{
    public void Dispose()
    {
        _leftFileSource?.Dispose();
        _rightFileSource?.Dispose();
        GC.SuppressFinalize(this);
    }
}
```

**Inheritable classes** — full `Dispose(bool)` pattern:

```csharp
public partial class InspectorViewModel(IFileDialogService fileDialog) : ViewModelBase, IDisposable
{
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
            _dataSource?.Dispose();
        }
        _disposed = true;
    }
}
```

The owning window or parent ViewModel calls `Dispose()` during cleanup (e.g., in `OnClosing`).

---

## Services

### Interface + Implementation Pattern

Every service has an interface (`IFooService`) and implementation (`FooService`):

```csharp
public interface IFileDialogService
{
    Task<string?> OpenFileAsync(string title, IReadOnlyList<string> filters);
    Task<string?> SaveFileAsync(string title, string defaultExtension,
        IReadOnlyList<string> filters, string? defaultFileName = null);
    Task<string?> OpenFolderAsync(string title);
}

public class FileDialogService : IFileDialogService
{
    public Task<string?> OpenFileAsync(string title, IReadOnlyList<string> filters)
    {
        var dialog = new OpenFileDialog
        {
            Title = title,
            Filter = BuildFilter(filters),
            Multiselect = false
        };
        return Task.FromResult(dialog.ShowDialog() == true ? dialog.FileName : null);
    }
}
```

### Event-Based Progress Reporting

Services expose events for progress. ViewModels subscribe in constructors:

```csharp
// Service
public event EventHandler<BruteForceProgressEventArgs>? Progress;
public event EventHandler<FileCopyProgressEventArgs>? FileCopyProgress;

// ViewModel constructor
_bruteForceService.Progress += OnProgress;
_bruteForceService.FileCopyProgress += OnFileCopyProgress;
```

### File Dialog Filters

Filter format is `"Description|*.ext1;*.ext2"`, passed as `IReadOnlyList<string>`:

```csharp
string? path = await _fileDialog.OpenFileAsync("Select Input File",
    ["SFV Files|*.sfv", "RAR Files|*.rar", "All Files|*.*"]);
```

---

## UI Thread Marshaling

Service events fire on background threads. Use Dispatcher to update UI properties.

### `Dispatcher.Invoke()` — blocking, synchronous

Use when you need all updates to complete before returning:

```csharp
private void OnProgress(object? sender, BruteForceProgressEventArgs e)
{
    Application.Current.Dispatcher.Invoke(() =>
    {
        ProgressPercent = e.Progress;
        PhaseDescription = e.PhaseDescription;
    });
}
```

### `Dispatcher.BeginInvoke()` — non-blocking, async

Use for deferred execution, especially for high-frequency progress events and opening modals:

```csharp
private void OnFileCopyProgress(object? sender, FileCopyProgressEventArgs e)
{
    Application.Current.Dispatcher.BeginInvoke(() =>
    {
        if (!IsCopying)
        {
            IsCopying = true;
            _copyStopwatch.Restart();
        }
        CopyProgressPercent = (double)e.BytesCopied / e.TotalBytes * 100;
    });
}
```

### Modal Windows from PropertyChanged

Never open modal dialogs directly in PropertyChanged handlers. Always defer with `BeginInvoke`:

```csharp
private void Vm_PropertyChanged(object? sender, PropertyChangedEventArgs e)
{
    if (e.PropertyName == nameof(ReconstructorViewModel.IsRunning)
        && sender is ReconstructorViewModel { IsRunning: true })
    {
        Dispatcher.BeginInvoke(DispatcherPriority.Normal, () =>
        {
            var window = new BruteForceProgressWindow
            {
                Owner = Window.GetWindow(this),
                DataContext = DataContext
            };
            window.ShowDialog();
        });
    }
}
```

---

## Model Classes

Simple DTOs with auto-properties and initializers. No constructor logic:

```csharp
public class PropertyItem
{
    public string Name { get; set; } = string.Empty;
    public string Value { get; set; } = string.Empty;
    public ByteRange? ByteRange { get; set; }
    public bool HasByteRange => ByteRange != null;
    public bool IsIndented { get; set; }
    public bool IsDifferent { get; set; }
}
```

Do not use record types for models — this codebase uses classes with mutable properties.

---

## Value Converters

Implement `IValueConverter`. Register in `App.xaml` as `StaticResource`. Throw `NotSupportedException` for unsupported `ConvertBack`:

```csharp
public class InverseBoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is bool b)
            return b ? Visibility.Collapsed : Visibility.Visible;
        return Visibility.Visible;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
```

Registration in `App.xaml`:

```xml
<local:InverseBoolToVisibilityConverter x:Key="InverseBoolToVisibility" />
```

---

## Custom Controls (DependencyProperty)

Use the standard WPF DependencyProperty registration pattern:

```csharp
public static readonly DependencyProperty DataSourceProperty =
    DependencyProperty.Register(
        nameof(DataSource),
        typeof(IHexDataSource),
        typeof(HexViewControl),
        new PropertyMetadata(null, OnDataChanged));

public IHexDataSource? DataSource
{
    get => (IHexDataSource?)GetValue(DataSourceProperty);
    set => SetValue(DataSourceProperty, value);
}
```

With optional coercion:

```csharp
public static readonly DependencyProperty BytesPerLineProperty =
    DependencyProperty.Register(nameof(BytesPerLine), typeof(int), typeof(HexViewControl),
        new PropertyMetadata(16, OnBytesPerLineChanged, CoerceBytesPerLine));

private static object CoerceBytesPerLine(DependencyObject d, object baseValue)
{
    int val = (int)baseValue;
    return Math.Max(1, Math.Min(val, 128));
}
```

---

## Code-Behind Rules

Keep code-behind minimal. Only the following belongs in code-behind:

1. **TreeView selection** — WPF TreeView doesn't support two-way SelectedItem binding:
   ```csharp
   private void TreeView_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
   {
       if (DataContext is InspectorViewModel vm)
           vm.SelectedTreeNode = e.NewValue as TreeNodeViewModel;
   }
   ```
2. **Drag-and-drop handling** (DragOver, Drop events)
3. **Window lifecycle** — OnClosing cleanup, OnContentRendered for command-line args
4. **Platform-specific** — `DarkTitleBar.Enable(this)` in SourceInitialized
5. **Modal window management** — opening/closing progress dialogs from PropertyChanged
6. **Event cleanup** — always unsubscribe from events in OnClosing:
   ```csharp
   protected override void OnClosing(CancelEventArgs e)
   {
       if (DataContext is ReconstructorViewModel vm)
           vm.PropertyChanged -= Vm_PropertyChanged;
       base.OnClosing(e);
   }
   ```

Access ViewModel from code-behind using pattern matching:

```csharp
if (DataContext is MainWindowViewModel vm)
    vm.OpenSceneFile(file);
```

Never put business logic, data transformation, or state management in code-behind.

---

## XAML Conventions

### Formatting

- **2-space indentation** (not 4, not tabs)
- Attributes on separate lines when there are 3 or more
- Section comments with decorative borders:
  ```xml
  <!-- ── Section Name ─────────────────────────────────── -->
  ```

### Resource References

- **`DynamicResource`** for all theme-aware values (brushes, colors, font sizes, spacing):
  ```xml
  <TextBlock FontSize="{DynamicResource FontSizeBody}"
             Foreground="{DynamicResource ForegroundPrimary}" />
  ```

- **`StaticResource`** for styles, converters, and non-theme resources:
  ```xml
  <Button Style="{StaticResource PrimaryButton}" />
  <Border Style="{StaticResource PanelSection}" />
  <TextBlock Visibility="{Binding IsVisible, Converter={StaticResource BoolToVisibility}}" />
  ```

### Design Tokens

All tokens defined in `Resources/Tokens.xaml`. Always use tokens — never hardcode colors, font sizes, or spacing:

```xml
<!-- Typography -->
<FontFamily x:Key="UIFontFamily">Segoe UI</FontFamily>
<FontFamily x:Key="MonoFontFamily">Cascadia Mono, Consolas, Courier New, monospace</FontFamily>
<sys:Double x:Key="FontSizeH1">20</sys:Double>
<sys:Double x:Key="FontSizeH2">16</sys:Double>
<sys:Double x:Key="FontSizeBody">14</sys:Double>
<sys:Double x:Key="FontSizeCaption">12</sys:Double>

<!-- Spacing -->
<Thickness x:Key="SpacingMD">8</Thickness>
<Thickness x:Key="PageMargin">12</Thickness>

<!-- Colors (dark theme) -->
<SolidColorBrush x:Key="WindowBackground" Color="#FF1E1E1E" />
<SolidColorBrush x:Key="SurfaceBackground" Color="#FF252526" />
<SolidColorBrush x:Key="ForegroundPrimary" Color="#FFD4D4D4" />
<SolidColorBrush x:Key="ForegroundSecondary" Color="#FF999999" />
<SolidColorBrush x:Key="AccentPrimary" Color="#FF0078D4" />
<SolidColorBrush x:Key="AccentError" Color="#FFF44747" />
<SolidColorBrush x:Key="DiffRowBackground" Color="#33F44747" />
```

### Binding Patterns

```xml
<!-- Direct binding -->
<TextBlock Text="{Binding StatusMessage}" />

<!-- Command binding ([RelayCommand] on Method → MethodCommand) -->
<Button Command="{Binding BrowseInputCommand}" Content="Browse..." />

<!-- Command with parameter -->
<MenuItem Command="{Binding OpenRecentFileCommand}" CommandParameter="{Binding}" />

<!-- Two-way (explicit only when needed — TextBox Text is already TwoWay by default) -->
<TreeViewItem IsSelected="{Binding IsSelected, Mode=TwoWay}"
              IsExpanded="{Binding IsExpanded, Mode=TwoWay}" />

<!-- Real-time input (default UpdateSourceTrigger for TextBox is LostFocus) -->
<TextBox Text="{Binding FilterText, UpdateSourceTrigger=PropertyChanged}" />

<!-- Value converter -->
<TextBlock Visibility="{Binding HasWarning, Converter={StaticResource BoolToVisibility}}" />
```

### Layout Patterns

**Page-level** — DockPanel with top/bottom docked, content fills remaining space:

```xml
<DockPanel Margin="{DynamicResource PageMargin}">
  <TextBlock DockPanel.Dock="Top" Text="Description" />
  <Border DockPanel.Dock="Bottom"><!-- status bar --></Border>
  <Grid><!-- main content fills remaining space --></Grid>
</DockPanel>
```

**Structured layouts** — Grid with explicit definitions:

```xml
<Grid>
  <Grid.RowDefinitions>
    <RowDefinition Height="Auto" />              <!-- Content-sized -->
    <RowDefinition Height="*" MinHeight="100" />  <!-- Fill -->
    <RowDefinition Height="Auto" />              <!-- Splitter -->
    <RowDefinition Height="2*" />                <!-- Proportional fill -->
  </Grid.RowDefinitions>
</Grid>
```

**Resizable panels** — GridSplitter between rows/columns:

```xml
<GridSplitter Grid.Row="1"
              Height="{DynamicResource SplitterHeight}"
              HorizontalAlignment="Stretch"
              ResizeBehavior="PreviousAndNext" />
```

**Panel containers** — Border with PanelSection style wrapping DockPanel with header:

```xml
<Border Style="{StaticResource PanelSection}">
  <DockPanel>
    <Border DockPanel.Dock="Top" Style="{StaticResource PanelHeaderBar}">
      <TextBlock Style="{StaticResource PanelHeaderText}" Text="Section Title" />
    </Border>
    <!-- panel content -->
  </DockPanel>
</Border>
```

**TabControl** — Views with child ViewModel DataContext:

```xml
<TabControl SelectedIndex="{Binding SelectedTabIndex}" Padding="0">
  <TabItem Header="Home">
    <v:HomeView DataContext="{Binding Home}" />
  </TabItem>
  <TabItem Header="Inspector">
    <v:InspectorView DataContext="{Binding Inspector}" />
  </TabItem>
</TabControl>
```

### DataGrid Pattern

```xml
<DataGrid ItemsSource="{Binding Properties}"
          SelectedItem="{Binding SelectedProperty}"
          IsReadOnly="True"
          AutoGenerateColumns="False"
          CanUserReorderColumns="False"
          CanUserSortColumns="False"
          GridLinesVisibility="Horizontal"
          HeadersVisibility="Column"
          SelectionMode="Single"
          BorderThickness="0">
  <DataGrid.RowStyle>
    <Style TargetType="DataGridRow" BasedOn="{StaticResource {x:Type DataGridRow}}">
      <Style.Triggers>
        <DataTrigger Binding="{Binding IsDifferent}" Value="True">
          <Setter Property="Background" Value="{DynamicResource DiffRowBackground}" />
          <Setter Property="Foreground" Value="{DynamicResource AccentError}" />
        </DataTrigger>
      </Style.Triggers>
    </Style>
  </DataGrid.RowStyle>
  <DataGrid.Columns>
    <DataGridTextColumn Header="Name" Binding="{Binding Name}" Width="200" />
    <DataGridTextColumn Header="Value" Binding="{Binding Value}" Width="*" />
  </DataGrid.Columns>
</DataGrid>
```

### TreeView with HierarchicalDataTemplate

```xml
<TreeView ItemsSource="{Binding TreeRoots}"
          SelectedItemChanged="TreeView_SelectedItemChanged"
          FontSize="{DynamicResource FontSizeBody}">
  <TreeView.ItemContainerStyle>
    <Style TargetType="TreeViewItem" BasedOn="{StaticResource {x:Type TreeViewItem}}">
      <Setter Property="IsSelected" Value="{Binding IsSelected, Mode=TwoWay}" />
      <Setter Property="IsExpanded" Value="{Binding IsExpanded, Mode=TwoWay}" />
    </Style>
  </TreeView.ItemContainerStyle>
  <TreeView.ItemTemplate>
    <HierarchicalDataTemplate ItemsSource="{Binding Children}">
      <TextBlock Text="{Binding Text}" TextTrimming="CharacterEllipsis">
        <TextBlock.Style>
          <Style TargetType="TextBlock">
            <Style.Triggers>
              <DataTrigger Binding="{Binding IsDifferent}" Value="True">
                <Setter Property="Foreground" Value="{DynamicResource AccentError}" />
              </DataTrigger>
            </Style.Triggers>
          </Style>
        </TextBlock.Style>
      </TextBlock>
    </HierarchicalDataTemplate>
  </TreeView.ItemTemplate>
</TreeView>
```

### Window Definitions

```xml
<Window x:Class="ReScene.NET.Views.BruteForceProgressWindow"
        xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
        Title="Brute Force Progress"
        Width="750" Height="600"
        MinWidth="600" MinHeight="450"
        WindowStartupLocation="CenterOwner"
        Background="{DynamicResource WindowBackground}"
        Foreground="{DynamicResource ForegroundPrimary}"
        FontFamily="{DynamicResource UIFontFamily}"
        ResizeMode="CanResize">
```

Every window must call `DarkTitleBar.Enable(this)` in SourceInitialized:

```csharp
SourceInitialized += (_, _) => DarkTitleBar.Enable(this);
```

### KeyBindings

KeyBindings must reference commands on the Window's DataContext directly — no dotted paths like `Inspector.ExportBlockCommand`:

```xml
<Window.InputBindings>
  <KeyBinding Gesture="Ctrl+O" Command="{Binding OpenFileCommand}" />
  <KeyBinding Gesture="Ctrl+E" Command="{Binding ExportStoredFileCommand}" />
</Window.InputBindings>
```

If the actual command lives on a child ViewModel, create a forwarding command on MainWindowViewModel:

```csharp
[RelayCommand]
private async Task ExportStoredFileAsync() => await Inspector.ExportBlockCommand.ExecuteAsync(null);
```

---

## Logging

ViewModel log entries use `HH:mm:ss` timestamp prefix:

```csharp
private void Log(string message)
{
    string entry = $"{DateTime.Now:HH:mm:ss} {message}";
    SystemLog = SystemLog.Length == 0 ? entry : SystemLog + Environment.NewLine + entry;
}
```

---

## Helper Classes

Static utility classes go in `Helpers/`. Use `internal static`:

```csharp
internal static class DarkTitleBar
{
    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;

    public static void Enable(Window window)
    {
        if (PresentationSource.FromVisual(window) is HwndSource source)
        {
            int value = 1;
            DwmSetWindowAttribute(source.Handle, DWMWA_USE_IMMERSIVE_DARK_MODE, ref value, sizeof(int));
        }
    }
}
```

---

## Event Handler Patterns

### PropertyChanged Forwarding

MainWindowViewModel subscribes to child ViewModel property changes for status/busy aggregation:

```csharp
Inspector.PropertyChanged += (_, e) =>
{
    if (e.PropertyName == nameof(InspectorViewModel.StatusMessage))
        StatusMessage = Inspector.StatusMessage;
    else if (e.PropertyName == nameof(InspectorViewModel.IsExporting))
        IsBusy = Inspector.IsExporting || Creator.IsCreating;
};
```

### DispatcherTimer for Periodic Updates

```csharp
private readonly DispatcherTimer _elapsedTimer;

// In constructor
_elapsedTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
_elapsedTimer.Tick += OnElapsedTimerTick;

// Start/stop with operations
_elapsedTimer.Start();
_elapsedTimer.Stop();
```

---

## Git Conventions

- Commit messages: `type: short description` (types: `feat`, `fix`, `refactor`, `docs`, `test`, `chore`)
- Do not commit `.env`, credentials, or user-specific files
- Submodule (`ReScene.Lib`): update with `git submodule update --remote` when needed
- Line endings: CRLF (Windows project)

---

## Modern C# Features

Use the latest C# language features available in .NET 10:

- **`is null` / `is not null`** instead of `== null` / `!= null`
- **Pattern matching** — `is { }`, property patterns, switch expressions
- **Collection expressions** — `[]` for empty collections and inline arrays
- **Target-typed `new`** — `FileStream stream = new(...)` when type is on the left
- **File-scoped namespaces** — `namespace X;` (never block-scoped)
- **Primary constructors** — for simple dependency injection in ViewModels
- **Expression-bodied members** — for one-line methods, properties, operators
- **`[GeneratedRegex]`** — compile-time regex via source generator
- **`[ObservableProperty]` / `[RelayCommand]`** — CommunityToolkit.Mvvm source generators
- **Discards `_`** — for unused parameters (`sender`, `e`)
- **`using` declarations** — `using var stream = ...;` without braces where appropriate
- **Raw string literals** — `"""..."""` for multi-line strings if needed
- **`string.IsNullOrEmpty` / `string.IsNullOrWhiteSpace`** — prefer over manual null + length checks
- **`GC.SuppressFinalize(this)`** — always call in `Dispose()`, even without a finalizer (CA1816)

---

## Event Handler Naming

### In ViewModels

Event handlers subscribed to service events use the `On` prefix with the event name:

```csharp
_bruteForceService.Progress += OnProgress;
_bruteForceService.FileCopyProgress += OnFileCopyProgress;
_srrService.Progress += OnProgress;
```

### In Code-Behind

Code-behind event handlers use the `On` prefix describing the action:

```csharp
// CORRECT
private void OnLoaded(object _, RoutedEventArgs e) { }
private void OnStopCloseClick(object _, RoutedEventArgs e) { }
private void OnCancelClick(object _, RoutedEventArgs e) { }
private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e) { }
private void OnDragOver(object _, DragEventArgs e) { }

// WRONG — WinForms-style naming
private void BtnCancel_Click(object sender, RoutedEventArgs e) { }
private void Vm_PropertyChanged(object? sender, PropertyChangedEventArgs e) { }
```

---

## Do NOT

- Add XML doc comments, type annotations, or comments to code you didn't write or change
- Add comments where the logic is self-evident
- Over-engineer or add abstractions for single-use code
- Create new files unless necessary — prefer editing existing ones
- Use `async void` — always `async Task`
- Swallow exceptions silently — always log `ex.Message`
- Change formatting or style in lines you didn't modify
- Hardcode colors, font sizes, or spacing — use design tokens from Tokens.xaml
- Use block-scoped namespaces — always file-scoped
- Use string concatenation for display strings — use interpolation
- Use LINQ query syntax — use method syntax
- Use record types for models — use classes with mutable properties
- Open modal dialogs directly in PropertyChanged handlers — defer with BeginInvoke
- Put business logic in code-behind — only UI-specific plumbing belongs there
- Use `ConfigureAwait(false)` in UI code
- Add trailing commas in object/collection initializers
- Use `== null` or `!= null` — use `is null` / `is not null` instead
- Omit braces on `if`/`else`/`for`/`while`/`foreach` bodies — always use braces, even for single statements
- Omit blank line after `}` before the next statement (except before `else`/`catch`/`finally`/`}`)
- Leave unused parameters named — use `_` discard for unused `sender`, `e`, etc.
- Name event handlers with WinForms-style `BtnFoo_Click` — use `OnFooClick` instead
- Skip `GC.SuppressFinalize(this)` in Dispose — always include it
