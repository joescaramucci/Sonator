using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace Sonator;

// Left margin of a block on the timeline: its start in sixteenth steps times the width of one step.
public sealed class StepOffsetConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        if (values.Length != 2 || values[0] is not (int or double) || values[1] is not double stepWidth)
        {
            return new Thickness(0, 4, 0, 4);
        }

        var steps = System.Convert.ToDouble(values[0], CultureInfo.InvariantCulture);
        return new Thickness(steps * stepWidth, 4, 0, 4);
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}
