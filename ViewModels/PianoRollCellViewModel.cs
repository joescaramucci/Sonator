using System.ComponentModel;

namespace Sonator.ViewModels;

public sealed class PianoRollCellViewModel : ViewModelBase, IDisposable
{
    private readonly StepViewModel _step;
    private readonly int _midiNoteNumber;

    public PianoRollCellViewModel(StepViewModel step, int midiNoteNumber)
    {
        _step = step;
        _midiNoteNumber = midiNoteNumber;
        _step.PropertyChanged += OnStepPropertyChanged;
    }

    public StepViewModel Step => _step;

    public int StepIndex => _step.Index;

    public int MidiNoteNumber => _midiNoteNumber;

    public string StepLabel => _step.Label;

    public string NoteLabel => MidiNoteHelper.ToLabel(_midiNoteNumber);

    public bool IsActive => _step.IsActive && _step.MidiNoteNumber == _midiNoteNumber;

    public bool IsCurrent => _step.IsCurrent;

    public bool IsPlayhead
    {
        get => _isPlayhead;
        set => SetProperty(ref _isPlayhead, value);
    }

    private bool _isPlayhead;

    private void OnStepPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(StepViewModel.IsActive) or nameof(StepViewModel.MidiNoteNumber) or nameof(StepViewModel.IsCurrent))
        {
            RaisePropertyChanged(nameof(IsActive));
            RaisePropertyChanged(nameof(IsCurrent));
        }
    }

    public void Dispose()
    {
        _step.PropertyChanged -= OnStepPropertyChanged;
    }
}
