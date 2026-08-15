using System.Reflection;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Automation.Peers;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace ReScene.Manager.Tests;

/// <summary>
/// The structural form of the lesson `FieldStatusLine` cost: <b>no element carrying a live region may
/// sit under an ancestor whose visibility is switched.</b>
/// <para>
/// <see cref="ControlAutomationPeer"/>'s <c>GetChildrenCore</c> filters <c>IsVisible=false</c>
/// controls out of a peer's children, so a hidden subtree has no automation nodes at all. A live
/// region inside one cannot announce its FIRST arrival: the text turns up on a node that did not
/// previously exist, and a node born holding its content raises no name-change. Only transitions
/// between two already-visible states work — which is why the defect survived a live region being
/// present, a test asserting the live region was present, and a census classifying it as announced.
/// </para>
/// <para>
/// Ancestry is a tree relationship, so no grep can answer this: files containing both a live region
/// and a switched visibility are ordinary and prove nothing (InspectorView holds 17 bound
/// visibilities and 2 live lines, and is correct). It is checked here on real visual trees.
/// </para>
/// <para>
/// POPULATION is derived, not authored: every instantiable <see cref="Control"/> in the
/// ReScene.Manager assembly. That closes the hole its sibling censuses have to disclose — a brand-new
/// view is included the moment it compiles, without anyone remembering to add it to a list. No
/// ViewModels are attached, deliberately: unresolved bindings leave properties at their defaults,
/// which does not change the ANCESTRY being examined, and it keeps the population free of any
/// hand-maintained hosting.
/// </para>
/// <para>
/// WHAT THIS DOES NOT CATCH. Visibility applied by a style <c>Setter</c> rather than a local value is
/// invisible to it (measured: 5 such setters exist in this app, 4 constant and 1 bound, none of them
/// over a live region). Controls whose constructor requires arguments are skipped and reported by
/// name. And it proves a live region CAN be reached, never that a screen reader announces it — that
/// remains unanswerable without a real AT session.
/// </para>
/// </summary>
public class LiveRegionAncestryTests
{
    /// <summary>
    /// Rig validity: a walk that finds nothing passes while proving nothing, so the floors exist to
    /// catch the rig breaking rather than to guard completeness.
    /// <para>
    /// Measured when written: <b>33</b> surfaces hosted, <b>50</b> live regions reached. The floors
    /// sit below both so ordinary churn does not fail this test, and far enough above zero that
    /// reflection finding nothing does. The live-region count exceeds the 17 <c>LiveSetting</c>
    /// attributes in markup because <see cref="ReScene.Manager.Controls.FieldStatusLine"/> is
    /// embedded 32 times, and each hosted surface reaches its own copies.
    /// </para>
    /// </summary>
    private const int MinimumSurfaces = 25;

    private const int MinimumLiveRegions = 30;

    [AvaloniaFact]
    public void NoLiveRegion_SitsUnderASwitchedVisibility()
    {
        var violations = new List<string>();
        var skipped = new List<string>();
        int surfaces = 0;
        int liveRegions = 0;

        foreach (Type type in InstantiableControls())
        {
            Control control;
            try
            {
                control = (Control)Activator.CreateInstance(type)!;
            }
            catch (Exception ex)
            {
                skipped.Add($"{type.Name} ({ex.GetBaseException().GetType().Name})");
                continue;
            }

            Window window = control as Window ?? new Window { Width = 1200, Height = 900, Content = control };
            window.Show();
            Dispatcher.UIThread.RunJobs();
            surfaces++;
            try
            {
                foreach (Visual live in window.GetVisualDescendants()
                    .Where(v => AutomationProperties.GetLiveSetting(v) != AutomationLiveSetting.Off))
                {
                    liveRegions++;
                    foreach (Visual ancestor in live.GetVisualAncestors())
                    {
                        if (ReferenceEquals(ancestor, window))
                        { break; }
                        if (!ancestor.IsSet(Visual.IsVisibleProperty))
                        { continue; }

                        violations.Add(
                            $"{type.Name}: a live region ({Describe(live)}) sits under {Describe(ancestor)}, whose " +
                            "IsVisible is switched. While that ancestor is hidden the live region has NO automation " +
                            "node, so the first text to arrive comes with a node that did not exist a moment before " +
                            "and raises no name-change — nothing is announced. Do not gate a container holding a live " +
                            "region: let it render nothing instead (empty text renders nothing).");
                    }
                }
            }
            finally { window.Close(); }
        }

        Assert.True(skipped.Count == 0,
            $"{skipped.Count} controls could not be constructed, so they were never examined: " +
            $"{string.Join(", ", skipped)}. Give this test a way to build them, or it is quietly smaller than it looks.");

        Assert.True(surfaces >= MinimumSurfaces,
            $"rig validity: only {surfaces} surfaces were hosted, fewer than the {MinimumSurfaces} this app is known " +
            "to have, so a pass would prove very little");

        Assert.True(liveRegions >= MinimumLiveRegions,
            $"rig validity: only {liveRegions} live regions were reached, fewer than the {MinimumLiveRegions} the " +
            "markup declares, so a pass would prove very little");

        Assert.True(violations.Count == 0,
            $"{violations.Count} live regions cannot announce their first message." +
            $"{Environment.NewLine}{string.Join(Environment.NewLine, violations.Distinct())}");
    }

    private static IEnumerable<Type> InstantiableControls() =>
        typeof(ReScene.Manager.Controls.FieldStatusLine).Assembly.GetTypes()
            .Where(t => t is { IsAbstract: false, IsGenericTypeDefinition: false })
            .Where(typeof(Control).IsAssignableFrom)
            .Where(t => t.GetConstructor(Type.EmptyTypes) is not null)
            .OrderBy(t => t.Name, StringComparer.Ordinal);

    private static string Describe(Visual visual)
    {
        string name = (visual as Control)?.Name is { Length: > 0 } n ? $" x:Name={n}" : string.Empty;
        string text = visual is TextBlock { Text.Length: > 0 } tb ? $" text=\"{tb.Text}\"" : string.Empty;
        return $"{visual.GetType().Name}{name}{text}";
    }
}
