using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using ReScene.App.Core.Models;
using ReScene.App.Core.Services;
using ReScene.App.Core.ViewModels;
using ReScene.App.Core.ViewModels.Wizards;
using ReScene.Manager.Views.Wizards;
using ReScene.SRR;
using ReScene.SRS;

namespace ReScene.Manager.Tests;

/// <summary>
/// Folder-input chrome on the Beginner "Create an SRR" wizard step 0
/// (<see cref="CreateSRRWizardBody"/>): the new "Browse folder…" button wired to
/// <c>BrowseInputFolderCommand</c>, the detected-sets <see cref="ItemsControl"/> bound to
/// <c>DetectedSets</c>/<c>RelativeName</c>, and accessible-name coverage (accessible name on
/// the input TextBox via <c>LabeledBy</c>, Label-in-Name on the folder button). Headless binding
/// assertions only — runtime screen-reader announcement is out of scope here.
/// </summary>
public class CreateSRRWizardBodyBindingTests
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

    private sealed class RecordingFileDialogService : IFileDialogService
    {
        public int OpenFolderCalls { get; private set; }
        public int SaveFileCalls { get; private set; }

        public Task<string?> OpenFileAsync(string title, IReadOnlyList<string> filters, string? initialPath = null) => Task.FromResult<string?>(null);
        public Task<IReadOnlyList<string>> OpenFilesAsync(string title, IReadOnlyList<string> filters, string? initialPath = null) => Task.FromResult<IReadOnlyList<string>>([]);
        public Task<string?> SaveFileAsync(string title, string defaultExtension, IReadOnlyList<string> filters, string? defaultFileName = null) { SaveFileCalls++; return Task.FromResult<string?>(null); }
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

    private static WizardViewModel CreateWizard(CreatorViewModel content) =>
        new("Create an SRR", content,
        [
            new WizardStep { Title = "Release" },
            new WizardStep { Title = "Samples & subtitles" },
            new WizardStep { Title = "Stored files" },
            new WizardStep { Title = "Save as" },
            new WizardStep { Title = "Create" },
        ]);

    // Mirror how WizardWindow wires them: the Window's DataContext is the WizardViewModel; the body's
    // DataContext is the task VM (its Content). Step 0 (the release input) is visible by default.
    private static Window Show(CreatorViewModel vm)
    {
        WizardViewModel wizard = CreateWizard(vm);
        var body = new CreateSRRWizardBody { DataContext = wizard.Content };
        var window = new Window { Width = 900, Height = 700, DataContext = wizard, Content = body };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return window;
    }

    private static Button FolderBrowseButton(Window window) =>
        window.GetVisualDescendants().OfType<Button>()
            .Single(b => b.Content is string s && s.StartsWith("Browse folder", StringComparison.Ordinal));

    [AvaloniaFact]
    public void FolderBrowseButton_BindsAndExecutes_BrowseInputFolderCommand()
    {
        var dialog = new RecordingFileDialogService();
        CreatorViewModel vm = CreateViewModel(dialog);

        using var sink = new BindingErrorSink();
        Window window = Show(vm);

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
        Window window = Show(vm);

        Button folder = FolderBrowseButton(window);
        Assert.Equal("Browse folder for release input", AutomationProperties.GetName(folder));
        Assert.Contains("Browse folder", AutomationProperties.GetName(folder), StringComparison.Ordinal);
    }

    [AvaloniaFact]
    public void InputTextBox_ExposesNonEmptyAccessibleName_ViaNameOrLabeledBy()
    {
        CreatorViewModel vm = CreateViewModel(new RecordingFileDialogService());
        Window window = Show(vm);

        // The step-0 input is the TextBox bound to InputPath.
        TextBox input = window.GetVisualDescendants().OfType<TextBox>().Single(t => t.Name == "WizInputTextBox");

        string? name = AutomationProperties.GetName(input);
        Control? labeledBy = AutomationProperties.GetLabeledBy(input);
        Assert.True(!string.IsNullOrEmpty(name) || labeledBy is not null,
            "Wizard input TextBox must expose an accessible name via AutomationProperties.Name or LabeledBy.");
    }

    [AvaloniaFact]
    public void DetectedSetsList_BindsRelativeName_WhenSetsPresent()
    {
        CreatorViewModel vm = CreateViewModel(new RecordingFileDialogService());
        vm.DetectedSets.Add(new ReleaseSetInput(@"C:\rel\CD2\b.sfv", "CD2/b.sfv"));

        using var sink = new BindingErrorSink();
        Window window = Show(vm);

        ItemsControl list = window.GetVisualDescendants().OfType<ItemsControl>()
            .Single(ic => ReferenceEquals(ic.ItemsSource, vm.DetectedSets));

        TextBlock item = window.GetVisualDescendants().OfType<TextBlock>()
            .Single(t => t.Text == "CD2/b.sfv");
        Assert.NotNull(item);

        Assert.Empty(sink.Messages);
    }

    [AvaloniaFact]
    public void InputHeader_MentionsFolder_SinceFolderInputIsAccepted()
    {
        // Step 0 accepts a release .sfv/first .rar OR a folder (the "Browse folder…" path). WizInputHeader
        // doubles as the input field's accessible name (AutomationProperties.LabeledBy), so it must reflect
        // the folder option — regression guard for the old ".sfv or first .rar"-only label. Asserts intent
        // (mentions a folder, still names the .rar), not exact wording.
        CreatorViewModel vm = CreateViewModel(new RecordingFileDialogService());
        Window window = Show(vm);

        TextBlock header = window.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Name == "WizInputHeader");
        Assert.Contains("folder", header.Text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(".rar", header.Text, StringComparison.Ordinal);
    }

    [AvaloniaFact]
    public void SaveLogButton_OnCreateStep_BindsAndInvokesSaveLog()
    {
        // The user needs to save the creation output "in case of problems". Step 4's log header carries a
        // "Save log..." button (mirroring the sibling operation views) wired to SaveLogCommand. It lives in
        // the visual tree even while step 0 is the visible step — Avalonia keeps IsVisible=false subtrees —
        // so we assert it from the default render without switching steps.
        var dialog = new RecordingFileDialogService();
        CreatorViewModel vm = CreateViewModel(dialog);

        using var sink = new BindingErrorSink();
        Window window = Show(vm);

        Button saveLog = window.GetVisualDescendants().OfType<Button>()
            .Single(b => b.Content is string s && s.StartsWith("Save log", StringComparison.Ordinal));
        Assert.Same(vm.SaveLogCommand, saveLog.Command);

        // With a non-empty log, executing routes to the save dialog (SaveLogToFileAsync no-ops on empty).
        vm.LogEntries.Add("Created SRR.");
        saveLog.Command!.Execute(null);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(1, dialog.SaveFileCalls);

        Assert.Empty(sink.Messages);
    }
}
