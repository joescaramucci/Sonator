using System.Collections.ObjectModel;

namespace Sonator.ViewModels;

public sealed class ArrangeRowViewModel
{
    public ArrangeRowViewModel(string trackName, string instrument, ObservableCollection<ArrangeCellViewModel> cells)
    {
        TrackName = trackName;
        Instrument = instrument;
        Cells = cells;
    }

    public string TrackName { get; }

    public string Instrument { get; }

    public ObservableCollection<ArrangeCellViewModel> Cells { get; }
}
