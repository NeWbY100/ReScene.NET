using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using ReScene.App.Core.Models;

namespace ReScene.Manager.Controls;

/// <summary>
/// Renders a <see cref="FieldStatus"/> as a colored glyph (✓/ℹ/⚠/✗) plus its message.
/// <para>
/// Renders nothing when the state is <see cref="FieldState.None"/> — an empty glyph and an empty
/// message — but is NOT hidden, because its message is a live region and a hidden subtree has no
/// automation nodes to announce from. See the remarks in the XAML.
/// </para>
/// </summary>
public partial class FieldStatusLine : UserControl
{
    public static readonly StyledProperty<FieldStatus?> StatusProperty =
        AvaloniaProperty.Register<FieldStatusLine, FieldStatus?>(nameof(Status), FieldStatus.None);

    public FieldStatusLine()
    {
        AvaloniaXamlLoader.Load(this);
    }

    /// <summary>The status to display. Defaults to <see cref="FieldStatus.None"/> (hidden).</summary>
    public FieldStatus? Status
    {
        get => GetValue(StatusProperty);
        set => SetValue(StatusProperty, value);
    }
}
