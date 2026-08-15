using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using ReScene.App.Core.Helpers;
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
/// The app-wide picker-row census: EVERY row that pairs a path field with a Browse button must tab
/// in the order it renders — the field first, then its button(s).
/// <para>
/// These rows are all right-docked <see cref="DockPanel"/>s. Docking consumes edges in declaration
/// order, so the rightmost control must be declared FIRST, which makes every such row's markup
/// order the exact REVERSE of what the user sees. Left alone, a keyboard user tabs the Browse
/// button before the field it belongs to (WCAG 2.4.3 Focus Order). The correction is explicit
/// <c>TabIndex</c> restoring visual order plus <c>KeyboardNavigation.TabNavigation="Local"</c> to
/// keep those pins from being compared against the whole window scope.
/// </para>
/// <para>
/// POPULATION comes from the same thirteen-surface list as
/// <see cref="BrowseButtonCensusTests"/> — not from whichever files a round happens to have open.
/// That rule exists because defining the population by files-in-hand produced a wrong app-wide
/// count three separate times in this workstream, missing the same three surfaces
/// (<see cref="InspectorView"/>, <see cref="FileCompareView"/>, <see cref="SettingsWindow"/>) every
/// time.
/// </para>
/// </summary>
[Collection("AppDataConfig")]
public class PickerRowOrderTests
{
    /// <summary>
    /// Rows per surface. The completeness guard: a new picker row on one of these surfaces changes
    /// its count and fails until the row is pinned and this table updated, so the fix cannot be
    /// forgotten on a surface nobody was looking at.
    /// <para>
    /// WHAT THIS DOES NOT CATCH, stated because overclaiming a guard's reach is the same error the
    /// guard exists to prevent — and this file's own class doc opens with three surfaces that were
    /// missed three times running. TWO holes, both of which leave the total at 37 and pass:
    /// </para>
    /// <para>
    /// (1) A brand-new VIEW is invisible. The census walks only the surfaces it is told about,
    /// inherited from <see cref="BrowseButtonCensusTests"/>'s list. Closing this needs the list
    /// derived rather than authored — scanning the .axaml sources at test time, a different kind of
    /// test.
    /// </para>
    /// <para>
    /// (2) A row of a different SHAPE is invisible even on a known surface. <c>Sweep</c> matches a
    /// <see cref="DockPanel"/> with a <see cref="TextBox"/> and a Browse <see cref="Button"/> among
    /// its DIRECT children. A Grid-based row, or one that nests its field inside a wrapper, has the
    /// identical backwards-tab-order defect and is never examined. The shape filter is what makes
    /// the census cheap; it is also its blind spot.
    /// </para>
    /// <para>
    /// What it does catch: a new DockPanel-shaped picker row on a known surface, a row losing its
    /// pins or its Local scoping, and any row whose Tab order stops matching the order it renders.
    /// </para>
    /// </summary>
    private static readonly (string Surface, int Expected)[] ExpectedRowsPerSurface =
    [
        ("CreatorView", 2),
        ("ReconstructorView", 4),
        ("SampleRestorerView", 3),
        ("SRSCreatorView", 3),
        ("SRSReconstructorView", 3),
        ("InspectorView", 1),
        ("FileCompareView", 2),
        ("SettingsWindow", 3),
        ("CreateSRRWizardBody", 2),
        ("CreateSRSWizardBody", 3),
        ("ReconstructWizardBody", 4),
        ("EditSRRWizardBody", 2),
        ("RestoreWizardBody", 5),
    ];

    private const int ExpectedTotal = 37;

