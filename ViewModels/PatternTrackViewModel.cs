using System.Collections.ObjectModel;

namespace Sonator.ViewModels;

public sealed class PatternTrackViewModel : ViewModelBase
{
    private string _name;
    private string _instrument;
    private int _channel;
    private int _programNumber;
    private int _noteNumber;
    private bool _isMuted;
    private bool _isSolo;
    private bool _isSelected;
    private double _signalLevel;
    private IReadOnlyList<MidiNoteEventViewModel> _midiNoteEvents;

    public PatternTrackViewModel(
        string name,
        string instrument,
        int channel,
        int noteNumber,
        ObservableCollection<StepViewModel> steps,
        int programNumber = 0,
        IReadOnlyList<MidiNoteEventViewModel>? midiNoteEvents = null)
    {
        _name = name;
        _instrument = instrument;
        _channel = channel;
        _noteNumber = noteNumber;
        _programNumber = programNumber;
        Steps = steps;
        _midiNoteEvents = midiNoteEvents ?? Array.Empty<MidiNoteEventViewModel>();
    }

    public string Name
    {
        get => _name;
        set => SetProperty(ref _name, value);
    }

    public string Instrument
    {
        get => _instrument;
        set => SetProperty(ref _instrument, value);
    }

    public int Channel
    {
        get => _channel;
        set => SetProperty(ref _channel, Math.Clamp(value, 1, 16));
    }

    public int NoteNumber
    {
        get => _noteNumber;
        set => SetProperty(ref _noteNumber, value);
    }

    public int ProgramNumber
    {
        get => _programNumber;
        set => SetProperty(ref _programNumber, Math.Clamp(value, 0, 127));
    }

    public ObservableCollection<StepViewModel> Steps { get; }

    public IReadOnlyList<MidiNoteEventViewModel> MidiNoteEvents
    {
        get => _midiNoteEvents;
        set => SetProperty(ref _midiNoteEvents, value);
    }

    public bool IsMuted
    {
        get => _isMuted;
        set => SetProperty(ref _isMuted, value);
    }

    public bool IsSolo
    {
        get => _isSolo;
        set => SetProperty(ref _isSolo, value);
    }

    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }

    public double SignalLevel
    {
        get => _signalLevel;
        set => SetProperty(ref _signalLevel, Math.Clamp(value, 0.0, 1.0));
    }
}
