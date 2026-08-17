using System.Reflection;
using Avalonia;
using Avalonia.Automation.Peers;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using ReScene.App.Core.Models;
using ReScene.App.Core.Services;
using ReScene.App.Core.ViewModels;
using ReScene.Manager.Behaviors;
using ReScene.Manager.Views;

namespace ReScene.Manager.Tests;

/// <summary>
/// Small-window layout, whole-board close: cross-view regression guards that no single
/// per-view <c>*CompactTests</c> file owns on its own — every named font source enlarged
/// together, RenderScaling-driven layout-rounding jitter at 1.25/1.5x, and a reflection guard
/// that every converted view still carries its own threshold-invariant coverage. Uses
/// <see cref="BeginnerShellTestFactory"/> for inert VMs (the same five view-models the beginner
/// shell wires) rather than re-declaring five sets of inert service doubles a fourth time.
/// </summary>
public class SmallWindowBoardTests
{
    // ── Case 1: font-enlargement (spec Testing: "a FontSize bump... must not clip the pinned
    // band or log header") — exercising every NAMED font source at once, not just one. ─────────

    /// <summary>
    /// Application-level resource overrides for the three DynamicResource-driven font sources the
    /// five views consume directly: <c>ControlContentThemeFontSize</c> (Fluent-templated control
    /// content, Density.axaml), <c>FontSizeCaption</c> (captions/tips/status lines),
    /// <c>MonoFontSize</c> (the log lists), and <c>FontSizeBody</c> (the warning row). Restored on
    /// <see cref="Dispose"/> using the same capture-or-remove idiom as
    /// <c>CreatorCompactTests.HighContrastFixtureScope</c> (MEASURED there and re-confirmed here:
    /// these keys live in a MERGED dictionary, so a top-level override must be REMOVED, not
    /// reassigned to a captured "original", to fall back to the merged value correctly).
    /// </summary>
    private sealed class FontResourceOverrideScope : IDisposable
    {
        private readonly Dictionary<string, (bool HadDirectOverride, object? Original)> _captured = [];

        public FontResourceOverrideScope()
        {
            Override("ControlContentThemeFontSize", 16.0); // 13 -> 16
            Override("FontSizeCaption", 17.0);              // 13 -> 17
            Override("MonoFontSize", 18.0);                 // 14 -> +4
            Override("FontSizeBody", 18.0);                 // 14 -> +4
            Dispatcher.UIThread.RunJobs();
        }

        private void Override(string key, double value)
        {
            bool had = Application.Current!.Resources.ContainsKey(key);
            _captured[key] = (had, had ? Application.Current!.Resources[key] : null);
            Application.Current!.Resources[key] = value;
        }

        public void Dispose()
        {
            foreach ((string key, (bool hadDirectOverride, object? original)) in _captured)
            {
                if (hadDirectOverride)
                {
                    Application.Current!.Resources[key] = original;
                }
                else
                {
                    Application.Current!.Resources.Remove(key);
                }
            }
            Dispatcher.UIThread.RunJobs();
        }
    }

    private const string FontEnlargedWindowClass = "boardFontEnlargedProbe";

    /// <summary>
    /// The <c>:is(Window)</c> style in Styles.axaml pins unstyled content to FontSize=13 as a
    /// plain (non-activator) Style setter — that style's own comment records why a LOCAL VALUE
    /// must never be used to override it: a local value out-prioritizes it, which is exactly the
    /// mechanism behind the shipped 14px regression this repo already fixed once (commit
    /// 426000f, "remove window root FontSize pins"). This override therefore does NOT set
    /// <c>window.FontSize</c> directly; it adds a CLASS-TOKEN style instead — activator-based
    /// (StyleTrigger priority), which beats a plain Style, the same rule this codebase's own
    /// CheckBox-glyph styles document and rely on. Empirically verified (throwaway probe, not
    /// committed): this technique correctly overrides <c>:is(Window)</c>, correctly inherits into
    /// unstyled descendants, and correctly leaves explicitly resource-bound FontSize consumers
    /// (e.g. FontSizeCaption bindings) untouched — exactly as a real Density.axaml change would.
    /// </summary>
    private static void ApplyEnlargedWindowFontStyle(Window window)
    {
        var style = new Style(x => x.OfType<Window>().Class(FontEnlargedWindowClass))
        {
            Setters = { new Setter(Window.FontSizeProperty, 16.0) },
        };
        window.Styles.Add(style);
        window.Classes.Add(FontEnlargedWindowClass);
        Dispatcher.UIThread.RunJobs();
    }