    [AvaloniaFact]
    public void EveryPickerRow_TabsItsFieldBeforeItsBrowseButton()
    {
        string originalFolder = AppDataConfig.FolderName;
        string temp = $"ReScene.Manager.Tests-{Guid.NewGuid():N}";
        AppDataConfig.FolderName = temp;
        try
        {
            var counts = new Dictionary<string, int>(StringComparer.Ordinal);
            var failures = new List<string>();
            var unwalked = new List<string>();
            int walked = 0;

            foreach ((string surface, IReadOnlyList<RowCheck> rows) in CollectRows())
            {
                counts[surface] = rows.Count;
                int skipped = 0;
                foreach (RowCheck row in rows)
                {
                    if (row.Walked)
                    { walked++; }
                    else
                    { skipped++; }
                    if (row.Failure is { } why)
                    { failures.Add($"{surface}: {why}"); }
                }

                if (skipped > 0)
                { unwalked.Add($"{surface} ({skipped} of {rows.Count})"); }
            }

            Assert.True(failures.Count == 0,
                $"{failures.Count} of {counts.Values.Sum()} picker rows tab their Browse button before the field it " +
                $"belongs to.{Environment.NewLine}{string.Join(Environment.NewLine, failures)}");

            Assert.True(walked == ExpectedTotal,
                $"only {walked} of {ExpectedTotal} picker rows were proved by an actual Tab walk; the rest fell back to " +
                "the weaker structural check because their panel was hidden, or their controls disabled, in the state " +
                $"they were hosted in — {string.Join("; ", unwalked)}. Drive the gating property in " +
                $"{nameof(CollectRows)} (as RestoreWizardBody's Kind is driven) so the new row is walked too — a hidden " +
                "row silently passing this census is exactly how six rows were once reported fixed without being touched.");

            foreach ((string surface, int expected) in ExpectedRowsPerSurface)
            {
                Assert.True(counts.TryGetValue(surface, out int actual),
                    $"{surface} is in the expected-counts table but the census never hosted it.");
                Assert.True(expected == actual,
                    $"{surface} holds {actual} picker rows, not the {expected} recorded here. A new row must be pinned " +
                    $"to visual order and both {nameof(ExpectedRowsPerSurface)} and {nameof(ExpectedTotal)} updated.");
            }

            Assert.True(counts.Values.Sum() == ExpectedTotal,
                $"the app now has {counts.Values.Sum()} picker rows, not {ExpectedTotal}.");
        }
        finally
        {
            AppDataConfig.FolderName = originalFolder;
            string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), temp);
            if (Directory.Exists(dir))
            { Directory.Delete(dir, recursive: true); }
        }
    }

    /// <param name="Failure">Null when the row tabs in render order.</param>
    /// <param name="Walked">
    /// True when a real Tab walk proved it; false when only the structural fallback could run.
    /// </param>
    private readonly record struct RowCheck(string? Failure, bool Walked);

    /// <summary>
    /// Checks one row on both halves of the correction: its markup must carry the pins AND the Local
    /// scoping, and — where the row can be focused — a Tab walk must visit its controls in the order
    /// they RENDER (ascending X), field before button.
    /// <para>
    /// Rendered position is the oracle for the walk rather than the markup, deliberately — the markup
    /// order is the thing under suspicion, so deriving the expectation from it would assert only that
    /// the row is self-consistent.
    /// </para>
    /// <para>
    /// The scoping is asserted from markup rather than behaviour because a walk of the row alone
    /// cannot see it: pins with no Local scoping are compared against the WHOLE window, where every
    /// other control sits at <see cref="int.MaxValue"/>, so a row that happens to come first still
    /// tabs correctly — measured, on <see cref="InspectorView"/>, where deleting the scoping changed
    /// nothing this census could observe. What it silently breaks is any control added ABOVE the row
    /// later, which the pins would then jump ahead of. That consequence is proved behaviourally, once,
    /// by WizardTabOrderTests.ScopedPins_KeepAFieldAddedAboveTheRow_AheadOfIt; here the point is only
    /// that the scoping cannot be deleted app-wide without something failing.
    /// </para>
    /// </summary>
    private static RowCheck CheckRow(Window window, DockPanel row)
    {
        bool scoped = KeyboardNavigation.GetTabNavigation(row) == KeyboardNavigationMode.Local;
        bool pinned = row.Children.OfType<Control>().Any(c => c.TabIndex != int.MaxValue);
        if (!scoped || !pinned)
        {
            return new RowCheck(
                $"a picker row carries {(pinned ? "TabIndex pins but no Local scoping" : scoped ? "Local scoping but no TabIndex pins" : "neither TabIndex pins nor Local scoping")} " +
                $"({string.Join(", ", row.Children.OfType<Control>().Select(CompactViewRig.Describe))}) — it renders " +
                "field-then-button but its markup declares the button first, so it tabs backwards without both halves " +
                "of the correction.",
                Walked: false);
        }

        List<Control> visual = [.. row.Children.OfType<Control>()
            .Where(c => c.Focusable && c.IsEffectivelyVisible && c.IsEffectivelyEnabled)
            .OrderBy(c => c.TranslatePoint(new Point(0, 0), window)?.X ?? 0)];

        // A row whose panel is hidden has no focusable children, so a Tab walk cannot exercise it at
        // all. An earlier version of this census silently returned "no failure" for exactly that
        // case: it reported 27 of 37 where the markup showed 33 unpinned, because RestoreWizardBody's
        // bulk/single sub-panels are gated on IsBulk/IsSingle and both are false until a file is
        // loaded. Six rows would have been declared fixed without ever being touched.
        // The census now DRIVES that gating (see CollectRows) so all thirty-seven rows are walked,
        // and the caller asserts that count. Reaching here means only the markup could be checked,
        // which the caller reports as the weaker evidence it is.
        if (visual.Count < 2)
        { return new RowCheck(null, Walked: false); }

        visual[0].Focus();
        Dispatcher.UIThread.RunJobs();
        if (window.FocusManager?.GetFocusedElement() as Control is not { } start || !ReferenceEquals(start, visual[0]))
        {
            return new RowCheck($"could not focus the leftmost control of a row ({CompactViewRig.Describe(visual[0])})",
                Walked: false);
        }

        for (int i = 1; i < visual.Count; i++)
        {
            Control? next = CompactViewRig.StepFocus(window, forward: true);
            if (next is null || !ReferenceEquals(next, visual[i]))
            {
                return new RowCheck(
                    $"the row rendering [{string.Join(", ", visual.Select(CompactViewRig.Describe))}] tabs " +
                    $"{(next is null ? "<nothing>" : CompactViewRig.Describe(next))} at position {i} instead of " +
                    $"{CompactViewRig.Describe(visual[i])} — the Browse button is reached before the field it belongs to. " +
                    "Pin the row with TabIndex in visual order plus KeyboardNavigation.TabNavigation=\"Local\".",
                    Walked: true);
            }
        }

        return new RowCheck(null, Walked: true);
    }

    private static IEnumerable<(string Surface, IReadOnlyList<RowCheck> Rows)> CollectRows()
    {
        BeginnerShellViewModel shell = BeginnerShellTestFactory.Create();

        yield return View("CreatorView", new CreatorView { DataContext = shell.CreateSRRWizard });
        yield return View("ReconstructorView", new ReconstructorView { DataContext = shell.Reconstructor });
        yield return View("SampleRestorerView", new SampleRestorerView { DataContext = shell.Restore.BulkRestorer });
        yield return View("SRSCreatorView", new SRSCreatorView { DataContext = shell.SRSCreator });
        yield return View("SRSReconstructorView", new SRSReconstructorView { DataContext = shell.Restore.SingleRebuilder });
        yield return View("InspectorView", new InspectorView { DataContext = CreateInspectorViewModel() });
        yield return View("FileCompareView", new FileCompareView { DataContext = CreateFileCompareViewModel() });

        yield return Body("CreateSRRWizardBody", new CreateSRRWizardBody(), shell.CreateSRRWizard, 5);
        yield return Body("CreateSRSWizardBody", new CreateSRSWizardBody(), shell.SRSCreator, 3);
        yield return Body("ReconstructWizardBody", new ReconstructWizardBody(), shell.Reconstructor, 3);
        yield return Body("EditSRRWizardBody", new EditSRRWizardBody(), shell.SRREditor, 4);

        // RestoreWizardBody routes one input file into a bulk (.srr) or single (.srs) sub-flow, and
        // four of its five rows live in panels gated on IsBulk/IsSingle — mutually exclusive, so no
        // single state shows them all. Kind is driven directly rather than through InputPath because
        // setting the path also pushes it into the sub-ViewModels; Kind alone is inert.
        yield return Body("RestoreWizardBody", new RestoreWizardBody(), shell.Restore, 3,
            [() => shell.Restore.Kind = SampleRestoreKind.SRR, () => shell.Restore.Kind = SampleRestoreKind.SRS]);

        var settings = new SettingsWindow
        {
            DataContext = new SettingsViewModel(new AppSettingsService(), new AvaloniaFileDialogService(static () => null)),
        };
        settings.Show();
        Dispatcher.UIThread.RunJobs();
        try
        { yield return ("SettingsWindow", Sweep(settings, null, 1)); }
        finally { settings.Close(); }
    }

    private static (string, IReadOnlyList<RowCheck>) View(string surface, Control view)
    {
        var window = new Window { Width = 1200, Height = 900, Content = view };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        try
        { return (surface, Sweep(window, null, 1)); }
        finally { window.Close(); }
    }

    private static (string, IReadOnlyList<RowCheck>) Body(
        string surface, Control body, object taskVm, int steps, IReadOnlyList<Action>? states = null)
    {
        var wizard = new WizardViewModel(surface, taskVm,
            [.. Enumerable.Range(0, steps).Select(i => new WizardStep { Title = $"s{i}" })]);
        var window = new WizardWindow(wizard, body);
        window.Show();
        Dispatcher.UIThread.RunJobs();
        try
        { return (surface, Sweep(window, wizard, steps, states)); }
        finally { window.Close(); }
    }

    /// <summary>
    /// Finds every picker row reachable in this window and checks each once.
    /// <para>
    /// Wizard steps, TabControl tabs and caller-supplied ViewModel <paramref name="states"/> are all
    /// cycled: an unselected <see cref="TabItem"/>'s content is not materialized, an
    /// <c>IsVisible=false</c> <see cref="ScrollViewer"/> does not realize its content either, and a
    /// sub-panel gated on a ViewModel flag stays unfocusable until that flag is driven — so a single
    /// pass silently under-counts. Rows are keyed by REFERENCE — a shape-based key collapses
    /// identically-shaped rows whose fields carry no x:Name, which under-reported this very census by
    /// six rows once already — and each is re-checked until an encounter in which it can actually be
    /// walked.
    /// </para>
    /// </summary>
    private static IReadOnlyList<RowCheck> Sweep(
        Window window, WizardViewModel? wizard, int steps, IReadOnlyList<Action>? states = null)
    {
        var results = new Dictionary<DockPanel, RowCheck>(ReferenceEqualityComparer.Instance);

        void Scan()
        {
            foreach (DockPanel panel in window.GetVisualDescendants().OfType<DockPanel>().ToList())
            {
                List<Control> kids = [.. panel.Children.OfType<Control>()];
                if (!kids.OfType<TextBox>().Any())
                { continue; }
                if (!kids.OfType<Button>().Any(b => b.Content is string s && s.StartsWith("Browse", StringComparison.Ordinal)))
                { continue; }

                // A row is checked once, but the FIRST encounter is not necessarily the one that can
                // prove anything: a hidden panel keeps its children in the visual tree, so step 0
                // already discovers the rows belonging to later steps. Keeping that first result
                // would freeze all of them at the weaker structural tier — which is what happened,
                // measured: 30 of 37. Re-check until a walkable encounter is found, then stop.
                if (results.TryGetValue(panel, out RowCheck prior) && prior.Walked)
                { continue; }

                results[panel] = CheckRow(window, panel);
            }
        }

        foreach (Action enterState in states ?? [static () => { }])
        {
            enterState();
            Dispatcher.UIThread.RunJobs();

            for (int step = 0; step < Math.Max(1, steps); step++)
            {
                if (wizard is not null)
                { wizard.CurrentStepIndex = step; Dispatcher.UIThread.RunJobs(); }

                foreach (TabControl tabs in window.GetVisualDescendants().OfType<TabControl>().ToList())
                {
                    for (int t = 0; t < tabs.ItemCount; t++)
                    {
                        tabs.SelectedIndex = t;
                        Dispatcher.UIThread.RunJobs();
                        Scan();
                    }

                    tabs.SelectedIndex = 0;
                    Dispatcher.UIThread.RunJobs();
                }

                Scan();
            }
        }

        return [.. results.Values];
    }

    // ── Inert doubles for the two surfaces the Beginner shell factory does not build ──

    private sealed class InlineUiDispatcher : IUiDispatcher
    {
        public void Invoke(Action a) => a();
        public void Post(Action a) => a();
        public void Post(Action a, UiDispatcherPriority p) => a();
        public bool CheckAccess() => true;
    }

    private sealed class InertFileCompareService : IFileCompareService
    {
        public object? LoadFileData(string filePath) => null;
        public IReadOnlyList<RARDetailedBlock>? ParseDetailedBlocks(string filePath) => null;
        public CompareResult Compare(object? l, object? r, IReadOnlyList<RARDetailedBlock>? lb = null,
            IReadOnlyList<RARDetailedBlock>? rb = null, IHexDataSource? ls = null, IHexDataSource? rs = null) => new();
    }

    private sealed class InertHexDiffComputer : IHexDiffComputer
    {
        public Task<HexDiffResult> ComputeAsync(IHexDataSource ls, long lo, long ll, IHexDataSource rs, long ro, long rl,
            IProgress<HexDiffProgress>? p, CancellationToken ct) => Task.FromResult(new HexDiffResult([], []));
    }

    private sealed class InertSRREditingService : ISRREditingService
    {
        public void AddStoredFiles(string p, IReadOnlyList<(string StoredName, string FilePath)> f) { }
        public void RemoveStoredFiles(string p, IReadOnlyList<string> n) { }
        public Task RenameStoredFileAsync(string p, string o, string n, CancellationToken ct = default) => Task.CompletedTask;
        public Task MoveStoredFileAsync(string p, string n, int o, CancellationToken ct = default) => Task.CompletedTask;
        public IReadOnlyList<StoredFileInfo> GetStoredFiles(string p) => [];
        public Task<string?> ExtractStoredFileAsync(string p, string d, string n, CancellationToken ct = default) => Task.FromResult<string?>(null);
        public Task<byte[]?> ReadStoredFileBytesAsync(string p, string n, CancellationToken ct = default) => Task.FromResult<byte[]?>(null);
    }

    private sealed class InertSRRVerifyService : ISRRVerifyService
    {
        public Task<SRRVerifyResult> VerifyAsync(string p, CancellationToken ct = default) =>
            Task.FromResult(new SRRVerifyResult { IsValid = true, Issues = [], BlocksScanned = 0, FileSize = 0 });
    }

    private sealed class InertPropertyExportService : IPropertyExportService
    {
        public Task ExportSelectedAsync(string o, TreeNodeViewModel n, IEnumerable<PropertyItem> p, CancellationToken ct = default) => Task.CompletedTask;
        public Task ExportTreeAsync(string o, IEnumerable<TreeNodeViewModel> r, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class InertImagePreviewService : IImagePreviewService
    {
        public void Preview(byte[] d, string f) { }
    }

    private static FileCompareViewModel CreateFileCompareViewModel() =>
        new(new InertFileCompareService(), new AvaloniaFileDialogService(static () => null),
            new InertHexDiffComputer(), new InlineUiDispatcher());

    private static InspectorViewModel CreateInspectorViewModel() =>
        new(new AvaloniaFileDialogService(static () => null), new InertSRREditingService(),
            new InertSRRVerifyService(), new InertPropertyExportService(), new InertImagePreviewService(), settingsService: null);
}
