using Avalonia.Automation;
using Avalonia.Automation.Peers;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using ReScene.App.Core.ViewModels;
using ReScene.App.Core.ViewModels.Wizards;
using ReScene.Manager.Views.Wizards;

namespace ReScene.Manager.Tests;

/// <summary>
/// The Edit-SRR wizard's step 3 exists to report the outcome of the save. It used to report it only
/// visually: the result block toggled <c>IsVisible</c>, and an element that is not realized when its
/// text arrives gives an assistive technology no transition to notice, so a screen-reader user was
/// told nothing at all on the step that carries the answer.
/// </summary>
public class EditSRRAnnouncementTests
{
    private const string NoWorkingCopy = "Nothing to save — no working copy was created.";

    /// <summary>
    /// Drives the REAL wizard — steps and their <c>OnLeave</c> hooks come from
    /// <see cref="BeginnerWizardFactory"/>, not from a step list this test invents — so what is
    /// proved is the shipping path: pressing Next on the save step runs <c>vm.Save</c> and the result
    /// reaches a live region that is realized at that moment.
    /// <para>
    /// That last part is the load-bearing ordering fact, and it is not obvious from
    /// <c>Save()</c>'s own summary ("called when leaving the save step"), which reads as though the
    /// text arrives while step 3 is still hidden. <c>WizardViewModel.Next()</c> increments
    /// <c>CurrentStepIndex</c> BEFORE invoking <c>OnLeave</c>, so step 3 is already visible when the
    /// message lands. Asserted here rather than trusted: if that order is ever swapped, the live
    /// line stops being realized in time and this fails.
    /// </para>
    /// </summary>
    [AvaloniaFact]
    public void AdvancingOffTheSaveStep_DeliversTheResultToARealizedLiveRegion()
    {
        BeginnerShellViewModel shell = BeginnerShellTestFactory.Create();
        SRREditorViewModel vm = shell.SRREditor;
        (WizardViewModel wizard, Control body) = BeginnerWizardFactory.Create(BeginnerCard.EditSRR, shell);

        var window = new WizardWindow(wizard, body) { Width = 900, Height = 700 };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        try
        {
            TextBlock live = FindResultStatus(window);
            Assert.Equal(AutomationLiveSetting.Polite, AutomationProperties.GetLiveSetting(live));
            Assert.Equal(string.Empty, live.Text);

            // Stand on the save step with an output path but no working copy, so Save() takes its
            // one branch that needs no file system and produces a known literal.
            vm.OutputPath = @"X:\output\edited.srr";
            wizard.CurrentStepIndex = 2;
            Dispatcher.UIThread.RunJobs();

            bool visibleWhenTextArrived = false;
            vm.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(vm.ResultMessage) && vm.ResultMessage.Length > 0)
                {
                    visibleWhenTextArrived = live.IsEffectivelyVisible;
                }
            };

            wizard.NextCommand.Execute(null);
            Dispatcher.UIThread.RunJobs();

            Assert.Equal(3, wizard.CurrentStepIndex);
            Assert.Equal(NoWorkingCopy, live.Text);
            Assert.True(visibleWhenTextArrived,
                "the result arrived while the live region was NOT realized, so an assistive technology " +
                "would have no transition to announce — WizardViewModel.Next() must increment " +
                "CurrentStepIndex before invoking the leaving step's OnLeave hook");
        }
        finally { window.Close(); }
    }

    /// <summary>
    /// The live line is always in the tree, which is the whole correction — and it is NOT guarded by
    /// <c>ShowResult</c> any more. Toggling that property must therefore leave both the text and the
    /// layout untouched, which is the relational form of "the visible rendering did not change":
    /// what the old binding controlled, nothing now controls.
    /// </summary>
    [AvaloniaFact]
    public void TheLiveResultLine_IsAlwaysInTheTree_AndItsLayoutNoLongerDependsOnShowResult()
    {
        BeginnerShellViewModel shell = BeginnerShellTestFactory.Create();
        SRREditorViewModel vm = shell.SRREditor;
        var wizard = new WizardViewModel("Edit an SRR", vm,
            [.. Enumerable.Range(0, 4).Select(i => new WizardStep { Title = $"s{i}" })]);
        var window = new WizardWindow(wizard, new EditSRRWizardBody()) { Width = 900, Height = 700 };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        try
        {
            wizard.CurrentStepIndex = 3;
            Dispatcher.UIThread.RunJobs();

            TextBlock live = FindResultStatus(window);
            var header = (StackPanel)live.GetVisualParent()!;

            vm.ResultMessage = "Saved edited SRR to:\nX:\\output\\edited.srr";
            vm.ShowResult = true;
            Dispatcher.UIThread.RunJobs();

            Assert.True(live.IsEffectivelyVisible);
            double heightWithResultShown = header.Bounds.Height;
            Assert.True(heightWithResultShown > 0, "rig validity: the step-3 header measured to nothing");

            vm.ShowResult = false;
            Dispatcher.UIThread.RunJobs();

            Assert.True(live.IsEffectivelyVisible,
                "the result line went out of the tree when ShowResult turned false — it is bound to " +
                "visibility again, and an element that is not realized when its text arrives announces nothing");
            Assert.Equal(vm.ResultMessage, live.Text);
            Assert.Equal(heightWithResultShown, header.Bounds.Height, precision: 1);
        }
        finally { window.Close(); }
    }

    /// <summary>
    /// The announced name is the text itself. A <c>Name</c> would be read INSTEAD of it, which is how
    /// a live region ends up announcing a label over and over while the news it carries is lost.
    /// </summary>
    [AvaloniaFact]
    public void TheLiveResultLine_AnnouncesItsOwnText_WithNoCompetingName()
    {
        BeginnerShellViewModel shell = BeginnerShellTestFactory.Create();
        SRREditorViewModel vm = shell.SRREditor;
        var wizard = new WizardViewModel("Edit an SRR", vm,
            [.. Enumerable.Range(0, 4).Select(i => new WizardStep { Title = $"s{i}" })]);
        var window = new WizardWindow(wizard, new EditSRRWizardBody()) { Width = 900, Height = 700 };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        try
        {
            wizard.CurrentStepIndex = 3;
            vm.ResultMessage = NoWorkingCopy;
            Dispatcher.UIThread.RunJobs();

            TextBlock live = FindResultStatus(window);
            Assert.Null(AutomationProperties.GetName(live));

            AutomationPeer peer = ControlAutomationPeer.CreatePeerForElement(live);
            Assert.Equal(NoWorkingCopy, peer.GetName());
        }
        finally { window.Close(); }
    }

    private static TextBlock FindResultStatus(Window window) =>
        window.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Name == "ResultStatus");
}
