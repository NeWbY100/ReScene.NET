using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using ReScene.App.Core.Models;
using ReScene.App.Core.Services;
using ReScene.App.Core.ViewModels;
using ReScene.App.Core.ViewModels.Wizards;
using ReScene.Core.Comparison;
using ReScene.Hex;
using ReScene.Manager.Services;
using ReScene.Manager.Views;
using ReScene.Manager.Views.Wizards;
using ReScene.RAR;
using ReScene.SRR;

namespace ReScene.Manager.Tests;

/// <summary>
/// The app-wide census: EVERY button whose visible label starts with "Browse", on EVERY surface that
/// has one, must carry an accessible name that starts with "Browse".
/// <para>
/// This test exists because of a specific, twice-repeated mistake rather than as general hygiene.
/// Two rounds of naming work swept the codebase for buttons that HAD been named, counted them, and
/// concluded "one convention app-wide". Neither ever counted how many Browse buttons EXIST, so both
/// conclusions were measured against the work rather than against the problem, and both were wrong:
/// the first left 25 unnamed and the second left 16, all of them in shipping, reachable surfaces. A
/// grep in a report cannot fail a build. This can.
/// </para>
/// <para>
/// The general rule it enforces: every "all X are Y" claim needs the count of X, not just the count
/// of Y. <see cref="ExpectedBrowseButtonsPerSurface"/> is the one place that denominator now lives.
/// </para>
/// </summary>
/// <remarks>
/// <see cref="SettingsWindow"/> needs a real <see cref="AppSettingsService"/> pointed at a temp
/// folder, which is why this class joins the "AppDataConfig" collection alongside
/// <see cref="SettingsWindowTests"/> and friends — none of them may run concurrently.
/// </remarks>
[Collection("AppDataConfig")]
public class BrowseButtonCensusTests
{
    /// <summary>
    /// The denominator, per surface. A new Browse button on any surface listed here changes its
    /// count and fails until this table and the convention are both updated — which is the point:
    /// the failure arrives at the moment the button is added, not two rounds later in review.
    /// <para>
    /// Per-surface rather than one grand total on purpose. A single number would say only "39
    /// expected, 40 found"; this says which surface moved.
    /// </para>
    /// </summary>
    private static readonly (string Surface, int Expected)[] ExpectedBrowseButtonsPerSurface =
    [
        ("CreatorView", 3),
        ("ReconstructorView", 4),
        ("SampleRestorerView", 3),
        ("SRSCreatorView", 3),
        ("SRSReconstructorView", 3),
        ("InspectorView", 1),
        ("FileCompareView", 2),
        ("SettingsWindow", 3),
        ("CreateSRRWizardBody", 3),
        ("CreateSRSWizardBody", 3),
        ("ReconstructWizardBody", 4),
        ("EditSRRWizardBody", 2),
        ("RestoreWizardBody", 5),
    ];

    private const int ExpectedTotal = 39;

