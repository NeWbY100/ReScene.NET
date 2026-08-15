using Avalonia;
using Avalonia.Automation;
using Avalonia.Automation.Peers;
using Avalonia.Automation.Provider;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless.XUnit;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using ReScene.Manager.Behaviors;
using ReScene.Manager.Controls;

namespace ReScene.Manager.Tests;

/// <summary>
/// Proves the semantic style classes in <c>Resources/Styles.axaml</c> (merged into
/// <c>App.axaml</c> after <c>FluentTheme</c>) actually resolve — not just that the file parses.
/// Each case renders a control carrying one class into a headless <see cref="Window"/>, pumps
/// layout via <c>Dispatcher.UIThread.RunJobs</c>, and asserts a representative property
/// took the token-driven value the class sets, plus zero Avalonia binding errors overall.
/// Expected colors are hardcoded from <c>Resources/Tokens.axaml</c> (same convention as
/// <see cref="FieldStatusLineTests"/>) rather than re-resolved via <c>FindResource</c>, so a test
/// failure can't be masked by both the style and the assertion drifting together.
/// </summary>
public class StylesTests
{
    private static Color AccentPrimary => Color.Parse("#FF0078D4");
    private static Color AccentError => Color.Parse("#FFF44747");
    private static Color BorderSubtle => Color.Parse("#FF3C3C3C");
    private static Color BorderMedium => Color.Parse("#FF4D4D4D");
    private static Color PanelBackground => Color.Parse("#FF252526");
    private static Color SurfaceBackground => Color.Parse("#FF2D2D30");
    private static Color HeaderForeground => Color.Parse("#FFE0E0E0");
    private static Color PanelHeaderSeparator => Color.Parse("#FF333333");
    private static Color LogTerminalForeground => Color.Parse("#FF4EC9B0");
    private static Color StatusVersionForeground => Color.Parse("#FFAAAAAA");

    private static Color Solid(IBrush? brush) => Assert.IsAssignableFrom<ISolidColorBrush>(brush).Color;

    [AvaloniaFact]
    public void AllSemanticClasses_ResolveTheirTokenDrivenProperties_WithNoBindingErrors()
    {
        var primary = new Button { Content = "Primary", Classes = { "primary" } };
        var cancel = new Button { Content = "Cancel", Classes = { "cancel" } };
        var ghost = new Button { Content = "Ghost", Classes = { "ghost" } };
        var recentItem = new Button { Content = "Recent", Classes = { "recentItem" } };
        var toolbarToggle = new ToggleButton { Content = "Hex", Classes = { "toolbar" } };
        var statusLink = new Button { Content = "v1.0", Classes = { "link", "statusVersion" } };
        var mono = new TextBlock { Text = "DEADBEEF", Classes = { "mono" } };
        var panelHeader = new TextBlock { Text = "Section", Classes = { "panelHeader" } };
        var panelHeaderBar = new Border { Classes = { "panelHeaderBar" }, Child = new TextBlock { Text = "Header" } };
        var section = new Border { Classes = { "section" }, Child = new TextBlock { Text = "Body" } };
        var logList = new ListBox { Classes = { "logList" }, ItemsSource = new[] { "line one", "line two" } };

        var root = new StackPanel
        {
            Children =
            {
                primary, cancel, ghost, recentItem, toolbarToggle, statusLink,
                mono, panelHeader, panelHeaderBar, section, logList,
            },
        };

        using var sink = new BindingErrorSink();
        var window = new Window { Content = root };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        // Button.primary: accent-filled, white text, MediumRadius.
        Assert.Equal(AccentPrimary, Solid(primary.Background));
        Assert.Equal(Colors.White, Solid(primary.Foreground));
        Assert.Equal(new CornerRadius(3), primary.CornerRadius);

        // Button.cancel: transparent bg, AccentError foreground/border.
        Assert.Equal(Colors.Transparent, Solid(cancel.Background));
        Assert.Equal(AccentError, Solid(cancel.Foreground));
        Assert.Equal(AccentError, Solid(cancel.BorderBrush));

        // Button.ghost: transparent bg, subtle border.
        Assert.Equal(Colors.Transparent, Solid(ghost.Background));
        Assert.Equal(BorderSubtle, Solid(ghost.BorderBrush));

        // Button.recentItem: flat (no border), left-aligned content.
        Assert.Equal(Colors.Transparent, Solid(recentItem.Background));
        Assert.Equal(new Thickness(0), recentItem.BorderThickness);
        Assert.Equal(HorizontalAlignment.Left, recentItem.HorizontalContentAlignment);

        // ToggleButton.toolbar: compact ghost-style toggle (base/unchecked state) + WPF-matching FontSize.
        Assert.Equal(Colors.Transparent, Solid(toolbarToggle.Background));
        Assert.Equal(BorderMedium, Solid(toolbarToggle.BorderBrush));
        Assert.Equal(11, toolbarToggle.FontSize);

        // Button.statusVersion: muted rest foreground set via a STYLE setter (so it wins over
        // Button.link's HyperlinkForeground and lets :pointerover brighten it — hover is launch-smoke).
        Assert.Equal(StatusVersionForeground, Solid(statusLink.Foreground));

        // TextBlock.mono: monospaced family + size.
        Assert.Contains("Cascadia Mono", mono.FontFamily.Name, StringComparison.Ordinal);
        Assert.Equal(14, mono.FontSize);

        // TextBlock.panelHeader: semibold header foreground.
        Assert.Equal(FontWeight.SemiBold, panelHeader.FontWeight);
        Assert.Equal(HeaderForeground, Solid(panelHeader.Foreground));

        // Border.panelHeaderBar: surface background, bottom separator.
        Assert.Equal(SurfaceBackground, Solid(panelHeaderBar.Background));
        Assert.Equal(PanelHeaderSeparator, Solid(panelHeaderBar.BorderBrush));
        Assert.Equal(new Thickness(0, 0, 0, 1), panelHeaderBar.BorderThickness);

        // Border.section: panel background, subtle border, LargeRadius, card margin.
        Assert.Equal(PanelBackground, Solid(section.Background));
        Assert.Equal(BorderSubtle, Solid(section.BorderBrush));
        Assert.Equal(new CornerRadius(4), section.CornerRadius);
        Assert.Equal(new Thickness(0, 0, 0, 4), section.Margin);

        // ListBox.logList: terminal-style monospaced foreground on panel background.
        Assert.Equal(PanelBackground, Solid(logList.Background));
        Assert.Equal(LogTerminalForeground, Solid(logList.Foreground));
        Assert.Contains("Cascadia Mono", logList.FontFamily.Name, StringComparison.Ordinal);

        Assert.Empty(sink.Messages);
    }

