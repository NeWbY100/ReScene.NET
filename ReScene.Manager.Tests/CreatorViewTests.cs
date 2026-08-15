using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using ReScene.App.Core.Models;
using ReScene.App.Core.Services;
using ReScene.App.Core.ViewModels;
using ReScene.Manager.Services;
using ReScene.Manager.Views;
using ReScene.SRR;
using ReScene.SRS;

namespace ReScene.Manager.Tests;

/// <summary>
/// Headless render tests for the ported <see cref="CreatorView"/> (SRR Creator tab — the first view
/// to host an Avalonia <c>DataGrid</c>). The central gate is <b>zero binding errors</b> (via
/// <see cref="BindingErrorSink"/>), plus: the Stored Files grid is present with its two columns and
/// reflects the VM's <c>StoredFiles</c> collection (seeded rows appear), and the key inputs are
/// two-way bound (the Input TextBox mirrors the VM; toggling an option CheckBox writes back to it).
/// The creation pipeline and file dialogs are inert fakes — only the view wiring is exercised.
/// </summary>
public class CreatorViewTests
{
    // ── Inert service doubles (the view test never runs a creation) ──

    private sealed class InertSrrCreationService : ISRRCreationService
    {
        public event EventHandler<SRRCreationProgressEventArgs>? Progress { add { } remove { } }

        public Task<SRRCreationResult> CreateFromRARAsync(string outputPath, IReadOnlyList<string> rarVolumePaths,
            IReadOnlyList<StoredFileEntry>? storedFiles, SRRCreationOptions options, CancellationToken ct)
            => Task.FromResult(new SRRCreationResult { Success = true });

        public Task<SRRCreationResult> CreateFromSFVAsync(string outputPath, string sfvFilePath,
            IReadOnlyList<StoredFileEntry>? additionalFiles, SRRCreationOptions options, CancellationToken ct)
            => Task.FromResult(new SRRCreationResult { Success = true });

        public Task<SRRCreationResult> CreateFromInputsAsync(string outputPath, IReadOnlyList<string> inputFiles,
            string? rootFolder, bool storeRelativePaths, IReadOnlyList<StoredFileEntry>? additionalFiles,
            SRRCreationOptions options, CancellationToken ct)
            => Task.FromResult(new SRRCreationResult { Success = true });
    }

    private sealed class InertReleaseScanner : IReleaseScanner
    {
        public ReleaseScanResult Scan(string releaseRoot, CancellationToken ct = default) => new([], [], [], [], [], []);
    }

    private sealed class InertSrsCreationService : ISRSCreationService
    {
        public event EventHandler<SRSCreationProgressEventArgs>? Progress { add { } remove { } }
        public event EventHandler<SRSScanProgressEventArgs>? ScanProgress { add { } remove { } }

        public Task<SRSCreationResult> CreateAsync(string outputPath, string sampleFilePath, SRSCreationOptions options, CancellationToken ct)
            => Task.FromResult(new SRSCreationResult { Success = true });
    }

    private sealed class InertTempDirectoryService : ITempDirectoryService
    {
        public string CreateTempDirectory() => Path.GetTempPath();
        public void Cleanup(string? tempDir) { }
    }

    private sealed class DefaultAppSettingsService : IAppSettingsService
    {
        public event EventHandler? Changed { add { } remove { } }
        public AppSettings Load() => new();
        public void Save(AppSettings settings) { }
    }

    private sealed class InlineUiDispatcher : IUiDispatcher
    {
        public void Invoke(Action action) => action();
        public void Post(Action action) => action();
        public void Post(Action action, UiDispatcherPriority priority) => action();
        public bool CheckAccess() => true;
    }

    private static CreatorViewModel CreateViewModel() =>
        new(
            new InertSrrCreationService(),
            new InertSrsCreationService(),
            new AvaloniaFileDialogService(static () => null), // headless: dialogs no-op, never block
            new InertTempDirectoryService(),
            new DefaultAppSettingsService(),
            new InlineUiDispatcher(),
            new InertReleaseScanner());