    /// <summary>
    /// Walks every surface, finds every Browse-labelled button, and asserts three things: that each
    /// one announces a name beginning "Browse" (the convention, and WCAG 2.5.3 Label-in-Name at the
    /// same time, since every visible label here begins with that word); that each surface holds
    /// exactly the number of them this file records; and that the grand total is
    /// <see cref="ExpectedTotal"/>.
    /// <para>
    /// WHAT THIS DOES NOT CATCH, stated because overclaiming a guard's reach is the same error the
    /// guard exists to prevent: a brand-new VIEW that adds Browse buttons and is never added to
    /// <see cref="ExpectedBrowseButtonsPerSurface"/> is invisible here — the census only walks
    /// surfaces it is told about, so the total stays 39 and this passes. It catches a new button on
    /// a KNOWN surface, a rename that breaks the convention, and a name being dropped. Closing the
    /// new-surface hole needs the list itself to be derived rather than authored, which means
    /// scanning the .axaml sources at test time and is a different kind of test.
    /// </para>
    /// </summary>
    [AvaloniaFact]
    public void EveryBrowseButtonOnEverySurface_AnnouncesAName_StartingWithBrowse()
    {
        string originalFolder = AppDataConfig.FolderName;
        string tempFolder = UseTempAppDataFolder();
        try
        {
            var counts = new Dictionary<string, int>(StringComparer.Ordinal);
            var failures = new List<string>();

            foreach ((string surface, IReadOnlyList<Button> buttons) in CollectBrowseButtons())
            {
                counts[surface] = buttons.Count;

                foreach (Button button in buttons)
                {
                    string content = (string)button.Content!;
                    string? name = AutomationProperties.GetName(button);

                    if (string.IsNullOrWhiteSpace(name))
                    {
                        failures.Add(
                            $"{surface}: a button labelled \"{content}\" has NO accessible name — a screen reader " +
                            $"announces only \"{content}\", which does not say which of the app's {ExpectedTotal} " +
                            "Browse buttons it is. Give it AutomationProperties.Name=\"Browse for <target>\", with " +
                            "the target from that row's own visible caption.");
                        continue;
                    }

                    if (!name.StartsWith("Browse", StringComparison.Ordinal))
                    {
                        failures.Add(
                            $"{surface}: the button labelled \"{content}\" announces \"{name}\", which does not begin " +
                            "with \"Browse\". WCAG 2.5.3 (Label in Name) requires the accessible name to contain the " +
                            "visible label so a speech-input user can activate what they can see.");
                    }
                }
            }

            Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));

            foreach ((string surface, int expected) in ExpectedBrowseButtonsPerSurface)
            {
                Assert.True(counts.TryGetValue(surface, out int actual),
                    $"{surface} is in the expected-counts table but the census never hosted it — the two lists have drifted apart.");
                Assert.True(expected == actual,
                    $"{surface} holds {actual} Browse buttons, not the {expected} this file records. If that is a " +
                    "deliberate addition, name the new button to the convention and update " +
                    $"{nameof(ExpectedBrowseButtonsPerSurface)} and {nameof(ExpectedTotal)} together.");
            }

