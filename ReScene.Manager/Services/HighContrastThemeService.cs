using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Platform;

namespace ReScene.Manager.Services;

/// <summary>
/// Follows the operating system's contrast preference: while the OS reports
/// <see cref="ColorContrastPreference.High"/> the high-contrast dictionary is merged over the app's
/// design tokens, and when it reports <see cref="ColorContrastPreference.NoPreference"/> the app is
/// exactly its normal self again.
/// <para>
/// The app invents no preference of its own and offers no in-app toggle. That is deliberate: a user
/// who needs high contrast has already said so once, to their OS, and every other app on the machine
/// honours it without being asked twice.
/// </para>
/// <para>
/// WHAT THE PLATFORM ACTUALLY GIVES, verified against the shipped assemblies rather than assumed —
/// the lesson of an earlier round that built a plan around an API that did not exist.
/// <see cref="PlatformColorValues"/> carries a <see cref="PlatformColorValues.ContrastPreference"/>
/// and three accent colours, and nothing else: there is no OS high-contrast PALETTE to read, so the
/// dictionary is derived rather than copied (see the remarks in HighContrast.axaml).
/// <see cref="IPlatformSettings.ColorValuesChanged"/> is the live signal, so a contrast change while
/// the app is running applies without a restart — IF the platform backend raises it. Whether Windows,
/// X11 and macOS all do is not something a headless test can establish, and it is not claimed here.
/// </para>
/// </summary>
public sealed class HighContrastThemeService : IDisposable
{
    /// <summary>Where the overrides come from. Public so tests name the same URI the app loads.</summary>
    public const string DictionaryUri = "avares://ReScene.Manager/Resources/HighContrast.axaml";

    private readonly Application _application;
    private readonly IPlatformSettings? _settings;
    private readonly IResourceProvider _overrides;

    public HighContrastThemeService(Application application, IPlatformSettings? settings)
    {
        _application = application;
        _settings = settings;
        _overrides = new ResourceInclude((Uri?)null) { Source = new Uri(DictionaryUri) };
    }

    /// <summary>True while the high-contrast overrides are merged into the application.</summary>
    public bool IsHighContrastApplied =>
        _application.Resources.MergedDictionaries.Contains(_overrides);

    /// <summary>
    /// Applies the current preference and subscribes for later changes. Safe to call when the
    /// platform exposes no settings at all — the app simply stays in its normal theme.
    /// </summary>
    public void Start()
    {
        if (_settings is null)
        { return; }

        Apply(_settings.GetColorValues().ContrastPreference);
        _settings.ColorValuesChanged += OnColorValuesChanged;
    }

    /// <summary>
    /// Merges or removes the overrides to match <paramref name="preference"/>. Idempotent, because
    /// the platform may raise ColorValuesChanged for an unrelated reason (a theme or accent change)
    /// while the contrast preference has not moved.
    /// </summary>
    public void Apply(ColorContrastPreference preference)
    {
        bool wanted = preference == ColorContrastPreference.High;
        if (wanted == IsHighContrastApplied)
        { return; }

        if (wanted)
        {
            _application.Resources.MergedDictionaries.Add(_overrides);
        }
        else
        {
            _application.Resources.MergedDictionaries.Remove(_overrides);
        }
    }

    private void OnColorValuesChanged(object? sender, PlatformColorValues e) => Apply(e.ContrastPreference);

    public void Dispose()
    {
        if (_settings is not null)
        { _settings.ColorValuesChanged -= OnColorValuesChanged; }
        _application.Resources.MergedDictionaries.Remove(_overrides);
    }
}
