namespace Sonator.ViewModels;

public sealed record MidiNoteEventViewModel(
    int StepIndex,
    int DurationSteps,
    int NoteNumber,
    int Velocity);
