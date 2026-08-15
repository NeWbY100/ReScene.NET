using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace ReScene.Manager.Converters;

/// <summary>
/// Maps the SRS Reconstructor's <c>ResultSuccess</c> flag to the result banner's background tint: a
/// translucent success-green when <see langword="true"/>, a translucent error-red otherwise. Replaces
/// the WPF <c>Border.Style</c> + <c>DataTrigger</c> that swapped the Background Setter — Avalonia has
/// no style triggers, so the binding is redirected through this converter instead. The tint colors are
/// the literal ARGB values the WPF <c>DataTrigger</c> used.
/// </summary>
public sealed class ResultSuccessToBrushConverter : IValueConverter
{
    private static readonly IBrush _successBrush = new SolidColorBrush(Color.Parse("#304EC9B0"));
    private static readonly IBrush _failureBrush = new SolidColorBrush(Color.Parse("#30FF4444"));

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? _successBrush : _failureBrush;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
