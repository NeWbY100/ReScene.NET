using System.Text.RegularExpressions;

namespace ReScene.Manager.Tests;

/// <summary>
/// The app-wide <c>IsVisible</c> census: every element whose visibility is data-bound is either an
/// OUTCOME — something whose appearance is the news, which a screen-reader user must be told about —
/// or it is not, and this file records which, one entry per binding, judged by hand.
/// <para>
/// The defect it guards against has now been found four times: an element that toggles
/// <c>IsVisible</c> is not realized when its text arrives, so an assistive technology has no
/// transition to notice and the outcome is delivered silently. Fixing an instance is easy. Knowing
/// the instances is the hard part, and three separate sweeps for them were wrong because each
/// defined its population as "the property names I already know about" — the last one, in report
/// §I3, grepped three literal names and declared the class closed with three instances still open.
/// </para>
/// <para>
/// So the population here is not a name list. It is every <c>IsVisible="{Binding …}"</c> in the
/// application's XAML, read from the SOURCE at test time rather than from a hosted view. That
/// choice is what closes the two holes its sibling censuses disclose: a brand-new view is included
/// the moment it exists, and no element can hide by having an unusual shape, because nothing is
/// being pattern-matched in a visual tree.
/// </para>
/// <para>
/// WHAT THIS DOES NOT CATCH, stated because overclaiming a guard's reach is the same error the guard
/// exists to prevent. It reads XAML as TEXT: an element whose visibility is bound in C#, driven by a
/// style Setter, or expressed with an element-syntax <c>&lt;IsVisible&gt;</c> child is invisible to
/// it. It checks that an outcome HAS an announcement counterpart, not that the counterpart actually
/// fires — <c>FieldStatusLine</c> below is a live line inside its own toggled container, which this
/// census passes and which nobody has measured end to end. And the classifications are judgments,
/// not measurements; the reasons are recorded so a future reader can disagree with a specific one
/// rather than having to redo all of them.
/// </para>
/// </summary>
public class IsVisibleCensusTests
{
    private enum Kind
    {
        /// <summary>Visibility is structure, state, or availability — no outcome is being delivered.</summary>
        NotAnOutcome,

        /// <summary>An outcome, announced by an always-in-tree live region.</summary>
        AnnouncedByLiveRegion,

        /// <summary>An outcome, announced by some other measured mechanism (named in the evidence).</summary>
        AnnouncedOtherwise,
    }

    private sealed record Entry(string File, string Expression, Kind Kind, string Evidence);