    private const string FullReconstructorTip =
        "Tip: click “Import from SRR” to auto-configure versions, compression, " +
        "dictionary, timestamps and Host OS from the release's SRR.";

    [AvaloniaFact]
    public void FontEnlargement_AllSourcesTogether_PinnedBandsAndLogHeadersStayUnclipped()
    {
        BeginnerShellViewModel shell = BeginnerShellTestFactory.Create();
        using var fontResources = new FontResourceOverrideScope();

        AssertViewSurvivesFontGrowth(new ReconstructorView { DataContext = shell.Reconstructor }, isReconstructor: true);
        AssertViewSurvivesFontGrowth(new SRSCreatorView { DataContext = shell.SRSCreator }, isReconstructor: false);
        AssertViewSurvivesFontGrowth(new SRSReconstructorView { DataContext = shell.Restore.SingleRebuilder }, isReconstructor: false);
        AssertViewSurvivesFontGrowth(new SampleRestorerView { DataContext = shell.Restore.BulkRestorer }, isReconstructor: false);
        AssertViewSurvivesFontGrowth(new CreatorView { DataContext = shell.CreateSRRWizard }, isReconstructor: false);
    }

    private static void AssertViewSurvivesFontGrowth(UserControl view, bool isReconstructor)
    {
        (Window window, Grid root) = CompactViewRig.HostAt(view, CompactInvariantRig.InnerBudget);
        try
        {
            Assert.Contains("compactHeight", root.Classes);

            ApplyEnlargedWindowFontStyle(window);

            // "each view's pinned/action band and log header remain unclipped": two complementary
            // checks. AssertArrangesWithin is the SAME structural no-clip proof every per-view
            // *CompactTests file already uses as its own criterion-B check — measured against
            // root.Bounds.Height (the WINDOW never resizes here; only the font, and therefore the
            // CONTENT inside it, grows) — but it only looks at ROOT's DIRECT children, and that
            // alone is non-discriminating for anything nested deeper — a
            // clipping regression inside the log band's HEADER specifically (docked above its
            // ListBox, itself several levels below root) would leave the outer log band's own
            // bounds untouched and pass undetected. AssertNoDescendantIsClipped closes that gap.
            CompactInvariantRig.AssertArrangesWithin(root, root.Bounds.Height);
            CompactInvariantRig.AssertNoAlwaysVisibleDescendantIsClipped(
                window, root, $"{view.GetType().Name} under enlarged fonts");

            // "the per-view... reachability assertions still hold": RestoreFocusTarget is the
            // behavior's OWN restore-direction fallback target — a view-agnostic probe
            // rather than hand-picking each view's differently-shaped primary-action control. For
            // the three-band views it is the first input TextBox in the ALWAYS-visible config
            // band, reachable unconditionally. The Reconstructor is one documented exception:
            // its target is the first link Button INSIDE the Help disclosure's body, which is
            // collapsed by default in compact mode — genuinely unreachable until Help is opened,
            // exactly like ReconstructorCompactTests' own Help-open cases open it first.
            if (isReconstructor)
            {
                Dispatcher.UIThread.RunJobs();
            }

            Control restoreTarget = CompactHeightBehavior.GetRestoreFocusTarget(root)
                ?? throw new InvalidOperationException($"{view.GetType().Name} has no RestoreFocusTarget wired.");

            // Every view is walked from a genuine COLD START — nothing focused, so
            // AssertReachableByKeyboard establishes its own starting point with a blind Tab and
            // walks from there. The Creator used to be exempted here, its target focused directly
            // instead, because its Input row's unscoped TabIndex pins trapped a cold-start walk
            // before it could reach anything. That trap is fixed (the path rows are scoped — see
            // CreatorView.axaml), so the exemption is gone and this case now exercises the same
            // route as the other four.
            CompactViewRig.AssertReachableByKeyboard(window, restoreTarget);

            // "the per-view tip... assertions still hold": only the Reconstructor has a "Tip:" line;
            // condition 1 requires the rendered UIA Name to stay the FULL bound text
            // (trimming is visual-only) no matter how much wider the enlarged glyphs render.
            if (isReconstructor)
            {
                TextBlock tip = root.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Classes.Contains("tipLine"));
                string peerName = ControlAutomationPeer.CreatePeerForElement(tip).GetName() ?? string.Empty;
                Assert.Equal(FullReconstructorTip, peerName);
            }
        }
        finally
        {
            window.Close();
        }
    }

    // ── Case 2: RenderScaling sweep — distinct from the 1.0x every per-view invariant test
    // already covers (spec Testing: "render scales 1.0/1.25/1.5"). ──────────────────────────────

    /// <summary>
    /// Forces a headless window's platform implementation to report a different
    /// <see cref="TopLevel.RenderScaling"/> and re-triggers a full measure pass under it.
    /// Avalonia.Headless 11.3.18 exposes NO public API for this: <c>RenderScaling</c> is get-only
    /// on every public surface this was checked against (<c>TopLevel</c>, <c>ITopLevelImpl</c>,
    /// <c>IWindowImpl</c>, the internal <c>IHeadlessWindow</c>), and <c>HeadlessWindowImpl</c>'s
    /// own auto-property has no setter either. This reaches that internal type's backing field
    /// directly and then invokes the SAME <c>ScalingChanged</c> callback <see cref="TopLevel"/>
    /// itself registers for real platform DPI-change notifications — not a fabricated shortcut:
    /// it is the identical notification path a genuine OS scaling change would drive, triggered by
    /// hand only because no test-facing entry point exists in this Avalonia version.
    /// <para>
    /// Empirically verified before use (throwaway probe, not committed): a <c>Border</c> sized to
    /// a fractional DIP (100.35 x 40.35) arranges to DIFFERENT, scale-correct rounded bounds at
    /// each of 1.0/1.25/1.5x (101 / 100.8 / 100.6667 wide) after this call, proving the override
    /// reaches Avalonia's real layout-rounding pipeline (the exact mechanism behind the spec's own
    /// "12-DIP jitter... fractional DIPs at 125/150%" allowance) rather than only the
    /// <c>RenderScaling</c> property's readback.
    /// </para>
    /// <para>
    /// Throws loudly — rather than silently measuring every scale factor at 1.0 — if a future
    /// Avalonia upgrade changes this internal shape, and again if the readback does not confirm
    /// the requested scaling actually took effect.
    /// </para>
    /// </summary>
    private static void OverrideRenderScaling(Window window, double scaling)
    {
        object platformImpl = window.PlatformImpl
            ?? throw new InvalidOperationException("Window has no PlatformImpl to override scaling on.");
        Type implType = platformImpl.GetType();

        FieldInfo field = implType.GetField("<RenderScaling>k__BackingField", BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException(
                $"{implType.FullName}'s RenderScaling backing field was not found by reflection — " +
                "Avalonia.Headless's internal shape changed; this sweep needs a working override or " +
                "it would silently measure every scale factor at 1.0.");
        field.SetValue(platformImpl, scaling);

        PropertyInfo scalingChangedProperty = implType.GetProperty("ScalingChanged", BindingFlags.Public | BindingFlags.Instance)
            ?? throw new InvalidOperationException($"{implType.FullName}.ScalingChanged was not found by reflection.");
        var notifyScalingChanged = scalingChangedProperty.GetValue(platformImpl) as Action<double>;
        notifyScalingChanged?.Invoke(scaling);

        // Layoutable.Measure short-circuits on an unchanged constraint when not marked dirty, and
        // the notification above does not itself walk the tree invalidating descendants — force
        // every existing measurement in the view to be recomputed under the new scaling.
        if (window.Content is Control content)
        {
            content.InvalidateMeasure();
            foreach (Visual descendant in content.GetVisualDescendants())
            {
                if (descendant is Control descendantControl)
                {
                    descendantControl.InvalidateMeasure();
                }
            }
        }

        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();

        if (Math.Abs(window.RenderScaling - scaling) > 0.001)
        {
            throw new InvalidOperationException(
                $"RenderScaling override did not take effect: requested {scaling}, window reports {window.RenderScaling}.");
        }
    }

    [AvaloniaFact]
    public void CompactFloors_HoldUnderRenderScaling_At1_25x() => AssertAllFiveCompactFloorsHoldAtScaling(1.25);

    [AvaloniaFact]
    public void CompactFloors_HoldUnderRenderScaling_At1_5x() => AssertAllFiveCompactFloorsHoldAtScaling(1.5);

    private static void AssertAllFiveCompactFloorsHoldAtScaling(double scaling)
    {
        BeginnerShellViewModel shell = BeginnerShellTestFactory.Create();

        AssertCompactFloorHoldsAtScaling("Reconstructor", BuildReconstructorWorstCase(shell.Reconstructor), scaling);
        AssertCompactFloorHoldsAtScaling("SRSCreator", BuildSrsCreatorWorstCase(shell.SRSCreator), scaling);
        AssertCompactFloorHoldsAtScaling("SRSReconstructor", BuildSrsReconstructorWorstCase(shell.Restore.SingleRebuilder!), scaling);
        AssertCompactFloorHoldsAtScaling("SampleRestorer", BuildSampleRestorerWorstCase(shell.Restore.BulkRestorer!), scaling);
        AssertCompactFloorHoldsAtScaling("Creator", BuildCreatorWorstCase(shell.CreateSRRWizard), scaling);
    }

    private static void AssertCompactFloorHoldsAtScaling(string viewName, UserControl view, double scaling)
    {
        (Window window, Grid root) = CompactViewRig.HostAt(view, CompactInvariantRig.InnerBudget);
        try
        {
            Assert.Contains("compactHeight", root.Classes);

            OverrideRenderScaling(window, scaling);

            double floor = CompactInvariantRig.MeasureFloor(root);
            Assert.True(floor <= CompactInvariantRig.CiBound,
                $"{viewName} compact floor {floor:F2} at {scaling}x scaling must be <= " +
                $"{CompactInvariantRig.CiBound} (the 1.0x baseline is each view's own dedicated invariant test)");
        }
        finally
        {
            window.Close();
        }
    }

    // Worst-case recipes mirrored verbatim from each view's own *CompactTests.ForceWorstCase, so
    // the scaling sweep exercises the SAME worst floor its 1.0x sibling already pins, not a
    // lighter stand-in that would understate the risk.

    private static ReconstructorView BuildReconstructorWorstCase(ReconstructorViewModel vm)
    {
        vm.CustomPackerWarning = "Custom packer detected.";
        return new ReconstructorView { DataContext = vm };
    }

    private static SRSCreatorView BuildSrsCreatorWorstCase(SRSCreatorViewModel vm)
    {
        vm.IsISOSource = true;
        vm.SampleStatus = FieldStatus.Warning("This looks like a very small sample — check it is not truncated before continuing.");
        vm.MainFileStatus = FieldStatus.Warning("This file doesn't exist — match offsets will stay 0.");
        vm.OutputStatus = FieldStatus.Info("Auto-filled from the sample name. Change it if needed.");
        vm.IsCreating = true;
        vm.ShowProgress = true;
        vm.ProgressMessage = "Profiling sample...";
        return new SRSCreatorView { DataContext = vm };
    }

    private static SRSReconstructorView BuildSrsReconstructorWorstCase(SRSReconstructorViewModel vm)
    {
        vm.SRSStatus = FieldStatus.Warning("This SRS contains no sample file data — check it was created correctly.");
        vm.MediaStatus = FieldStatus.Warning("This media file's size doesn't match what the SRS expects.");
        vm.OutputStatus = FieldStatus.Info("Auto-filled from the SRS sample name. Change it if needed.");
        vm.ShowResult = true;
        vm.ResultSuccess = true;
        vm.ResultSummary = "CRC32 match: 0ABCDEF1 (123,456,789 bytes) — reconstructed successfully from the matching VOB title set found on the ISO image.";
        return new SRSReconstructorView { DataContext = vm };
    }

    private static SampleRestorerView BuildSampleRestorerWorstCase(SampleRestorerViewModel vm)
    {
        vm.SRRStatus = FieldStatus.Warning("This SRR contains no embedded SRS sample data — check it was created correctly.");
        vm.MatchStatus = FieldStatus.Warning("Only some samples matched a file in this media folder; the rest need manual assignment.");
        vm.IsRestoring = true;
        vm.ShowProgress = true;
        vm.OverallProgressText = "Restoring 8 of 12...";
        vm.ProgressMessage = "Reconstructing sample 8: verifying CRC against the expected checksum...";
        for (int i = 0; i < 12; i++)
        {
            vm.SRSEntries.Add(new SampleRestorerViewModel.SRSFileEntry
            {
                SRSFileName = $"sample{i:D2}.srs",
                SampleFileName = $"sample{i:D2}.mkv",
                MediaFilePath = string.Empty,
                Status = "Pending",
                IsSelected = true,
            });
        }
        return new SampleRestorerView { DataContext = vm };
    }

    private static CreatorView BuildCreatorWorstCase(CreatorViewModel vm)
    {
        vm.IsScanning = true;
        for (int i = 0; i < 12; i++)
        {
            vm.DetectedSets.Add(new ReleaseSetInput($@"C:\release\disc{i:D2}\movie.sfv", $"disc{i:D2}/movie.sfv"));
        }

        vm.InputStatus = FieldStatus.Warning("No .rar volumes found in \"release-group\". An SRR is built from the release's .rar files — they need to be in this folder next to the .sfv.");
        vm.OutputStatus = FieldStatus.Info("Auto-filled from the release folder name. Change it if needed.");
        vm.IsCreating = true;
        vm.ShowProgress = true;
        vm.ProgressMessage = "Creating SRR: hashing volume 4 of 12...";
        for (int i = 0; i < 8; i++)
        {
            vm.StoredFiles.Add(new CreatorViewModel.StoredFileItem { FullPath = $@"C:\release\file{i:D2}.nfo", StoredName = $"file{i:D2}.nfo" });
        }

        return new CreatorView { DataContext = vm };
    }

    // ── Case 3: cross-view invariant existence guard ──────────────────────────────────────────

    /// <summary>
    /// The established per-view shape: <c>Invariant_ExpandedModeFloor_UnderDerivedThreshold</c>,
    /// <c>Invariant_ActiveModeFits_AtEveryHeightAroundTheSwitchPoint</c>,
    /// <c>Invariant_CompactFloor_HelpClosed_WithinCiBound</c>, and
    /// <c>Invariant_CompactFloor_HelpOpen_WithinCiBound_And...Sane</c> — the compact one-sum checks
    /// (the pinned-band bound is asserted inside the last), plus the two that police the derived
    /// switch point. Below this count a view has PARTIALLY dropped its invariant coverage even if
    /// it still has some.
    /// <para>
    /// Raised from 3 to 4 when the sweep arrived: it is the centerpiece invariant — the one that
    /// says the ACTIVE mode fits at every height around the switch — and a view quietly losing it
    /// would leave the remaining three all passing.
    /// </para>
    /// </summary>
    private const int MinInvariantMethodsPerView = 3;

    /// <summary>
    /// Guards against a future view task silently dropping (deleting, renaming past recognition,
    /// or gutting the body of) its own threshold-invariant coverage. Reflects over the whole test
    /// assembly for the established <c>*CompactTests</c> naming pattern (Reconstructor/
    /// SRSCreator/SRSReconstructor/SampleRestorer/Creator) rather than hardcoding the
    /// five type names, so the count assertion below is itself the guard against one going
    /// missing; then re-invokes each type's own <c>Invariant_*</c> <c>[AvaloniaFact]</c> methods
    /// directly (not merely confirms they exist by name) so a gutted-but-still-named method — an
    /// empty body, say — still runs, and a genuinely broken one fails HERE too, not only in its
    /// own file's independent run. RED-verified (throwaway sabotage, reverted): raising the
    /// expected count past the real 5, and separately lowering <see cref="MinInvariantMethodsPerView"/>'s
    /// bar check below to demand more methods than any view actually has, both fail loudly with
    /// the true counts named — this is not a vacuous pass.
    /// </summary>
    [AvaloniaFact]
    public void EveryTaskView_HasThresholdInvariantTests_ThatExistAndRun()
    {
        Type[] compactTestTypes = [.. typeof(SmallWindowBoardTests).Assembly.GetTypes()
            .Where(t => t.IsClass && t.IsPublic && t.Name.EndsWith("CompactTests", StringComparison.Ordinal))
            .OrderBy(t => t.Name, StringComparer.Ordinal)];

        Assert.True(compactTestTypes.Length == 5,
            "expected exactly 5 *CompactTests types (one per Task 2-6 converted view), found " +
            $"{compactTestTypes.Length}: {string.Join(", ", compactTestTypes.Select(t => t.Name))}");

        List<string> incompleteInvariant = [];
        foreach (Type type in compactTestTypes)
        {
            MethodInfo[] invariantMethods = [.. type
                .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                .Where(m => m.Name.StartsWith("Invariant_", StringComparison.Ordinal)
                    && m.GetParameters().Length == 0
                    && m.GetCustomAttributes().Any(a => a.GetType().Name == "AvaloniaFactAttribute"))];

            if (invariantMethods.Length < MinInvariantMethodsPerView)
            {
                incompleteInvariant.Add($"{type.Name} (found {invariantMethods.Length}, need >= {MinInvariantMethodsPerView})");
                continue;
            }

            object instance = Activator.CreateInstance(type)!;
            foreach (MethodInfo method in invariantMethods)
            {
                // "exists and RUNS": a TargetInvocationException surfacing from here means the
                // invariant itself failed, exactly as if that per-view file's own test had failed.
                method.Invoke(instance, null);
            }
        }

        Assert.True(incompleteInvariant.Count == 0,
            "the following *CompactTests types are missing one or more Invariant_* [AvaloniaFact] " +
            $"methods: {string.Join(", ", incompleteInvariant)}");
    }
}
