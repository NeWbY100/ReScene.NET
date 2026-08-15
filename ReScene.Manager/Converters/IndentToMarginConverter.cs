using System.Globalization;
using Avalonia;
using Avalonia.Data.Converters;

namespace ReScene.Manager.Converters;

/// <summary>
/// Maps the property row's <c>IsIndented</c> flag to the "Property" column's left indent, replacing the
/// WPF <c>DataTrigger</c> that swapped the <c>Margin</c> Setter: indented rows use <c>20,0,0,0</c>,
/// others the resting <c>2,0,0,0</c>.
/// </summary>
public sealed class IndentToMarginConverter : IValueConverter
{
    private static readonly Thickness _indented = new(20, 0, 0, 0);
    private static readonly Thickness _normal = new(2, 0, 0, 0);

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? _indented : _normal;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
