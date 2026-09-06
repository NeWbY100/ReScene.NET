using Avalonia;
using Avalonia.Headless;

[assembly: AvaloniaTestApplication(typeof(ReScene.Manager.Tests.TestAppBuilder))]

// Avalonia headless tests share one global UI Dispatcher/Application AND a single process-global
// Avalonia.Logging.Logger.Sink (BindingErrorSink installs itself there). Running test classes in
// parallel lets one test's binding-error log propagate through the chained sink into a concurrently
// running test's BindingErrorSink, causing intermittent false failures. Serialize the whole assembly
// (headless Avalonia is single-UI-thread anyway, so parallelism buys nothing here).
[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace ReScene.Manager.Tests;

/// <summary>
/// Configures the headless Avalonia application used to run <c>[AvaloniaFact]</c> tests.
/// Boots the real <see cref="App"/> (not a bare <see cref="Application"/>) so Tokens.axaml and the
/// Fluent theme are merged into <c>Application.Current.Resources</c> exactly as in production —
/// required for tests that assert on <c>DynamicResource</c>-backed brushes.
/// </summary>
public sealed class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UseSkia()
            // Avalonia 12 decoupled text shaping from the rendering backend: an explicit .UseSkia()
            // no longer brings a shaper with it, and without one every glyph run measures as
            // fallback boxes. UsePlatformDetect() (production) still wires HarfBuzz itself.
            .UseHarfBuzz()
            // Match production (Program.BuildAvaloniaApp): UIFontFamily's fallback chain references
            // the embedded Inter collection (fonts:Inter#Inter); without registering it, font
            // resolution on machines lacking Segoe UI (Linux CI) would skip to $Default or fail.
            .WithInterFont()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
}
