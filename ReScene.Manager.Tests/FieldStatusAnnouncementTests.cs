using Avalonia.Automation;
using Avalonia.Automation.Peers;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using ReScene.App.Core.Models;
using ReScene.Manager.Controls;

namespace ReScene.Manager.Tests;

/// <summary>
/// <see cref="FieldStatusLine"/> is the validation status shown under every path field — 32 instances
/// across 11 surfaces, the widest-reaching announcement in the app. Its message carries
/// <c>LiveSetting="Polite"</c> and always did; what it did NOT do was announce the FIRST status a
/// field produces, which is the one that matters most.
/// <para>
/// The mechanism, and why a live region alone was not enough:
/// <c>ControlAutomationPeer.GetChildrenCore</c> filters <c>IsVisible=false</c> controls out of a
/// peer's children, so a hidden subtree has no automation nodes at all. The message TextBlock used to
/// sit inside a Grid gated on the state being anything but <see cref="FieldState.None"/>. Going from
/// None to Ok/Error therefore did not RENAME an existing node — it CREATED one, and a node that comes
/// into existence already carrying its text raises no name-change for an assistive technology to
/// announce. Only the transitions between two visible states worked, which is the case the control's
/// own comment happened to describe.
/// </para>
/// </summary>
public class FieldStatusAnnouncementTests
{
    private static (Window Window, FieldStatusLine Line) Host()
    {
        var line = new FieldStatusLine { Status = FieldStatus.None };
        var window = new Window { Width = 500, Height = 400, Content = new StackPanel { Children = { line } } };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return (window, line);
    }

    private static TextBlock Message(FieldStatusLine line) =>
        line.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Name != "Glyph");

    private static int CountPeerDescendants(AutomationPeer peer)
    {
        int n = 0;
        foreach (AutomationPeer child in peer.GetChildren())
        { n += 1 + CountPeerDescendants(child); }
        return n;
    }

    /// <summary>
    /// The defect, asserted at the level it actually occurs: the set of automation NODES must be the
    /// same before and after the first status arrives. If the node count grows, the status text
    /// arrived with a node that did not previously exist, and there was no rename to announce.
    /// </summary>
    [AvaloniaFact]
    public void TheFirstStatusAFieldProduces_ChangesNames_NotTheNodeTree()
    {
        (Window window, FieldStatusLine line) = Host();
        try
        {
            int idle = CountPeerDescendants(ControlAutomationPeer.CreatePeerForElement(line));

            line.Status = FieldStatus.Error("Pick an .srr (whole release) or an .srs (single sample) file.");
            Dispatcher.UIThread.RunJobs();

            int announced = CountPeerDescendants(ControlAutomationPeer.CreatePeerForElement(line));

            Assert.True(idle == announced,
                $"the status line exposed {idle} automation nodes while idle and {announced} once it had something " +
                "to say, so the message arrived on a node that did not exist a moment earlier. " +
                "ControlAutomationPeer.GetChildrenCore filters IsVisible=false controls out of the peer tree, so a " +
                "live region inside a visibility-gated container cannot announce the FIRST status a field produces — " +
                "only later changes between two visible states.");
        }
        finally { window.Close(); }
    }

    /// <summary>
    /// The same requirement stated as the property the peer filter actually keys on, so a failure
    /// says which knob was turned rather than only that a count moved.
    /// </summary>
    [AvaloniaFact]
    public void TheMessage_IsRealized_WhileTheFieldStillHasNothingToSay()
    {
        (Window window, FieldStatusLine line) = Host();
        try
        {
            TextBlock message = Message(line);

            Assert.Equal(AutomationLiveSetting.Polite, AutomationProperties.GetLiveSetting(message));
            Assert.True(message.IsEffectivelyVisible,
                "the message is not realized while the status is None, so it has no automation node and the first " +
                "status cannot be announced");
            Assert.True(string.IsNullOrEmpty(message.Text), "an idle status line must render no text");
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void EveryStateTransition_ReachesTheLiveMessage_IncludingBackToNone()
    {
        (Window window, FieldStatusLine line) = Host();
        try
        {
            TextBlock message = Message(line);

            line.Status = FieldStatus.Ok("SRR — will restore every embedded sample.");
            Dispatcher.UIThread.RunJobs();
            Assert.Equal("SRR — will restore every embedded sample.", message.Text);

            line.Status = FieldStatus.Warning("Pick an .srr or an .srs file.");
            Dispatcher.UIThread.RunJobs();
            Assert.Equal("Pick an .srr or an .srs file.", message.Text);

            // Clearing a field must clear what was said about it, and must not take the node away.
            line.Status = FieldStatus.None;
            Dispatcher.UIThread.RunJobs();
            Assert.True(string.IsNullOrEmpty(message.Text));
            Assert.True(message.IsEffectivelyVisible);
        }
        finally { window.Close(); }
    }

    /// <summary>
    /// The cost of the fix, pinned so it cannot grow unnoticed. An idle line now reserves one caption
    /// line box where it used to collapse to nothing — the price of keeping the message realized, and
    /// unavoidable: measured, the glyph and the message each reserve the full line box on their own,
    /// so gating the glyph alone would have saved none of it.
    /// <para>
    /// The bound is relational rather than a literal DIP count, because a different font stack moves
    /// the absolute numbers without anything being wrong. What must hold is that an idle line is
    /// ALREADY the height a speaking line will be — give or take the glyph's own ascender, measured
    /// at 1 DIP here — so a status arriving does not reflow the form around it.
    /// </para>
    /// </summary>
    [AvaloniaFact]
    public void AnIdleLine_AlreadyReservesTheHeightASpeakingLineNeeds()
    {
        (Window window, FieldStatusLine line) = Host();
        try
        {
            double idle = line.Bounds.Height;
            Assert.True(idle > 0, "an idle line that collapses to nothing has no realized message to announce with");

            line.Status = FieldStatus.Error("Pick an .srr file.");
            Dispatcher.UIThread.RunJobs();

            double speaking = line.Bounds.Height;
            Assert.True(speaking - idle < idle / 2,
                $"an idle line reserves {idle:F0} DIPs but a speaking one needs {speaking:F0} — the status arriving " +
                "reflows the form around it, which the always-realized shape exists to avoid");
        }
        finally { window.Close(); }
    }
}
