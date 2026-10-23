using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace Sonator;

// The cap colour of the n-th knob of an effect, in turn as on SSL's panels: green, white, blue, red, black.
public sealed class KnobCapConverter : IValueConverter
{
    private static readonly Brush[] Caps = new[] { "#1E8A5A", "#E8E8E6", "#2456B0", "#B4262A", "#2C2E30" }
        .Select(colour =>
        {
            var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(colour));
            brush.Freeze();
            return (Brush)brush;
        })
        .ToArray();

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        Caps[(value is int index ? index : 0) % Caps.Length];

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
