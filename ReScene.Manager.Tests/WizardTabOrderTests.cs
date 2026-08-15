using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using ReScene.App.Core.ViewModels;
using ReScene.App.Core.ViewModels.Wizards;
using ReScene.Manager.Views.Wizards;

namespace ReScene.Manager.Tests;

/// <summary>
/// Keyboard order for the Beginner wizards, exercised against a REAL <see cref="WizardWindow"/>
/// rather than a body hosted in isolation.
/// <para>
/// That distinction is the whole reason these tests exist here. The a11y follow-up report's
/// section 8 probed <c>CreateSRRWizardBody</c> on its own, saw a tight three-element cycle, and
/// recorded that the body-only probe UNDERSTATED what the real window does — the real cycle also
/// includes the footer. It then recorded two-part guidance as binding: scoping the body's TabIndex
/// pins alone "would place footer controls before BodyHost", so the fix had to be (a) scope the row
/// AND (b) align the host's own tree order, validated by exact real-window walks. Both halves are
/// pinned below.
/// </para>
/// </summary>
public class WizardTabOrderTests
{
    private const int MaxSteps = 30;

    private static (WizardWindow Window, WizardViewModel Wizard, CreatorViewModel Vm) ShowCreateSrrWizard()
    {
        BeginnerShellViewModel shell = BeginnerShellTestFactory.Create();
        CreatorViewModel vm = shell.CreateSRRWizard;
        var wizard = new WizardViewModel("Create SRR", vm,
            [.. Enumerable.Range(0, 5).Select(i => new WizardStep { Title = $"step {i}" })]);
        var window = new WizardWindow(wizard, new CreateSRRWizardBody());
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return (window, wizard, vm);
    }

    private static ContentControl BodyHost(Window window) =>
        window.GetVisualDescendants().OfType<ContentControl>().Single(c => c.Name == "BodyHost");

    /// <summary>The footer band — the DockPanel holding Close/Back/Next, resolved by containing the Back button.</summary>
    private static DockPanel Footer(Window window) =>
        window.GetVisualDescendants().OfType<DockPanel>()
            .Single(p => p.Children.OfType<Control>().Any(c => c is Button)
                         && p.GetVisualDescendants().OfType<Button>().Any(b => b.Content as string == "‹ Back"));

    /// <summary>Walks forward from <paramref name="start"/> until the walk repeats a control, and returns the order.</summary>
    private static List<Control> WalkFrom(Window window, Control start)
    {
        start.Focus();
        Dispatcher.UIThread.RunJobs();

        List<Control> order = [start];
        for (int i = 0; i < MaxSteps; i++)
        {
            if (CompactViewRig.StepFocus(window, forward: true) is not { } focused)
            { break; }
            if (order.Any(c => ReferenceEquals(c, focused)))
            { break; }
            order.Add(focused);
        }

        return order;
    }

    private static string Trail(IEnumerable<Control> order) =>
        string.Join(" -> ", order.Select(CompactViewRig.Describe));

