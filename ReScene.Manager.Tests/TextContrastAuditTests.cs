using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace ReScene.Manager.Tests;

/// <summary>
/// The text-contrast audit: every (foreground, background) pair the app ACTUALLY COMPOSES, measured
/// against WCAG AA.
/// <para>
/// The population is the whole point, and it is why an earlier attempt was thrown away. Taking the
/// cross-product of every foreground token against every surface token gives 82 pairs below 4.5:1 in
/// the default theme — a figure that is arithmetically true and almost entirely fictional, because
/// <c>AccentPressed</c> is a button's pressed fill and never sits as text on
/// <c>PropertyHighlightBrush</c>. Auditing that list would mean changing colours to satisfy
/// compositions nobody ever renders.
/// </para>
/// <para>
/// So pairs are read off REAL VISUAL TREES instead: every instantiable control in the app is hosted,
/// and each element carrying a foreground is matched with the nearest ancestor that actually paints a
/// background. What is measured is what is drawn.
/// </para>
/// <para>
/// WHAT THIS DOES NOT CATCH. Compositions that only appear in a state no default construction
/// reaches — a hover brush applied by a style trigger, a selected row, a control whose colours change
/// with data — are outside it; those are covered for the specific signals that matter by the
/// rendered-pixel tests in CreatorCompactTests. Colours Fluent supplies and this app never redefines
/// are ignored, because a pair is only reported when BOTH ends map to a token this app owns, and
/// changing Fluent's palette is not in this app's gift. Literal colours in markup are likewise
/// invisible to the token mapping; three exist, enumerated in <see cref="HighContrastTokenTests"/>.
/// </para>
/// </summary>
public class TextContrastAuditTests
{
    /// <summary>
    /// Pairs that fail a threshold on purpose, each with the reason it is allowed to. An entry here
    /// is an argument, not a suppression: it has to say why the standard permits it, or why the
    /// composition is not what it appears to be.
    /// </summary>
    private static readonly (string Foreground, string Background, string Reason)[] Exempt =
    [
        ("ForegroundDisabled", "*",
            "WCAG 2.2 SC 1.4.3 exempts text that is part of an inactive user interface component; " +
            "this token exists only to render disabled controls"),
        ("SystemControlForegroundBaseLowBrush", "*",
            "the Fluent-facing alias of ForegroundDisabled, applied to the same disabled states"),
    ];

    private const double NormalTextThreshold = 4.5;
    private const double LargeTextThreshold = 3.0;

    /// <summary>
    /// The role split, taken from the standard rather than invented. WCAG 2.2 SC 1.4.3 sets 3:1 for
    /// LARGE text — at least 18pt, or 14pt bold, which at this app's 96 DPI is 24px and 18.66px — and
    /// 4.5:1 for everything else. SC 1.4.11 independently sets 3:1 for graphical objects.
    /// <para>
    /// This is what decides the one pair the audit first reported as a failure: MessageDialog's
    /// severity glyph (ℹ / ⚠ / ✗) is a 26px icon sitting beside a message that states the same thing
    /// in words. It qualifies for 3:1 on BOTH readings — large text and graphical object — and clears
    /// it at 3.68:1. Two independent routes to the same threshold is a classification, not a
    /// judgment call, which is why it is applied here in code rather than written into the exemption
    /// table as an argument.
    /// </para>
    /// </summary>
    private static double Required(bool large) => large ? LargeTextThreshold : NormalTextThreshold;

    private static bool IsLargeText(Visual visual)
    {
        (double size, FontWeight weight) = visual switch
        {
            TextBlock t => (t.FontSize, t.FontWeight),
            TemplatedControl c => (c.FontSize, c.FontWeight),
            _ => (0d, FontWeight.Normal),
        };

        return weight >= FontWeight.Bold ? size >= 18.66 : size >= 24.0;
    }

