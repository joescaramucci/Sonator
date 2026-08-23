namespace Sonator.ViewModels;

public sealed class StepViewModel : ViewModelBase
{
    private bool _isActive;
    private bool _isCurrent;
    private int? _midiNoteNumber;
    private int _velocity = 96;
    private int _durationSteps = 1;

    public StepViewModel(int index, bool isActive = false)
    {
        Index = index;
        IsActive = isActive;
    }

    public int Index { get; }

    public string Label => (Index + 1).ToString("00");

    public bool IsActive
    {
        get => _isActive;
        set => SetProperty(ref _isActive, value);
    }

    public bool IsCurrent
    {
        get => _isCurrent;
        set => SetProperty(ref _isCurrent, value);
    }

    public int? MidiNoteNumber
    {
        get => _midiNoteNumber;
        set
        {
            if (SetProperty(ref _midiNoteNumber, value))
            {
                RaisePropertyChanged(nameof(MidiNoteLabel));
            }
        }
    }

    public int Velocity
    {
        get => _velocity;
        set => SetProperty(ref _velocity, Math.Clamp(value, 1, 127));
    }

    public int DurationSteps
    {
        get => _durationSteps;
        set => SetProperty(ref _durationSteps, Math.Max(1, value));
    }

    public string MidiNoteLabel => MidiNoteNumber.HasValue ? MidiNoteHelper.ToLabel(MidiNoteNumber.Value) : "-";
}
