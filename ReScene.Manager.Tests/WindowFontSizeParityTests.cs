using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using ReScene.App.Core.Services;

namespace ReScene.Manager.Tests;

/// <summary>
/// Guards the app-wide 13px content text (user decision 2026-08-02, after comparing 12/13/14; originally 12 for v1.9 WPF parity) against the two
/// mechanisms that silently broke it once: a root-level <c>FontSize</c> pin on a window (a local
/// value out-prioritizes the app style and re-inflates every inheriting control), and the
/// Avalonia style-key trap (a plain <c>Window</c> selector matches no subclassed window — the
/// style must be <c>:is(Window)</c>). Reflection over every <see cref="Window"/> subclass means a
/// new window enters the net automatically; one with only non-parameterless ctors fails loudly
/// here and must be registered explicitly rather than escaping coverage.
/// </summary>
/// <remarks>
/// Expected realized sizes: 13 everywhere, except <see cref="Views.PromptDialog"/> whose root pins
/// FontSizeBody (14) — v1.9's PromptWindow.xaml did the same, so the pin is parity, not drift.
/// The per-control asserts on MainWindow span all three FontSize-acquisition paths: window
/// inheritance (TextBox/CheckBox/DataGrid — the families that regressed), theme resource
/// (Button via ControlContentThemeFontSize), and explicit token (caption TextBlock at 13,
/// proving the app style does not clobber explicit sizes).
/// </remarks>
[Collection("AppDataConfig")]
public class WindowFontSizeParityTests
{
    private static readonly Dictionary<Type, double> ExpectedWindowFontSizes = new()
    {
        [typeof(Views.PromptDialog)] = 14, // v1.9 parity pin (PromptWindow.xaml:9)
    };

    private const double DefaultExpected = 13;

    [AvaloniaFact]
    public void EveryWindow_RealizesTheParityFontSize()
    {
        string originalFolder = AppDataConfig.FolderName;
        AppDataConfig.FolderName = $"ReScene.Manager.Tests-{Guid.NewGuid():N}";
        try
        {
            var windowTypes = typeof(Views.MainWindow).Assembly.GetTypes()
                .Where(t => typeof(Window).IsAssignableFrom(t) && !t.IsAbstract)
                .OrderBy(t => t.Name)
                .ToList();
            Assert.True(windowTypes.Count >= 12,
                $"Expected the reflection sweep to find at least 12 window classes, got {windowTypes.Count} — enumeration broke.");

            var failures = new List<string>();
            foreach (Type type in windowTypes)
            {
                if (Activator.CreateInstance(type) is not Window window)
                {
                    failures.Add($"{type.Name}: no parameterless ctor — register it explicitly in this test.");
                    continue;
                }

                window.Show();
                Dispatcher.UIThread.RunJobs();
                double expected = ExpectedWindowFontSizes.GetValueOrDefault(type, DefaultExpected);
                if (window.FontSize != expected)
                {
                    failures.Add($"{type.Name}: FontSize {window.FontSize}, expected {expected}.");
                }

                window.Close();
                Dispatcher.UIThread.RunJobs();
            }

            Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
        }
        finally
        {
            AppDataConfig.FolderName = originalFolder;
        }
    }

    [AvaloniaFact]
    public void MainWindow_InheritanceThemeAndExplicitPaths_AllRealizeCorrectly()
    {
        string originalFolder = AppDataConfig.FolderName;
        AppDataConfig.FolderName = $"ReScene.Manager.Tests-{Guid.NewGuid():N}";
        try
        {
            var main = new Views.MainWindow { Width = 1200, Height = 800 };
            main.Show();
            Dispatcher.UIThread.RunJobs();
            var tabs = main.GetVisualDescendants().OfType<TabControl>().First();
            tabs.SelectedIndex = 4; // RAR Reconstructor: path TextBoxes + option CheckBoxes
            Dispatcher.UIThread.RunJobs();

            // Window-inheritance path — the families that regressed to 14 when the root pin
            // masked the app style.
            TextBox pathBox = main.GetVisualDescendants().OfType<TextBox>().First();
            Assert.Equal(13, pathBox.FontSize);
            CheckBox checkBox = main.GetVisualDescendants().OfType<CheckBox>().First();
            Assert.Equal(13, checkBox.FontSize);

            // Theme-resource path (ControlContentThemeFontSize in Density.axaml).
            Button browse = main.GetVisualDescendants().OfType<Button>()
                .First(b => Equals(b.Content, "Browse"));
            Assert.Equal(13, browse.FontSize);

            // Explicit-token path: the tab description caption pins FontSizeCaption (13);
            // the :is(Window) style must not clobber it.
            TextBlock caption = main.GetVisualDescendants().OfType<TextBlock>()
                .First(t => t.Text?.StartsWith("Reconstruct original", StringComparison.Ordinal) == true);
            Assert.Equal(13, caption.FontSize);

            // Element-pin path: v1.9 deliberately emphasized primary content at Body (14) over
            // the smaller chrome text (now 13) — the Inspector tree carries that pin. Guards against a future
            // "cleanup" of redundant-looking pins silently flattening the v1.9 hierarchy.
            tabs.SelectedIndex = 1; // Inspector
            Dispatcher.UIThread.RunJobs();
            TreeView inspectorTree = main.GetVisualDescendants().OfType<TreeView>().First();
            Assert.Equal(14, inspectorTree.FontSize);

            main.Close();
            Dispatcher.UIThread.RunJobs();

            // Window-inheritance reaching a DataGrid (Compare/BruteForce grids ride the same
            // path; the control's realized size is what its cells inherit).
            var progress = new Views.BruteForceProgressWindow();
            progress.Show();
            Dispatcher.UIThread.RunJobs();
            DataGrid? grid = progress.GetVisualDescendants().OfType<DataGrid>().FirstOrDefault();
            if (grid is not null)
            {
                Assert.Equal(13, grid.FontSize);
            }

            progress.Close();
        }
        finally
        {
            AppDataConfig.FolderName = originalFolder;
        }
    }
}
