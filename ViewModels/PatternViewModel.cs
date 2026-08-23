using System.Collections.ObjectModel;

namespace Sonator.ViewModels;

public sealed class PatternViewModel : ViewModelBase
{
    private string _name;
    private string _summary;
    private string _slot;
    private int _transpose;

    public PatternViewModel(string name, string summary, string slot, ObservableCollection<PatternTrackViewModel> tracks)
    {
        _name = name;
        _summary = summary;
        _slot = slot;
        Tracks = tracks;
    }

    public string Name
    {
        get => _name;
        set => SetProperty(ref _name, value);
    }

    public string Summary
    {
        get => _summary;
        set => SetProperty(ref _summary, value);
    }

    public string Slot
    {
        get => _slot;
        set => SetProperty(ref _slot, value);
    }

    public int Transpose
    {
        get => _transpose;
        set => SetProperty(ref _transpose, value);
    }

    public ObservableCollection<PatternTrackViewModel> Tracks { get; }
}
