using Avalonia;
using Avalonia.Headless;

[assembly: Avalonia.Headless.AvaloniaTestApplication(typeof(ReScene.App.Core.Tests.TestAppBuilder))]

namespace ReScene.App.Core.Tests;

public sealed class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<Avalonia.Application>()
            .UseSkia()
            // Avalonia 12 decoupled text shaping from the rendering backend: an explicit .UseSkia()
            // no longer brings a shaper with it, and without one every glyph run measures as
            // fallback boxes. UsePlatformDetect() (production) still wires HarfBuzz itself.
            .UseHarfBuzz()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
}
