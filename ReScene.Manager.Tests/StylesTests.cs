using Avalonia;
using Avalonia.Automation.Peers;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless.XUnit;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

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
