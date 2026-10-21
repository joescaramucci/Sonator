using System.Globalization;
using System.Windows.Data;

namespace Sonator;

// Width of an audio clip on the timeline: its length in sixteenth-note steps times the width of one step.
public sealed class AudioClipWidthConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        if (values.Length != 2 || values[0] is not double steps || values[1] is not double stepWidth)
        {
            return 0d;
        }

        return steps * stepWidth;
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}