    [AvaloniaFact]
    public void EveryComposedTextPair_MeetsWcagAa_OrIsExemptWithAReason()
    {
        IReadOnlyDictionary<IBrush, string> tokens = TokenNamesByBrush();
        var pairs = new Dictionary<(string Fg, string Bg, bool Large), double>();

        foreach (Type type in InstantiableControls())
        {
            Control control;
            try
            { control = (Control)Activator.CreateInstance(type)!; }
            catch { continue; }

            Window window = control as Window ?? new Window { Width = 1200, Height = 900, Content = control };
            window.Show();
            Dispatcher.UIThread.RunJobs();
            try
            {
                foreach (Visual visual in window.GetVisualDescendants())
                {
                    if (ForegroundOf(visual) is not { } fg || !tokens.TryGetValue(fg, out string? fgName))
                    { continue; }
                    if (NearestBackground(visual) is not var (bgToken, bgRendered) || bgToken is null)
                    { continue; }
                    if (!tokens.TryGetValue(bgToken, out string? bgName))
                    { continue; }

                    pairs[(fgName, bgName, IsLargeText(visual))] = ContrastRatio(ColourOf(fg), bgRendered);
                }
            }
            finally { window.Close(); }
        }

        Assert.True(pairs.Count > 0,
            "rig validity: no composed foreground/background pair was found at all, so this audit examined nothing");

        List<string> failures =
        [
            .. pairs.Where(p => p.Value < Required(p.Key.Large))
                .Where(p => !IsExempt(p.Key.Fg, p.Key.Bg))
                .OrderBy(p => p.Value)
                .Select(p => $"{p.Key.Fg} on {p.Key.Bg} is {p.Value:F2}:1, needs {Required(p.Key.Large):F1}:1" +
                             $" ({(p.Key.Large ? "large text or graphical object" : "normal text")})"),
        ];

        Assert.True(failures.Count == 0,
            $"{failures.Count} of {pairs.Count} composed text pairs fall below WCAG AA." +
            $"{Environment.NewLine}{string.Join(Environment.NewLine, failures)}" +
            $"{Environment.NewLine}Fix the token, or record the pair in {nameof(Exempt)} with the reason the " +
            "standard permits it.");
    }

    private static bool IsExempt(string fg, string bg) =>
        Exempt.Any(e => e.Foreground == fg && (e.Background == "*" || e.Background == bg));

    private static Color ColourOf(IBrush brush) => ((ISolidColorBrush)brush).Color;

    private static IBrush? ForegroundOf(Visual visual) => visual switch
    {
        TextBlock { Foreground: ISolidColorBrush b } => b,
        TemplatedControl { Foreground: ISolidColorBrush b } => b,
        _ => null,
    };

    /// <summary>
    /// The nearest painted background, and — where that paint is translucent — what it actually
    /// composites to over whatever lies behind it.
    /// <para>
    /// This is not a refinement, it is the difference between a true and a false reading. The
    /// Inspector's warning bar fills with <c>#3DE0A030</c>: amber at 24% alpha. Treating that colour
    /// as if it were opaque reports its text at 1.58:1 — a failure against a light amber that is
    /// never drawn. Composited over the panel behind it, the bar is a dark muted amber and the text
    /// on it passes. Contrast is a property of what reaches the eye.
    /// </para>
    /// </summary>
    private static (IBrush Token, Color Rendered)? NearestBackground(Visual visual)
    {
        IBrush? token = null;
        Color accumulated = default;
        bool started = false;

        foreach (Visual ancestor in visual.GetVisualAncestors())
        {
            ISolidColorBrush? brush = ancestor switch
            {
                Border { Background: ISolidColorBrush b } => b,
                Panel { Background: ISolidColorBrush b } => b,
                TemplatedControl { Background: ISolidColorBrush b } => b,
                _ => null,
            };

            if (brush is not { Color.A: > 0 })
            { continue; }

            if (!started)
            {
                token = brush;
                accumulated = brush.Color;
                started = true;
            }
            else
            {
                accumulated = Composite(accumulated, brush.Color);
            }

            if (accumulated.A == 255)
            { return (token!, accumulated); }
        }

        // Nothing opaque behind it: the window itself paints last, so treat the accumulation as final.
        return started ? (token!, accumulated) : null;
    }

    /// <summary>Source-over: <paramref name="over"/> composited on top of <paramref name="under"/>.</summary>
    private static Color Composite(Color over, Color under)
    {
        double a = over.A / 255.0;
        byte Blend(byte o, byte u) => (byte)Math.Round((o * a) + (u * (1 - a)));
        return Color.FromArgb(
            (byte)Math.Min(255, over.A + (int)Math.Round(under.A * (1 - a))),
            Blend(over.R, under.R), Blend(over.G, under.G), Blend(over.B, under.B));
    }

