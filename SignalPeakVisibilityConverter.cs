using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace Sonator;

public sealed class SignalPeakVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return value is double signalLevel && signalLevel >= 0.875
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}
