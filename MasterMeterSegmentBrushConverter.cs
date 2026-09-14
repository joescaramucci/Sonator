using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace Sonator;

public sealed class MasterMeterSegmentBrushConverter : IValueConverter
{
    private static readonly Brush InactiveBrush = new SolidColorBrush(Color.FromRgb(0x11, 0x19, 0x22));
    private static readonly Brush ActiveBrush = new SolidColorBrush(Color.FromRgb(0x52, 0xC7, 0xA1));
    private static readonly Brush PeakBrush = new SolidColorBrush(Color.FromRgb(0xFF, 0x4D, 0x4D));

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not double level || parameter is not string raw || !int.TryParse(raw, out var segmentIndex))
        {
            return InactiveBrush;
        }

        const int segmentCount = 12;
        const int peakSegments = 2;
        var filledSegments = (int)Math.Ceiling(Math.Clamp(level, 0.0, 1.0) * segmentCount);
        if (segmentIndex >= filledSegments)
        {
            return InactiveBrush;
        }

        var peakThreshold = 0.99;
        if (level < peakThreshold)
        {
            return ActiveBrush;
        }

        return segmentIndex >= segmentCount - peakSegments ? PeakBrush : ActiveBrush;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}