    private static CreatorViewModel.StoredFileItem Item(string fullPath, string storedName) =>
        new() { FullPath = fullPath, StoredName = storedName };

    [AvaloniaFact]
    public void SeededStoredFiles_RenderInTwoColumnGrid_NoBindingErrors()
    {
        CreatorViewModel vm = CreateViewModel();
        vm.StoredFiles.Add(Item(@"C:\rel\release-group.nfo", "release-group.nfo"));
        vm.StoredFiles.Add(Item(@"C:\rel\movie.sfv", "movie.sfv"));

        using var sink = new BindingErrorSink();
        var window = new Window { Width = 1000, Height = 800, Content = new CreatorView { DataContext = vm } };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        DataGrid grid = window.GetVisualDescendants().OfType<DataGrid>().Single(g => g.Name == "StoredFilesGrid");

        // Two explicit columns: read-only "File Path" and editable "Stored As".
        Assert.Equal(2, grid.Columns.Count);
        Assert.Equal("File Path", grid.Columns[0].Header);
        Assert.Equal("Stored As", grid.Columns[1].Header);
        Assert.True(grid.Columns[0].IsReadOnly);
        Assert.False(grid.Columns[1].IsReadOnly);

        // The grid reflects the VM's StoredFiles collection, and its rows are realized.
        Assert.Same(vm.StoredFiles, grid.ItemsSource);
        int rows = window.GetVisualDescendants().OfType<DataGridRow>().Count();
        Assert.Equal(vm.StoredFiles.Count, rows);

        Assert.Empty(sink.Messages);
    }

    [AvaloniaFact]
    public void KeyInputs_AreTwoWayBound_NoBindingErrors()
    {
        CreatorViewModel vm = CreateViewModel();

        using var sink = new BindingErrorSink();
        var window = new Window { Width = 1000, Height = 800, Content = new CreatorView { DataContext = vm } };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        // VM -> view: the Input TextBox mirrors InputPath.
        TextBox input = window.GetVisualDescendants().OfType<TextBox>().Single(t => t.Name == "InputTextBox");
        vm.InputPath = @"C:\rel\movie.sfv";
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(@"C:\rel\movie.sfv", input.Text);

        // view -> VM: toggling the "Auto-include files" option CheckBox writes back to the VM.
        CheckBox autoInclude = window.GetVisualDescendants().OfType<CheckBox>()
            .Single(c => c.Content is string s && s.StartsWith("Auto-include files", StringComparison.Ordinal));
        Assert.True(vm.AutoIncludeFiles);          // property-initializer default
        Assert.True(autoInclude.IsChecked);        // bound to it

        autoInclude.IsChecked = false;
        Dispatcher.UIThread.RunJobs();
        Assert.False(vm.AutoIncludeFiles);

        Assert.Empty(sink.Messages);
    }

    [Fact]
    public void DedupGuard_Building_Blocks_AreWiredToTheViewModel()
    {
        // The editable-column code-behind rejects a rename onto an existing name via
        // IsStoredNameTaken and reports it through WarnDuplicateStoredName. Exercise those seams
        // directly (the grid inline-edit itself is covered by the launch-smoke).
        CreatorViewModel vm = CreateViewModel();
        vm.StoredFiles.Add(Item(@"X:\a\dup.nfo", "dup.nfo"));
        vm.StoredFiles.Add(Item(@"Y:\b\other.nfo", "other.nfo"));

        // A different row already uses "dup.nfo" (slash-insensitive); renaming onto it collides.
        Assert.True(vm.IsStoredNameTaken(@"dup.nfo", except: vm.StoredFiles[1]));
        Assert.False(vm.IsStoredNameTaken("unique.nfo", except: vm.StoredFiles[1]));

        // The warning is routed through the (headless, no-op) dialog service without throwing.
        vm.WarnDuplicateStoredName("dup.nfo");
    }
}
