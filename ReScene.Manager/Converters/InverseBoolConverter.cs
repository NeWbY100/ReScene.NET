using System.Globalization;
using Avalonia.Data.Converters;

namespace ReScene.Manager.Converters;

/// <summary>
/// Inverts a boolean value. Avalonia's <c>IsVisible</c> is itself a plain <see langword="bool"/>
/// (unlike WPF's three-state <c>Visibility</c>), so — unlike the WPF
/// <c>InverseBoolToVisibilityConverter</c> this replaces — no Visibility enum is involved.
/// </summary>
public sealed class InverseBoolConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is not bool b || !b;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is bool b && !b;
}