    /// <summary>
    /// THE HAND-JUDGMENT, executed once and held by a test. One entry per distinct binding
    /// expression per file; the <c>Evidence</c> is why, in each case, a screen-reader user is or is
    /// not left uninformed.
    /// </summary>
    private static readonly Entry[] Classified =
    [
        // ── Wizard step gating and shell structure ───────────────────────────────────────────
        new("CreateSRRWizardBody.axaml", StepGate, Kind.NotAnOutcome, StepGateReason),
        new("CreateSRSWizardBody.axaml", StepGate, Kind.NotAnOutcome, StepGateReason),
        new("EditSRRWizardBody.axaml", StepGate, Kind.NotAnOutcome, StepGateReason),
        new("ReconstructWizardBody.axaml", StepGate, Kind.NotAnOutcome, StepGateReason),
        new("RestoreWizardBody.axaml", StepGate, Kind.NotAnOutcome, StepGateReason),
        new("WizardWindow.axaml", "IsLastStep", Kind.NotAnOutcome, "navigation button availability"),
        new("WizardWindow.axaml", "IsLastStep, Converter={StaticResource InverseBoolConverter",
            Kind.NotAnOutcome, "navigation button availability"),
        new("WizardWindow.axaml", "IsBackVisible", Kind.NotAnOutcome, "navigation button availability"),
        new("MainWindow.axaml", "IsBeginnerMode", Kind.NotAnOutcome, "mode switch between two shells"),
        new("MainWindow.axaml", "IsAdvancedMode", Kind.NotAnOutcome, "mode switch between two shells"),
        new("MainWindow.axaml", "IsBusy", Kind.NotAnOutcome, "busy overlay; the progress text carries the detail"),

        // ── Busy and progress state ──────────────────────────────────────────────────────────
        new("CreatorView.axaml", "IsCreating", Kind.NotAnOutcome, BusyReason),
        new("CreatorView.axaml", "IsScanning", Kind.NotAnOutcome, BusyReason),
        new("CreatorView.axaml", "ShowProgress", Kind.NotAnOutcome, ProgressReason),
        new("CreateSRRWizardBody.axaml", "IsCreating", Kind.NotAnOutcome, BusyReason),
        new("CreateSRRWizardBody.axaml", "IsScanning", Kind.NotAnOutcome, BusyReason),
        new("CreateSRRWizardBody.axaml", "ShowProgress", Kind.NotAnOutcome, ProgressReason),
        new("CreateSRSWizardBody.axaml", "ShowProgress", Kind.NotAnOutcome, ProgressReason),
        new("SRSCreatorView.axaml", "IsCreating", Kind.NotAnOutcome, BusyReason),
        new("SRSCreatorView.axaml", "ShowProgress", Kind.NotAnOutcome, ProgressReason),
        new("SampleRestorerView.axaml", "IsRestoring", Kind.NotAnOutcome, BusyReason),
        new("SampleRestorerView.axaml", "ShowProgress", Kind.NotAnOutcome, ProgressReason),
        new("ReconstructWizardBody.axaml", "IsRunning", Kind.NotAnOutcome, BusyReason),
        new("ReconstructWizardBody.axaml", "ShowProgress", Kind.NotAnOutcome, ProgressReason),
        new("RestoreWizardBody.axaml", "BulkRestorer.IsRestoring", Kind.NotAnOutcome,
            "busy state: shows the Cancel button"),
        new("FileCompareView.axaml", "IsComparing", Kind.NotAnOutcome, BusyReason),

        // ── Sub-flow routing and view-mode switching ─────────────────────────────────────────
        new("RestoreWizardBody.axaml", "IsBulk", Kind.NotAnOutcome, SubFlowReason),
        new("RestoreWizardBody.axaml", "IsSingle", Kind.NotAnOutcome, SubFlowReason),
        new("SRSCreatorView.axaml", "ShowISOSelection", Kind.NotAnOutcome, IsoReason),
        new("CreateSRSWizardBody.axaml", "ShowISOSelection", Kind.NotAnOutcome, IsoReason),
        new("InspectorView.axaml", "IsTextViewActive", Kind.NotAnOutcome, ViewModeReason),
        new("InspectorView.axaml", "IsHexViewActive", Kind.NotAnOutcome, ViewModeReason),
        new("InspectorView.axaml", "IsHexSearchVisible", Kind.NotAnOutcome,
            "a search panel the user opened; the code-behind moves focus into its box"),

        // ── Content presence and per-item capability ─────────────────────────────────────────
        new("InspectorView.axaml", "IsSRRLoaded", Kind.NotAnOutcome, PresenceReason),
        new("InspectorView.axaml", "IsStoredFileSelected", Kind.NotAnOutcome,
            "selection-dependent controls"),
        new("InspectorView.axaml", "IsImagePreviewAvailable", Kind.NotAnOutcome,
            "capability of the selected item"),
        new("InspectorView.axaml", "HasProperties, Converter={StaticResource InverseBoolConverter",
            Kind.NotAnOutcome, "empty-state placeholder, met in document order"),
        new("FilePreviewWindow.axaml", "HasImageTab", Kind.NotAnOutcome, "capability of the previewed file"),
        new("HomeView.axaml", "HasRecentFiles", Kind.NotAnOutcome, PresenceReason),
        new("CreatorView.axaml", "HasDetectedSets", Kind.NotAnOutcome, PresenceReason),
        new("CreateSRRWizardBody.axaml", "HasDetectedSets", Kind.NotAnOutcome, PresenceReason),
        new("ReconstructWizardBody.axaml", "HasImportedSRR", Kind.NotAnOutcome,
            "a details panel; the import's own outcome rides CustomPackerStatus"),

        // ── Standing notes and hints: met in reading order, not the result of a command ───────
        new("InspectorView.axaml", "TextViewTruncated", Kind.NotAnOutcome, TruncationReason),
        new("FilePreviewWindow.axaml", "TextViewTruncated", Kind.NotAnOutcome, TruncationReason),
        new("ReconstructorView.axaml", "ShowNoVersionsHint", Kind.NotAnOutcome,
            "a standing hint beside the field it concerns, not a result"),

        // ── Actions that appear on success ───────────────────────────────────────────────────
        new("BruteForceProgressWindow.axaml", "LastRunSucceeded", Kind.NotAnOutcome, SuccessActionReason),
        new("ReconstructWizardBody.axaml", "LastRunSucceeded", Kind.NotAnOutcome, SuccessActionReason),

        // ── OUTCOMES: the appearance IS the news ─────────────────────────────────────────────
        new("ReconstructorView.axaml", "HasCustomPackerWarning", Kind.AnnouncedByLiveRegion, "CustomPackerStatus"),
        new("ReconstructWizardBody.axaml", "HasCustomPackerWarning", Kind.AnnouncedByLiveRegion, "CustomPackerStatus"),
        new("SRSReconstructorView.axaml", "ShowResult", Kind.AnnouncedByLiveRegion, "ResultStatus"),
        new("RestoreWizardBody.axaml", "SingleRebuilder.ShowResult", Kind.AnnouncedByLiveRegion, "ResultStatus"),
        new("InspectorView.axaml", "HasWarning", Kind.AnnouncedByLiveRegion, "WarningStatus"),
        new("InspectorView.axaml", "IsVerifyResultVisible", Kind.AnnouncedByLiveRegion, "VerifyStatus"),
        // FieldStatusLine USED to appear here, gated on "state is not None" and classified as
        // announced because the message inside it carries LiveSetting=Polite. That classification
        // was wrong, and this census passed it: a live region inside a visibility-gated container
        // has no automation node while the container is hidden, so it cannot announce the first
        // status a field produces. The gate is gone — the control renders nothing when idle instead
        // of hiding — so there is no entry to make. See FieldStatusAnnouncementTests.

        new("ReconstructorView.axaml", "PathsNeedAttention", Kind.AnnouncedOtherwise,
            "a warning glyph in a TabItem header: the tab's own accessible name becomes " +
            "\"Paths — needs attention\" (PathsTabAccessibleName), and the glyph is pruned from the control view"),
    ];