    /// <summary>
    /// The re-templated <c>Expander.helpDisclosure</c> shows its header
    /// ToggleButton only in compact mode. An earlier pass walked the peer tree and confirmed the toggle's
    /// peer is pruned at normal — but judged the arrangement coherent on the strength of the
    /// Expander peer being non-focusable. That was the wrong test: NON-FOCUSABLE IS NOT
    /// NON-ACTIONABLE. <c>IExpandCollapseProvider</c> is an action, and an AT can invoke
    /// <c>Collapse()</c> on it directly. MEASURED against the unfixed code: at NORMAL size that set
    /// <c>IsExpanded=false</c> and hid the body while the header toggle stayed pruned from both the
    /// visual and UIA trees — a state the visual design says cannot exist, with no affordance in
    /// ANY modality to undo it short of resizing the window.
    /// <para>
    /// FIXED at the level that owns the invariant rather than at the peer. Suppressing the pattern
    /// itself is not reachable from here: <c>Expander.OnCreateAutomationPeer</c> is
    /// <c>protected virtual</c>, so a custom peer requires subclassing the control and editing every
    /// view that hosts one, and <c>AutomationProperties.AccessibilityView=Raw</c> was MEASURED not
    /// to prune the peer from its parent's children walk at all. What
    /// <see cref="CompactHeightBehavior"/> already DECLARES — "flat mode always renders the body
    /// expanded" — is now enforced continuously instead of only on transitions, so a Collapse at
    /// normal is a genuine no-op: the state does not change and the body stays visible, which is
    /// the user-facing guarantee this behavior must provide.
    /// </para>
    /// <para>
    /// COMPACT keeps ExpandCollapse on the container deliberately, and it is NOT a second
    /// independent action: it drives the very same <c>IsExpanded</c> the toggle exposes (asserted
    /// below — invoking the container's Collapse moves the toggle's own IsChecked with it, and the
    /// behavior's HelpOpen tracks both), and only the toggle is keyboard focusable, so only the
    /// toggle is ever announced as actionable on focus. One state, one authoritative route to
    /// change it, two coherent views of it — complementary, not
    /// duplicated, now proven rather than asserted.
    /// </para>
    /// <para>
    /// Modes are driven through the real behavior (Threshold + window height), not by poking the
    /// class on, so the wiring under test is the shipped one. The accessible name is deliberately
    /// NOT the "Help" most callers use: mirroring is a TemplateBinding, and a distinctive value
    /// proves the binding rather than coinciding with a hardcoded string.
    /// </para>
    /// </summary>
    [AvaloniaFact]
    public void HelpDisclosure_ExposesCoherentAutomationPeers_InBothModes()
    {
        const string HelpName = "Help & links";
        const double Threshold = 300;

        var expander = new HelpDisclosure { Classes = { "helpDisclosure" } };
        var body = new TextBlock { Text = "help body" };
        expander.Content = body;
        AutomationProperties.SetName(expander, HelpName);

        var host = new Grid();
        host.Children.Add(expander);
        CompactHeightBehavior.SetThreshold(host, Threshold);
        CompactHeightBehavior.SetHelpExpander(host, expander);

        var window = new Window { Width = 700, Height = Threshold + 100, Content = host };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        try
        {
            // ── NORMAL: flat section. Header toggle hidden, body shown. ──
            Assert.DoesNotContain("compactHeight", host.Classes);
            ToggleButton toggle = expander.GetVisualDescendants().OfType<ToggleButton>().Single();
            Assert.False(toggle.IsVisible, "precondition: the base style hides the header toggle at normal size");
            Assert.True(expander.IsExpanded, "precondition: flat mode force-expands the body");
            Assert.True(body.IsEffectivelyVisible);

            AutomationPeer expanderPeer = ControlAutomationPeer.CreatePeerForElement(expander);
            Assert.Equal(HelpName, expanderPeer.GetName());
            Assert.False(expanderPeer.IsKeyboardFocusable(), "the container is not a Tab stop");
            Assert.DoesNotContain(DescendantPeers(expanderPeer), p => OwnerOf(p) is ToggleButton);
            Assert.Contains(expander.GetVisualDescendants(), v => ReferenceEquals(v, toggle));
            // ^ the toggle CONTROL is still in the visual tree, so its peer's absence is genuine UIA
            //   pruning of an invisible control rather than the template not creating it at all.

            // THE LOAD-BEARING ASSERTIONS: this container has no expand/collapse semantics AT ALL.
            // Not merely a withheld provider — the peer does not implement the interface, so there
            // is nothing to invoke and, crucially, nothing to RELAY as events either.
            Assert.Null(expanderPeer.GetProvider<IExpandCollapseProvider>());
            Assert.False(expanderPeer is IExpandCollapseProvider);
            Assert.Equal(AutomationControlType.Group, expanderPeer.GetAutomationControlType());

            // Subscribed BEFORE the scenario, not after: the defect this pins emitted its
            // contradicting event DURING the churn, so a subscription installed afterwards saw a
            // clean stream and proved nothing.
            var normalEvents = new List<object?>();
            expanderPeer.PropertyChanged += (_, e) => normalEvents.Add(e.NewValue);

            // The invariant guard stays as the PROGRAMMATIC defense (nothing in the UI or
            // the UIA tree can now reach it, but code still can): a collapse at normal size never
            // costs the user their Help.
            expander.IsExpanded = false;
            Dispatcher.UIThread.RunJobs();
            Assert.True(body.IsEffectivelyVisible,
                "flat mode force-expands the body, so a programmatic collapse must not survive — " +
                "the header toggle is hidden here, leaving no affordance in any modality to undo it");
            Assert.True(expander.IsExpanded);
            Assert.False(toggle.IsVisible, "and it must not have revealed the toggle as a side effect");
            Assert.Empty(normalEvents.OfType<ExpandCollapseState>());
            // ^ the guard's revert is a true->false->true round trip whose notifications nest, so the
            //   expander peer used to end the stream on Collapsed while the region sat expanded —
            //   telling a subscribed AT the opposite of the truth. No such semantics exist here now.

            // ── COMPACT: real disclosure. Toggle appears; the behavior resets it collapsed. ──
            // Still subscribed from above, so the mode transition itself is inside the window: a
            // real compact entry collapses the expander, and the container must stay silent about
            // that too rather than announcing a disclosure change it does not model.
            normalEvents.Clear();
            window.Height = Threshold - 1;
            Dispatcher.UIThread.RunJobs();
            Assert.Empty(normalEvents.OfType<ExpandCollapseState>());
            Assert.Contains("compactHeight", host.Classes);
            Assert.True(toggle.IsVisible);
            Assert.False(expander.IsExpanded, "compact entry starts collapsed (condition 5)");

            AutomationPeer compactExpanderPeer = ControlAutomationPeer.CreatePeerForElement(expander);

            // STABLE topology: the container withholds ExpandCollapse in BOTH modes, not per-mode.
            // A peer whose advertised patterns shift underneath a client that has already resolved
            // them is worse than one that never offered them, and a window resize is not something
            // the assistive technology initiated.
            Assert.Null(compactExpanderPeer.GetProvider<IExpandCollapseProvider>());
            Assert.Equal(AutomationControlType.Group, compactExpanderPeer.GetAutomationControlType());

            AutomationPeer togglePeer = Assert.Single(
                DescendantPeers(compactExpanderPeer), p => OwnerOf(p) is ToggleButton);
            Assert.Equal(HelpName, togglePeer.GetName());
            Assert.Equal(AutomationControlType.Button, togglePeer.GetAutomationControlType());
            Assert.True(togglePeer.IsEnabled());
            Assert.False(togglePeer.IsOffscreen());

            // THE SINGLE ACTIONABLE ROUTE, and the only one: Toggle on the header button, which is
            // also the only keyboard-focusable peer in the region.
            IToggleProvider? toggleProvider = togglePeer.GetProvider<IToggleProvider>();
            Assert.NotNull(toggleProvider);
            Assert.True(togglePeer.IsKeyboardFocusable());
            Assert.False(compactExpanderPeer.IsKeyboardFocusable());

            // Driving that one route moves the whole region coherently — the expander's state, the
            // toggle's own reported state, and the behavior's donation — and the automation event
            // stream never ends on a value that contradicts what the region actually is. (The
            // contradiction this pins was real before the pattern was withheld: an AT-driven
            // Collapse at normal size produced Expanded -> Collapsed while the body stayed visible,
            // because the invariant guard's revert re-entered ahead of the peer's own subscription.
            // With no provider to invoke, that path no longer exists to be raced.)
            // Subscribed BEFORE the scenario on BOTH peers: the container must stay silent about
            // disclosure, and the toggle must NOT — silencing the container must not silence the
            // feature, because the toggle's peer is where the semantics live now.
            var containerEvents = new List<object?>();
            var toggleEvents = new List<object?>();
            compactExpanderPeer.PropertyChanged += (_, e) => containerEvents.Add(e.NewValue);
            togglePeer.PropertyChanged += (_, e) => toggleEvents.Add(e.NewValue);

            toggleProvider.Toggle();
            Dispatcher.UIThread.RunJobs();
            Assert.True(expander.IsExpanded);
            Assert.True(toggle.IsChecked, "the toggle reports the state it just set");
            Assert.True(CompactHeightBehavior.GetHelpOpen(host), "and the behavior's donation follows it");
            Assert.Equal(ToggleState.On, toggleProvider.ToggleState);
            Assert.Contains(ToggleState.On, toggleEvents.OfType<ToggleState>());
            Assert.Empty(containerEvents.OfType<ExpandCollapseState>());

            containerEvents.Clear();
            toggleEvents.Clear();
            toggleProvider.Toggle();
            Dispatcher.UIThread.RunJobs();
            Assert.False(expander.IsExpanded, "collapsing IS allowed in compact — the toggle is right there");
            Assert.False(toggle.IsChecked);
            Assert.False(CompactHeightBehavior.GetHelpOpen(host));
            Assert.Equal(ToggleState.Off, toggleProvider.ToggleState);
            Assert.Contains(ToggleState.Off, toggleEvents.OfType<ToggleState>());
            Assert.Empty(containerEvents.OfType<ExpandCollapseState>());
        }
        finally { window.Close(); }
    }

    private static Control? OwnerOf(AutomationPeer peer) =>
        peer is ControlAutomationPeer control ? control.Owner : null;

    private static IEnumerable<AutomationPeer> DescendantPeers(AutomationPeer peer)
    {
        foreach (AutomationPeer child in peer.GetChildren())
        {
            yield return child;
            foreach (AutomationPeer grandchild in DescendantPeers(child))
            {
                yield return grandchild;
            }
        }
    }
}
