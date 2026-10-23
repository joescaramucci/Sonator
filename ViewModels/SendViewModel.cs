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