    private const string StepGate =
        "$parent[Window].DataContext.CurrentStepIndex, Converter={StaticResource IndexEqualsConverter";

    private const string StepGateReason = "step gating: the container for a whole step, not a result";
    private const string BusyReason = "busy state";
    private const string ProgressReason = "a progress panel; the progress TEXT is the announcement surface";
    private const string SubFlowReason = "sub-flow gating, routed from the chosen file";
    private const string IsoReason = "reveals a picker when the chosen source is an ISO";
    private const string ViewModeReason = "view-mode switch, driven by the user's own tab choice";
    private const string PresenceReason = "content presence";
    private const string TruncationReason = "a note attached to the text view, met in reading order";

    private const string SuccessActionReason =
        "an action button appearing on success; the outcome itself is reported by the run's own result text";

    /// <summary>
    /// Every occurrence, counted rather than assumed. A binding expression may legitimately repeat
    /// within a file (five <c>IsStoredFileSelected</c> controls, eighteen step gates); the guard is
    /// on the TOTAL, so a new one anywhere moves this number and fails until it is judged.
    /// </summary>
    private const int ExpectedOccurrences = 83;

    private static readonly Regex IsVisibleBinding =
        new(@"IsVisible=""\{Binding (?<expr>[^}""]*)", RegexOptions.Compiled);

