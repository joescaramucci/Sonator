namespace Sonator.ViewModels;

public sealed class TrackViewModel
{
    public TrackViewModel(string name, string instrument, int channel)
    {
        Name = name;
        Instrument = instrument;
        Channel = channel;
    }

    public string Name { get; }

    public string Instrument { get; }

    public int Channel { get; }
}
