using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace Sonator;

// Shows something only while a count is zero, such as a hint in an empty list.
public sealed class ZeroToVisibleConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is 0 ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