    /// <summary>
    /// THE (b) HALF, exercised the way a user meets it: press Next, then press Tab. Focus must
    /// arrive in the new step's own fields, not back in the navigation footer.
    /// <para>
    /// MEASURED against the pre-fix construction: on steps 1, 2 and 3 the order read
    /// <c>Close -> ‹ Back -> Next › -> …the step's own fields</c>, so a user who pressed Next (focus
    /// on Next, the footer's last control) and then Tab wrapped to the START of the order — which
    /// was the footer again. They tabbed Close and Back a second time before reaching anything they
    /// had come to that step to fill in (WCAG 2.4.3 Focus Order). The cause was structural rather
    /// than a stray attribute: the host was a <c>DockPanel</c>, whose fill child must be declared
    /// LAST, so the body host could only ever follow the footer in tree order — and tab order
    /// follows tree order.
    /// </para>
    /// <para>
    /// The entry point chosen is the DISCRIMINATING one, and finding that out took a false start
    /// worth recording. The obvious version — focus Next, change step, press Tab — passes with the
    /// footer declared first, because Next is the footer's LAST control either way, so Tab moves
    /// into the body from there regardless. It proved nothing. What actually differs is any moment
    /// the walk RESTARTS: the user was working in the step's fields, advanced (by clicking Next, or
    /// by any route that leaves focus on a control the new step hides), and pressed Tab. Then the
    /// order begins from the top — which was the footer.
    /// </para>
    /// </summary>
    [AvaloniaFact]
    public void AdvancingFromAFieldThenTabbing_LandsInTheNewStepsFields_NotTheFooter()
    {
        (WizardWindow window, WizardViewModel wizard, CreatorViewModel vm) = ShowCreateSrrWizard();
        try
        {
            ContentControl body = BodyHost(window);
            DockPanel footer = Footer(window);

            for (int step = 1; step <= 3; step++)
            {
                wizard.CurrentStepIndex = step - 1;
                Dispatcher.UIThread.RunJobs();

                Control? fieldOnOldStep = body.GetVisualDescendants().OfType<Control>()
                    .FirstOrDefault(c => c.Focusable && c.IsEffectivelyVisible && c.IsEffectivelyEnabled);
                Assert.True(fieldOnOldStep is not null, $"step {step - 1}: expected a focusable field to work in");
                fieldOnOldStep.Focus();
                Dispatcher.UIThread.RunJobs();

                // Advance. The control that had focus belongs to the previous step and is now hidden,
                // so the next Tab restarts the order rather than continuing from it.
                wizard.CurrentStepIndex = step;
                Dispatcher.UIThread.RunJobs();

                Control? landed = CompactViewRig.StepFocus(window, forward: true);
                Assert.True(landed is not null, $"step {step}: Tab after advancing lost focus entirely");
                Assert.True(body.IsVisualAncestorOf(landed),
                    $"step {step}: Tab after advancing landed on {CompactViewRig.Describe(landed)}, which is in the " +
                    $"{(footer.IsVisualAncestorOf(landed) ? "navigation footer" : "window chrome")} rather than the step's " +
                    "own fields — the user has to tab past the navigation before reaching what they came for.");
            }
        }
        finally { window.Close(); }
    }

    /// <summary>
    /// Step 0 from a genuine cold start — window freshly opened, nothing focused, first key is Tab.
    /// <para>
    /// This one PASSED before the fix too, and that is worth recording rather than presenting it as
    /// a repair: the unscoped <c>TabIndex</c> pins sorted the input trio ahead of the entire window,
    /// which put the right control first for the wrong reason. It is asserted now because after
    /// scoping, being first is a property of the host's tree order instead — the same outcome
    /// resting on something that stays true when a control is added to the step.
    /// </para>
    /// </summary>
    [AvaloniaFact]
    public void ColdStart_FirstTabLandsInTheBody_NotTheFooter()
    {
        (WizardWindow window, _, _) = ShowCreateSrrWizard();
        try
        {
            ContentControl body = BodyHost(window);
            Assert.Null(window.FocusManager?.GetFocusedElement());

            Control? landed = CompactViewRig.StepFocus(window, forward: true);
            Assert.True(landed is not null, "cold-start Tab moved focus nowhere");
            Assert.True(body.IsVisualAncestorOf(landed),
                $"cold-start Tab landed on {CompactViewRig.Describe(landed)}, outside the wizard body");
            Assert.Equal("Release .sfv, first .rar, or folder",
                Avalonia.Automation.Peers.ControlAutomationPeer.CreatePeerForElement(landed).GetName());
        }
        finally { window.Close(); }
    }

    /// <summary>
    /// Step transitions specifically: after moving forward and then back, Tab must reach the fields
    /// of the step that is NOW shown, not the one that was.
    /// <para>
    /// Worth its own test because the step panels are <c>IsVisible</c>-bound rather than unloaded,
    /// so every step's controls exist in the tree at all times and only the visible ones
    /// participate in the tab order. A regression that got that gating wrong would leave a walk
    /// silently visiting a hidden step's fields, which no ordering assertion above would catch.
    /// </para>
    /// </summary>
    [AvaloniaFact]
    public void StepTransitions_ForwardAndBack_ReachTheNewlyShownStepsFields()
    {
        (WizardWindow window, WizardViewModel wizard, CreatorViewModel vm) = ShowCreateSrrWizard();
        try
        {
            ContentControl body = BodyHost(window);

            foreach (int step in new[] { 0, 1, 2, 3, 2, 1, 0 })
            {
                wizard.CurrentStepIndex = step;
                Dispatcher.UIThread.RunJobs();

                List<Control> visibleBodyStops = [.. body.GetVisualDescendants().OfType<Control>()
                    .Where(c => c.Focusable && c.IsEffectivelyVisible && c.IsEffectivelyEnabled)];
                Assert.True(visibleBodyStops.Count > 0, $"step {step}: no focusable body control is visible");

                List<Control> order = WalkFrom(window, visibleBodyStops[0]);

                // Every body control the walk touched must belong to the step now shown — i.e. be
                // effectively visible. A hidden step's control appearing here is the gating defect.
                foreach (Control visited in order.Where(body.IsVisualAncestorOf))
                {
                    Assert.True(visited.IsEffectivelyVisible,
                        $"step {step}: the walk reached {CompactViewRig.Describe(visited)}, which belongs to a step that is " +
                        $"not shown. Walk was: {Trail(order)}");
                }

                Assert.Contains(order, c => ReferenceEquals(c, visibleBodyStops[0]));
            }
        }
        finally { window.Close(); }
    }

