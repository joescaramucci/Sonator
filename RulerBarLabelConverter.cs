using System.Globalization;
using System.Windows.Data;

namespace Sonator;

// The name of a bar on the timeline's ruler, as much of it as the bar's width leaves room for: "BAR 5", then
// "5", and on the narrowest bars only every fourth bar's number.
public sealed class RulerBarLabelConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        if (values.Length != 2 || values[0] is not int bar || values[1] is not double width)
        {
            return string.Empty;
        }

        return width >= 46 ? $"BAR {bar}"
            : width >= 22 || (bar - 1) % 4 == 0 ? bar.ToString(CultureInfo.InvariantCulture)
            : string.Empty;
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}
