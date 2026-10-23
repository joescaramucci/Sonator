using System.Globalization;
using System.Windows.Data;
using Sonator.ViewModels;

namespace Sonator;

// A track's sends for its channel strip, one per bus: from the track, the buses and the buses' version,
// which changes whenever a bus is added, removed or replaced.
public sealed class SendControlsConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture) =>
        values is [PatternTrackViewModel track, IEnumerable<PatternTrackViewModel> buses, ..]
            ? buses.Select((bus, index) => new SendViewModel(track, bus) { Cap = SendViewModel.CapFor(index) }).ToList()
            : Array.Empty<SendViewModel>();

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