    /// <summary>
    /// THE (b) HALF's premise, asserted so it cannot silently regress: the host declares the body
    /// BEFORE the footer, while rendering the footer below the body.
    /// <para>
    /// Both halves are load-bearing. If someone converts the host back to a <c>DockPanel</c> the
    /// declaration order flips (a DockPanel's fill child must come last) and the footer returns to
    /// the front of the tab order — this test says so by name rather than leaving the walk tests to
    /// fail with a puzzling partition error. And if the rows were ever reordered so the footer
    /// rendered above the body, the visual assertion catches that instead.
    /// </para>
    /// </summary>
    [AvaloniaFact]
    public void WizardHost_DeclaresBodyBeforeFooter_WhileRenderingFooterBelow()
    {
        (WizardWindow window, WizardViewModel wizard, CreatorViewModel vm) = ShowCreateSrrWizard();
        try
        {
            ContentControl body = BodyHost(window);
            DockPanel footer = Footer(window);

            var host = window.GetVisualDescendants().OfType<Grid>()
                .First(g => g.Children.Contains(body));

            int bodyAt = host.Children.IndexOf(body);
            int footerAt = host.Children.IndexOf(footer);
            Assert.True(bodyAt >= 0 && footerAt >= 0, "both bands must be direct children of the host grid");
            Assert.True(bodyAt < footerAt,
                $"the body host is declared at index {bodyAt} and the footer at {footerAt} — the body must be declared " +
                "FIRST, because tab order follows tree order and a footer declared first is tabbed first.");

            Assert.True(Grid.GetRow(body) < Grid.GetRow(footer), "the footer must still RENDER below the body");

            double bodyBottom = body.TranslatePoint(new Point(0, body.Bounds.Height), window)!.Value.Y;
            double footerTop = footer.TranslatePoint(new Point(0, 0), window)!.Value.Y;
            Assert.True(footerTop >= bodyBottom - 0.5,
                $"the footer renders at y={footerTop:F1}, overlapping or above the body which ends at y={bodyBottom:F1}");
        }
        finally { window.Close(); }
    }

    /// <summary>
    /// THE (a) HALF: the Create-SRR body's two picker rows are <c>DockPanel</c>s whose Browse
    /// buttons are docked Right and therefore declared FIRST, so their markup order is the REVERSE
    /// of what the user sees. Explicit <c>TabIndex</c> puts keyboard order back into visual order,
    /// and <c>KeyboardNavigation.TabNavigation="Local"</c> scopes those pins to their own row.
    /// <para>
    /// The scoping is what the section-8 guidance was about. Unscoped, the pins were compared
    /// against the whole window's navigation scope, where every other control carries the default
    /// <c>int.MaxValue</c> — so the pinned trio sorted ahead of everything. Both the INVERSION (the
    /// premise the pins exist to correct) and the resulting order are asserted, so a future reorder
    /// of the markup that makes the pins unnecessary fails by name instead of passing on a premise
    /// that has rotted.
    /// </para>
    /// </summary>
    [AvaloniaFact]
    public void PickerRows_TabInVisualOrder_DespiteReversedMarkupOrder()
    {
        (WizardWindow window, WizardViewModel wizard, _) = ShowCreateSrrWizard();
        try
        {
            AssertRow(window, wizard, step: 0,
            [
                ("Release .sfv, first .rar, or folder", typeof(TextBox)),
                ("Browse for input file", typeof(Button)),
                ("Browse folder for release input", typeof(Button)),
            ]);

            AssertRow(window, wizard, step: 3,
            [
                ("Save SRR to", typeof(TextBox)),
                ("Browse for output path", typeof(Button)),
            ]);
        }
        finally { window.Close(); }
    }

