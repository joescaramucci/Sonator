namespace Sonator.Models;

public sealed record ProjectDocument(
    string Version,
    double Tempo,
    int BarCount,
    string TimeSignature,
    string? SelectedPatternSlot,
    string? SelectedMidiOutputDeviceName,
    int LoopStartStep,
    int LoopEndStep,
    List<PatternDocument> Patterns,
    List<ArrangeRowDocument> ArrangeRows);

public sealed record PatternDocument(
    string Name,
    string Summary,
    string Slot,
    List<TrackDocument> Tracks);

public sealed record TrackDocument(
    string Name,
    string Instrument,
    int Channel,
    int NoteNumber,
    List<StepDocument> Steps,
    int ProgramNumber = 0,
    List<MidiNoteEventDocument>? MidiNoteEvents = null);

public sealed record StepDocument(
    int Index,
    bool IsActive,
    int? MidiNoteNumber,
    int Velocity,
    int DurationSteps);

public sealed record MidiNoteEventDocument(
    int StepIndex,
    int DurationSteps,
    int NoteNumber,
    int Velocity);

public sealed record ArrangeRowDocument(
    string TrackName,
    string Instrument,
    List<ArrangeCellDocument> Cells);

public sealed record ArrangeCellDocument(
    string Label,
    string Detail,
    string Accent);
