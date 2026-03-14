using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace ReScene.NET.Converters;

/// <summary>
/// Compares a bound integer value to a ConverterParameter integer.
/// Returns Visible when they match, Collapsed otherwise.
/// Used for showing/hiding contextual toolbar buttons based on SelectedTabIndex.
/// </summary>
public class IndexToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is int index && parameter is string paramStr && int.TryParse(paramStr, out int target))
            return index == target ? Visibility.Visible : Visibility.Collapsed;
        return Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