    /// <summary>
    /// THE (a) HALF, made demonstrable. Scoping the row's <c>TabIndex</c> pins changes NOTHING about
    /// today's shipped step 0 — measured, and worth saying plainly rather than shipping an attribute
    /// with an unverifiable claim attached: with the host order fixed, the pinned trio sorts to the
    /// front of the window scope, which is where it belongs anyway, so removing
    /// <c>TabNavigation="Local"</c> breaks none of the other tests in this file.
    /// <para>
    /// What it protects is the case the a11y follow-up report's section 8 predicted: "the moment
    /// anything focusable is added to that step … it would be excluded exactly as the Creator's form
    /// was". This test creates that moment. A focusable control is inserted into step 0's own panel
    /// ABOVE the picker row, and must therefore be tabbed BEFORE it.
    /// </para>
    /// <para>
    /// MEASURED both ways. Unscoped, the pins are compared against the whole window (every other
    /// control carrying the default <c>int.MaxValue</c>), so the trio sorts ahead of everything and
    /// the walk reads trio → injected-field → Close → Next: a field the user sees ABOVE the row is
    /// tabbed AFTER it. Scoped, the pins order only the row's own three children and the row takes
    /// its ordinary place, so the walk reads injected-field → trio → Close → Next. This test is RED
    /// without the attribute and green with it, which is the only honest basis for keeping it.
    /// </para>
    /// </summary>
    [AvaloniaFact]
    public void ScopedPins_KeepAFieldAddedAboveTheRow_AheadOfIt()
    {
        (WizardWindow window, WizardViewModel wizard, CreatorViewModel vm) = ShowCreateSrrWizard();
        try
        {
            TextBox input = window.GetVisualDescendants().OfType<TextBox>().Single(t => t.Name == "WizInputTextBox");
            var row = (DockPanel)input.GetVisualParent()!;
            var stepPanel = (StackPanel)row.GetVisualParent()!;

            var addedAbove = new CheckBox { Content = "a field added above the picker row" };
            stepPanel.Children.Insert(stepPanel.Children.IndexOf(row), addedAbove);
            Dispatcher.UIThread.RunJobs();

            Control? first = CompactViewRig.StepFocus(window, forward: true);
            Assert.True(first is not null, "cold-start Tab moved focus nowhere");
            Assert.True(ReferenceEquals(first, addedAbove),
                $"a control added ABOVE the picker row must be tabbed before it, but Tab landed on " +
                $"{CompactViewRig.Describe(first!)} — the row's TabIndex pins are escaping their row and sorting " +
                "ahead of the step. Scope them with KeyboardNavigation.TabNavigation=\"Local\".");

            Control? second = CompactViewRig.StepFocus(window, forward: true);
            Assert.True(second is not null && ReferenceEquals(second, input),
                "after the added field, the row's own first control (the path box) should follow");

            // The markup property is asserted LAST, deliberately. Asserted first — as this test
            // originally did — a sabotage that removes the attribute trips here and reports
            // "expected Local, got Continue", which says what changed but not what it COSTS. Run
            // last, the same sabotage reports the behavioural failure instead: Tab landed on the
            // wrong control. That is the message a future sabotage-runner needs, and it is the one
            // that would also fire if the attribute survived but stopped working.
            Assert.Equal(KeyboardNavigationMode.Local, KeyboardNavigation.GetTabNavigation(row));
        }
        finally { window.Close(); }
    }

    private static void AssertRow(WizardWindow window, WizardViewModel wizard, int step, (string Name, Type Type)[] visualRow)
    {
        wizard.CurrentStepIndex = step;
        Dispatcher.UIThread.RunJobs();

        List<Control> expected = [.. visualRow.Select(entry =>
            window.GetVisualDescendants().OfType<Control>().Single(c =>
                c.GetType() == entry.Type
                && Avalonia.Automation.Peers.ControlAutomationPeer.CreatePeerForElement(c).GetName() == entry.Name))];

        var panel = (DockPanel)expected[0].GetVisualParent()!;

        // The premise: markup order is the REVERSE of the visual order the pins produce.
        List<Control> markup = [.. panel.Children.OfType<Control>()];
        Assert.Equal(expected.AsEnumerable().Reverse(), markup);

        // And the pins put the walk back into visual order.
        List<Control> walked = WalkFrom(window, expected[0]).Take(expected.Count).ToList();
        Assert.Equal(expected, walked);
    }
}
