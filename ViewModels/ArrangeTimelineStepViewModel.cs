namespace Sonator.ViewModels;

public sealed class ArrangeTimelineStepViewModel : ViewModelBase
{
    private bool _isCurrent;

    public ArrangeTimelineStepViewModel(int absoluteStepIndex, StepViewModel? sourceStep)
    {
        AbsoluteStepIndex = absoluteStepIndex;
        IsActive = sourceStep?.IsActive == true;
        MidiNoteLabel = sourceStep?.MidiNoteLabel ?? string.Empty;
    }

    public int AbsoluteStepIndex { get; }

    public bool IsActive { get; }

    public string MidiNoteLabel { get; }

    public bool IsCurrent
    {
        get => _isCurrent;
        set => SetProperty(ref _isCurrent, value);
    }
}