    private static IEnumerable<Type> InstantiableControls() =>
        typeof(ReScene.Manager.Controls.FieldStatusLine).Assembly.GetTypes()
            .Where(t => t is { IsAbstract: false, IsGenericTypeDefinition: false })
            .Where(typeof(Control).IsAssignableFrom)
            .Where(t => t.GetConstructor(Type.EmptyTypes) is not null)
            .OrderBy(t => t.Name, StringComparer.Ordinal);

    /// <summary>
    /// Maps a rendered brush back to the token that defines it — by REFERENCE, not by colour.
    /// <para>
    /// Matching on colour was the first attempt and it was wrong in a way worth keeping: the app's
    /// accent is <c>#FF0078D4</c> and so is the Fluent palette's, so every Fluent-supplied foreground
    /// at that colour was attributed to this app's <c>AccentPrimary</c> token. The audit duly
    /// reported two AA failures against a token that is never used as a foreground anywhere in the
    /// markup — grep confirms it appears only as Background and BorderBrush — so acting on them would
    /// have meant changing a colour that could not affect the pair reported. A colour is not a token,
    /// exactly as a name was not a classification.
    /// </para>
    /// <para>
    /// A <c>DynamicResource</c> resolves to the very brush instance held in the dictionary, so
    /// reference identity answers "did this come from OUR token?" exactly, and brushes the app does
    /// not own fail to map and are skipped.
    /// </para>
    /// </summary>
    private static IReadOnlyDictionary<IBrush, string> TokenNamesByBrush()
    {
        var byBrush = new Dictionary<IBrush, string>(ReferenceEqualityComparer.Instance as IEqualityComparer<IBrush>
            ?? EqualityComparer<IBrush>.Default);

        string path = Path.Combine(ResourcesRoot(), "Tokens.axaml");
        foreach (Match m in Regex.Matches(File.ReadAllText(path),
            @"<SolidColorBrush x:Key=""(?<key>[A-Za-z0-9]+)"""))
        {
            string key = m.Groups["key"].Value;
            if (Application.Current!.Resources.TryGetResource(key, null, out object? value)
                && value is IBrush brush)
            {
                Assert.True(byBrush.TryAdd(brush, key),
                    $"two token keys resolve to the SAME brush instance ({key} collides with " +
                    $"{byBrush[brush]}), so this mapping would silently merge them and report pair " +
                    "identity against whichever name happened to be read first");
            }
        }

        // Ten groups of tokens in this app share a hex value — AccentPrimary, BorderFocused and
        // SystemAccentBrush are all #FF0078D4, and nine other groups collide likewise. Keying this
        // map on COLOUR merged them, which left ratios right and pair IDENTITY wrong: a nudge to one
        // token would then be measured against a name that does not carry it, missing that token's
        // other compositions entirely. Keying on the brush instance keeps them distinct, and the
        // assertion above makes a regression to colour-keying fail rather than quietly under-count.
        Assert.True(byBrush.Count >= 40,
            $"rig validity: only {byBrush.Count} token brushes resolved, so the mapping is not seeing " +
            "the dictionary it is meant to describe");

        return byBrush;
    }

    private static string ResourcesRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            string candidate = Path.Combine(dir.FullName, "ReScene.Manager", "Resources");
            if (File.Exists(Path.Combine(candidate, "Tokens.axaml")))
            { return candidate; }
            dir = dir.Parent;
        }

        throw new DirectoryNotFoundException($"could not find ReScene.Manager/Resources above {AppContext.BaseDirectory}");
    }

    private static double ContrastRatio(Color a, Color b)
    {
        double la = RelativeLuminance(a);
        double lb = RelativeLuminance(b);
        return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
    }

    private static double RelativeLuminance(Color c) =>
        (0.2126 * Linear(c.R)) + (0.7152 * Linear(c.G)) + (0.0722 * Linear(c.B));

    private static double Linear(byte channel)
    {
        double v = channel / 255.0;
        return v <= 0.04045 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4);
    }
}
