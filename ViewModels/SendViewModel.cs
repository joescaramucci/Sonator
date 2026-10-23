using System.Globalization;

namespace Sonator.ViewModels;

// One send of a track's channel strip: how much of the track goes to one bus. The mixer makes these again
// whenever the buses change, so they only read and write the track.
public sealed class SendViewModel : ViewModelBase
{
    public SendViewModel(PatternTrackViewModel track, PatternTrackViewModel bus)
    {
        Track = track;
        Bus = bus;
    }

    public PatternTrackViewModel Track { get; }

    public PatternTrackViewModel Bus { get; }

    // The colour of the send knob's cap: each bus its own, as a console's aux sends are.
    public System.Windows.Media.Brush Cap { get; init; } = System.Windows.Media.Brushes.Goldenrod;

    private static readonly System.Windows.Media.Brush[] CapColours = new[] { "#D9A72E", "#3A7BD5", "#3FA65A", "#C4413A", "#8E5BC7", "#D9772E" }
        .Select(colour =>
        {
            var brush = new System.Windows.Media.SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(colour));
            brush.Freeze();
            return (System.Windows.Media.Brush)brush;
        })
        .ToArray();

    // The cap colour of the bus at this place among the buses.
    public static System.Windows.Media.Brush CapFor(int busIndex) => CapColours[busIndex % CapColours.Length];

    // A fader position, as a track's volume: 0.75 sends the sound as it is.
    public double Level
    {
        get => Track.SendLevel(Bus.BusId);
        set
        {
            Track.SetSend(Bus.BusId, value);
            RaisePropertyChanged();
            RaisePropertyChanged(nameof(LevelDisplay));
        }
    }

    public string LevelDisplay => Level <= 0.0001
        ? "off"
        : string.Format(CultureInfo.CurrentCulture, "{0:+0.0;-0.0;0.0}", 20 * Math.Log10(Level / TrackEffectsHost.UnityVolume));
}
