using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using ReScene.App.Core.Models;
using ReScene.App.Core.Services;
using ReScene.App.Core.ViewModels;
using ReScene.Manager.Views;
using ReScene.SRR;
using ReScene.SRS;

namespace ReScene.Manager.Tests;

/// <summary>
/// Folder-input chrome on the SRR Creator tab (<see cref="CreatorView"/>): the new
/// "Browse folder…" button wired to <c>BrowseInputFolderCommand</c>, the detected-sets
/// <see cref="ItemsControl"/> bound to <c>DetectedSets</c>/<c>RelativeName</c>, and
/// accessible-name coverage (accessible name on the input TextBox, Label-in-Name on the folder
/// button). Headless binding assertions only — runtime screen-reader announcement is out of scope here.
/// </summary>
public class CreatorViewFolderBindingTests
{
    // ── Inert / recording service doubles ──

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

    /// <summary>Records whether the folder picker (as opposed to the file picker) was invoked;
    /// returns "cancelled" so no scan is triggered.</summary>
    private sealed class RecordingFileDialogService : IFileDialogService
    {
        public int OpenFolderCalls { get; private set; }

        public Task<string?> OpenFileAsync(string title, IReadOnlyList<string> filters, string? initialPath = null) => Task.FromResult<string?>(null);
        public Task<IReadOnlyList<string>> OpenFilesAsync(string title, IReadOnlyList<string> filters, string? initialPath = null) => Task.FromResult<IReadOnlyList<string>>([]);
        public Task<string?> SaveFileAsync(string title, string defaultExtension, IReadOnlyList<string> filters, string? defaultFileName = null) => Task.FromResult<string?>(null);
        public Task<string?> OpenFolderAsync(string title, string? initialPath = null) { OpenFolderCalls++; return Task.FromResult<string?>(null); }
        public Task<bool> ShowConfirmAsync(string title, string message) => Task.FromResult(false);
        public Task<string?> PromptForTextAsync(string title, string message, string initialValue) => Task.FromResult<string?>(null);
        public void ShowError(string title, string message) { }
        public void ShowWarning(string title, string message) { }
        public void ShowInfo(string title, string message) { }
        public bool Confirm(string title, string message) => false;
    }

    private static CreatorViewModel CreateViewModel(IFileDialogService dialog) =>
        new(
            new InertSrrCreationService(),
            new InertSrsCreationService(),
            dialog,
            new InertTempDirectoryService(),
            new DefaultAppSettingsService(),
            new InlineUiDispatcher(),
            new InertReleaseScanner());

    private static Button FolderBrowseButton(Window window) =>
        window.GetVisualDescendants().OfType<Button>()
            .Single(b => b.Content is string s && s.StartsWith("Browse folder", StringComparison.Ordinal));

    [AvaloniaFact]
    public void FolderBrowseButton_BindsAndExecutes_BrowseInputFolderCommand()
    {
        var dialog = new RecordingFileDialogService();
        CreatorViewModel vm = CreateViewModel(dialog);

        using var sink = new BindingErrorSink();
        var window = new Window { Width = 1000, Height = 800, Content = new CreatorView { DataContext = vm } };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        Button folder = FolderBrowseButton(window);
        Assert.Same(vm.BrowseInputFolderCommand, folder.Command);

        folder.Command!.Execute(null);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(1, dialog.OpenFolderCalls);

        Assert.Empty(sink.Messages);
    }

    [AvaloniaFact]
    public void FolderBrowseButton_HasLabelInName_AccessibleName()
    {
        CreatorViewModel vm = CreateViewModel(new RecordingFileDialogService());

        var window = new Window { Width = 1000, Height = 800, Content = new CreatorView { DataContext = vm } };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        Button folder = FolderBrowseButton(window);
        Assert.Equal("Browse folder for release input", AutomationProperties.GetName(folder));
        // Label-in-Name (WCAG 2.5.3): the accessible name contains the visible "Browse folder".
        Assert.Contains("Browse folder", AutomationProperties.GetName(folder), StringComparison.Ordinal);
    }

    [AvaloniaFact]
    public void InputTextBox_ExposesNonEmptyAccessibleName()
    {
        CreatorViewModel vm = CreateViewModel(new RecordingFileDialogService());

        var window = new Window { Width = 1000, Height = 800, Content = new CreatorView { DataContext = vm } };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        TextBox input = window.GetVisualDescendants().OfType<TextBox>().Single(t => t.Name == "InputTextBox");

        string? name = AutomationProperties.GetName(input);
        Control? labeledBy = AutomationProperties.GetLabeledBy(input);
        Assert.True(!string.IsNullOrEmpty(name) || labeledBy is not null,
            "Input TextBox must expose an accessible name via AutomationProperties.Name or LabeledBy.");
    }

    [AvaloniaFact]
    public void DetectedSetsList_BindsRelativeName_WhenSetsPresent()
    {
        CreatorViewModel vm = CreateViewModel(new RecordingFileDialogService());
        vm.DetectedSets.Add(new ReleaseSetInput(@"C:\rel\CD1\a.sfv", "CD1/a.sfv"));

        using var sink = new BindingErrorSink();
        var window = new Window { Width = 1000, Height = 800, Content = new CreatorView { DataContext = vm } };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        ItemsControl list = window.GetVisualDescendants().OfType<ItemsControl>()
            .Single(ic => ReferenceEquals(ic.ItemsSource, vm.DetectedSets));

        // The item template surfaces each set's RelativeName.
        TextBlock item = window.GetVisualDescendants().OfType<TextBlock>()
            .Single(t => t.Text == "CD1/a.sfv");
        Assert.NotNull(item);

        Assert.Empty(sink.Messages);
    }
}