            int total = counts.Values.Sum();
            Assert.True(total == ExpectedTotal,
                $"the app now has {total} Browse buttons, not {ExpectedTotal}. If a NEW SURFACE was added, add it to " +
                $"{nameof(ExpectedBrowseButtonsPerSurface)} and to CollectBrowseButtons (the census cannot see a " +
                $"surface it is not told about) and bump {nameof(ExpectedTotal)}.");
        }
        finally
        {
            AppDataConfig.FolderName = originalFolder;
            CleanUpTempAppDataFolder(tempFolder);
        }
    }

    /// <summary>
    /// Hosts each surface the way its own test class does — real ViewModels throughout, so nothing
    /// here depends on a button being reachable without its bindings.
    /// </summary>
    private static IEnumerable<(string Surface, IReadOnlyList<Button> Buttons)> CollectBrowseButtons()
    {
        BeginnerShellViewModel shell = BeginnerShellTestFactory.Create();

        yield return Host("CreatorView", new CreatorView { DataContext = shell.CreateSRRWizard });
        yield return Host("ReconstructorView", new ReconstructorView { DataContext = shell.Reconstructor });
        yield return Host("SampleRestorerView", new SampleRestorerView { DataContext = shell.Restore.BulkRestorer });
        yield return Host("SRSCreatorView", new SRSCreatorView { DataContext = shell.SRSCreator });
        yield return Host("SRSReconstructorView", new SRSReconstructorView { DataContext = shell.Restore.SingleRebuilder });
        yield return Host("InspectorView", new InspectorView { DataContext = CreateInspectorViewModel() });
        yield return Host("FileCompareView", new FileCompareView { DataContext = CreateFileCompareViewModel() });

        yield return HostWizardBody("CreateSRRWizardBody", new CreateSRRWizardBody { DataContext = shell.CreateSRRWizard }, shell.CreateSRRWizard, steps: 5);
        yield return HostWizardBody("CreateSRSWizardBody", new CreateSRSWizardBody { DataContext = shell.SRSCreator }, shell.SRSCreator, steps: 3);
        yield return HostWizardBody("ReconstructWizardBody", new ReconstructWizardBody { DataContext = shell.Reconstructor }, shell.Reconstructor, steps: 3);
        yield return HostWizardBody("EditSRRWizardBody", new EditSRRWizardBody { DataContext = shell.SRREditor }, shell.SRREditor, steps: 4);
        yield return HostWizardBody("RestoreWizardBody", new RestoreWizardBody { DataContext = shell.Restore }, shell.Restore, steps: 3);

        var settings = new SettingsWindow { DataContext = new SettingsViewModel(new AppSettingsService(), new AvaloniaFileDialogService(static () => null)) };
        settings.Show();
        Dispatcher.UIThread.RunJobs();
        try
        { yield return ("SettingsWindow", BrowseButtonsIn(settings)); }
        finally { settings.Close(); }
    }

    private static (string, IReadOnlyList<Button>) Host(string surface, Control view)
    {
        var window = new Window { Width = 1200, Height = 900, Content = view };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        try
        { return (surface, BrowseButtonsIn(window)); }
        finally { window.Close(); }
    }

    /// <summary>
    /// Hosts a wizard body and walks EVERY step, not just the selected one.
    /// <para>
    /// Stepping is load-bearing, and the census proved why on its first run: it found ZERO Browse
    /// buttons in <see cref="ReconstructWizardBody"/> where four exist. The step panels are
    /// <c>IsVisible</c>-bound, and an <c>IsVisible=false</c> <see cref="StackPanel"/> keeps its
    /// children in the visual tree while an <c>IsVisible=false</c> <see cref="ScrollViewer"/> does
    /// NOT realize its content. That body wraps its pickers in a ScrollViewer and the others do
    /// not, so a single-pass census silently under-counted exactly one surface — the same
    /// under-counting failure this whole test exists to stop, caught by the test on itself.
    /// </para>
    /// </summary>
    private static (string, IReadOnlyList<Button>) HostWizardBody(string surface, Control body, object taskVm, int steps)
    {
        var wizard = new WizardViewModel("census", taskVm,
            [.. Enumerable.Range(0, steps).Select(i => new WizardStep { Title = $"step {i}" })]);
        var window = new Window { Width = 1200, Height = 900, DataContext = wizard, Content = body };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        try
        { return (surface, BrowseButtonsIn(window, wizard, steps)); }
        finally { window.Close(); }
    }

    /// <summary>
    /// Every Browse-labelled button reachable in this window, including ones on unselected tabs.
    /// <para>
    /// The tab cycling is load-bearing rather than defensive: Avalonia does not materialize an
    /// unselected <see cref="TabItem"/>'s content, so a single pass over
    /// <see cref="SettingsWindow"/> would find only whichever tab happens to be selected and would
    /// silently under-count two of its three buttons. Each <see cref="TabControl"/> is cycled
    /// through every index and the results accumulated by reference, so a button is counted once no
    /// matter how many passes realize it.
    /// </para>
    /// </summary>
    private static IReadOnlyList<Button> BrowseButtonsIn(Window window, WizardViewModel? wizard = null, int steps = 0)
    {
        var found = new List<Button>();
        var seen = new HashSet<Button>(ReferenceEqualityComparer.Instance);

        void Sweep()
        {
            foreach (Button button in window.GetVisualDescendants().OfType<Button>())
            {
                if (button.Content is string content
                    && content.StartsWith("Browse", StringComparison.Ordinal)
                    && seen.Add(button))
                {
                    found.Add(button);
                }
            }
        }

        Sweep();

        // Wizard steps first — see HostWizardBody for why an unselected step can hide its buttons
        // entirely rather than merely not painting them.
        for (int step = 0; wizard is not null && step < steps; step++)
        {
            wizard.CurrentStepIndex = step;
            Dispatcher.UIThread.RunJobs();
            Sweep();
        }

        foreach (TabControl tabs in window.GetVisualDescendants().OfType<TabControl>().ToList())
        {
            int original = tabs.SelectedIndex;
            for (int i = 0; i < tabs.ItemCount; i++)
            {
                tabs.SelectedIndex = i;
                Dispatcher.UIThread.RunJobs();
                Sweep();
            }

            tabs.SelectedIndex = original;
            Dispatcher.UIThread.RunJobs();
        }

        return found;
    }

    // ── Inert doubles for the two surfaces the Beginner shell factory does not build ──

    private sealed class InlineUiDispatcher : IUiDispatcher
    {
        public void Invoke(Action action) => action();
        public void Post(Action action) => action();
        public void Post(Action action, UiDispatcherPriority priority) => action();
        public bool CheckAccess() => true;
    }

    private sealed class InertFileCompareService : IFileCompareService
    {
        public object? LoadFileData(string filePath) => null;
        public IReadOnlyList<RARDetailedBlock>? ParseDetailedBlocks(string filePath) => null;
        public CompareResult Compare(object? leftData, object? rightData,
            IReadOnlyList<RARDetailedBlock>? leftBlocks = null, IReadOnlyList<RARDetailedBlock>? rightBlocks = null,
            IHexDataSource? leftSource = null, IHexDataSource? rightSource = null) => new();
    }

    private sealed class InertHexDiffComputer : IHexDiffComputer
    {
        public Task<HexDiffResult> ComputeAsync(
            IHexDataSource leftSource, long leftOffset, long leftLength,
            IHexDataSource rightSource, long rightOffset, long rightLength,
            IProgress<HexDiffProgress>? progress, CancellationToken ct) => Task.FromResult(new HexDiffResult([], []));
    }

    private sealed class InertSRREditingService : ISRREditingService
    {
        public void AddStoredFiles(string srrFilePath, IReadOnlyList<(string StoredName, string FilePath)> files) { }
        public void RemoveStoredFiles(string srrFilePath, IReadOnlyList<string> storedNames) { }
        public Task RenameStoredFileAsync(string srrPath, string oldName, string newName, CancellationToken ct = default) => Task.CompletedTask;
        public Task MoveStoredFileAsync(string srrPath, string storedName, int offset, CancellationToken ct = default) => Task.CompletedTask;
        public IReadOnlyList<StoredFileInfo> GetStoredFiles(string srrFilePath) => [];
        public Task<string?> ExtractStoredFileAsync(string srrFilePath, string outputDirectory, string storedName, CancellationToken ct = default) => Task.FromResult<string?>(null);
        public Task<byte[]?> ReadStoredFileBytesAsync(string srrFilePath, string storedName, CancellationToken ct = default) => Task.FromResult<byte[]?>(null);
    }

    private sealed class InertSRRVerifyService : ISRRVerifyService
    {
        public Task<SRRVerifyResult> VerifyAsync(string srrFilePath, CancellationToken ct = default) =>
            Task.FromResult(new SRRVerifyResult { IsValid = true, Issues = [], BlocksScanned = 0, FileSize = 0 });
    }

    private sealed class InertPropertyExportService : IPropertyExportService
    {
        public Task ExportSelectedAsync(string outputPath, TreeNodeViewModel node, IEnumerable<PropertyItem> properties, CancellationToken ct = default) => Task.CompletedTask;
        public Task ExportTreeAsync(string outputPath, IEnumerable<TreeNodeViewModel> roots, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class InertImagePreviewService : IImagePreviewService
    {
        public void Preview(byte[] data, string fileName) { }
    }

    private static FileCompareViewModel CreateFileCompareViewModel() =>
        new(new InertFileCompareService(),
            new AvaloniaFileDialogService(static () => null),
            new InertHexDiffComputer(),
            new InlineUiDispatcher());

    private static InspectorViewModel CreateInspectorViewModel() =>
        new(new AvaloniaFileDialogService(static () => null),
            new InertSRREditingService(),
            new InertSRRVerifyService(),
            new InertPropertyExportService(),
            new InertImagePreviewService(),
            settingsService: null);

    private static string UseTempAppDataFolder()
    {
        string tempFolder = $"ReScene.Manager.Tests-{Guid.NewGuid():N}";
        AppDataConfig.FolderName = tempFolder;
        return tempFolder;
    }

    private static void CleanUpTempAppDataFolder(string tempFolder)
    {
        string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), tempFolder);
        if (Directory.Exists(dir))
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
