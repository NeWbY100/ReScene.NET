using System.Windows;
using System.Windows.Controls;
using ReScene.NET.Models;

namespace ReScene.NET.Controls;

/// <summary>
/// Renders a <see cref="FieldStatus"/> as a colored glyph (✓/ℹ/⚠/✗) plus its message.
/// Hidden when the status state is <see cref="FieldState.None"/>.
/// </summary>
public partial class FieldStatusLine : UserControl
{
    public static readonly DependencyProperty StatusProperty =
        DependencyProperty.Register(
            nameof(Status),
            typeof(FieldStatus),
            typeof(FieldStatusLine),
            new PropertyMetadata(FieldStatus.None));

    public FieldStatusLine() => InitializeComponent();

    /// <summary>The status to display. Defaults to <see cref="FieldStatus.None"/> (hidden).</summary>
    public FieldStatus Status
    {
        get => (FieldStatus)GetValue(StatusProperty);
        set => SetValue(StatusProperty, value);
    }
}