    private static readonly Regex Whitespace = new(@"\s+", RegexOptions.Compiled);

    [Fact]
    public void EveryDataBoundVisibility_IsClassified_AndEveryOutcomeHasAnAnnouncement()
    {
        var found = new List<(string File, string Expression)>();
        var liveLineFiles = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (string path in Directory.EnumerateFiles(AppXamlRoot(), "*.axaml", SearchOption.AllDirectories))
        {
            string text = File.ReadAllText(path);
            string name = Path.GetFileName(path);

            liveLineFiles[name] = Regex.Count(text, @"AutomationProperties\.LiveSetting=""");
            foreach (Match m in IsVisibleBinding.Matches(text))
            {
                // Bindings wrap across lines in this codebase, so the raw capture carries the
                // indentation of the continuation. Normalized to one space so a purely cosmetic
                // reformat cannot turn into a census failure.
                found.Add((name, Whitespace.Replace(m.Groups["expr"].Value, " ").Trim()));
            }
        }

        Assert.True(found.Count == ExpectedOccurrences,
            $"the app now has {found.Count} data-bound IsVisible elements, not {ExpectedOccurrences}. Each one is " +
            "either an outcome a screen-reader user must be told about or it is not; judge the new one and record it " +
            $"in {nameof(Classified)}.");

        var table = Classified.ToDictionary(e => (e.File, e.Expression));

        List<string> unclassified =
        [
            .. found.Select(f => (f.File, f.Expression)).Distinct()
                .Where(f => !table.ContainsKey(f))
                .Select(f => $"{f.File}: IsVisible=\"{{Binding {f.Expression}}}\""),
        ];
        Assert.True(unclassified.Count == 0,
            $"{unclassified.Count} data-bound visibilities are not judged. If the element's appearance IS the news " +
            "(a result, a warning, a verdict), it needs an always-in-tree live region — an element that is not " +
            "realized when its text arrives announces nothing. Otherwise record why it is not an outcome." +
            $"{Environment.NewLine}{string.Join(Environment.NewLine, unclassified)}");

        var live = found.Select(f => (f.File, f.Expression)).ToHashSet();
        List<string> stale = [.. table.Keys.Where(k => !live.Contains(k)).Select(k => $"{k.File}: {k.Expression}")];
        Assert.True(stale.Count == 0,
            $"{stale.Count} entries describe bindings that no longer exist — delete them, so this table keeps " +
            $"reading as the app's own inventory.{Environment.NewLine}{string.Join(Environment.NewLine, stale)}");

        List<string> unannounced =
        [
            .. Classified.Where(e => e.Kind == Kind.AnnouncedByLiveRegion)
                .Where(e => liveLineFiles.GetValueOrDefault(e.File) == 0)
                .Select(e => $"{e.File} ({e.Expression}) claims the live region \"{e.Evidence}\""),
        ];
        Assert.True(unannounced.Count == 0,
            $"{unannounced.Count} outcomes are recorded as announced by a live region, but their file contains no " +
            $"LiveSetting at all.{Environment.NewLine}{string.Join(Environment.NewLine, unannounced)}");
    }

    /// <summary>
    /// Walks up from the test binaries to the XAML being described. The census reads SOURCE, which is
    /// the whole point — a hosted view can only show what someone remembered to host.
    /// </summary>
    private static string AppXamlRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            string candidate = Path.Combine(dir.FullName, "ReScene.Manager");
            if (Directory.Exists(Path.Combine(candidate, "Views")))
            { return candidate; }
            dir = dir.Parent;
        }

        throw new DirectoryNotFoundException(
            $"could not find ReScene.Manager/Views above {AppContext.BaseDirectory}");
    }
}
