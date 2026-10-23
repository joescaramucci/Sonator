using System.Globalization;
using System.Windows.Data;

namespace Sonator;

// For a row of switches of which one is on, as the oscillator's frequencies: on when the value is the
// switch's own (the parameter), and switching it on sets the value to it.
public sealed class SameValueConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        string.Equals(System.Convert.ToString(value, CultureInfo.InvariantCulture), parameter as string, StringComparison.Ordinal);

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is true && int.TryParse(parameter as string, NumberStyles.Integer, CultureInfo.InvariantCulture, out var chosen)
            ? chosen
            : Binding.DoNothing;
}
