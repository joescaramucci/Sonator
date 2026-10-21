namespace Sonator.ViewModels;

// StartOffset and LengthFraction are parts of a step (0 to 1) for notes played without quantization.
public sealed record MidiNoteEventViewModel(
    int StepIndex,
    int DurationSteps,
    int NoteNumber,
    int Velocity,
    double StartOffset = 0,
    double LengthFraction = 0);
