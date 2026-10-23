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
        List<ArrangeRowDocument> ArrangeRows,
        List<string[]> SongGrid,
            List<SongArrangeBlockDocument>? ArrangeBlocks = null,
        double MasterVolume = 0.75,
        List<EffectDocument>? MasterEffects = null,
        List<TrackDocument>? Buses = null,
        List<TempoChangeDocument>? TempoChanges = null,
        List<MarkerDocument>? Markers = null,
        double? MasterVolumeLeft = null,
        double? MasterVolumeRight = null,
        bool MasterFadersLinked = true,
        bool HasLoopRange = false);

// From this step on the song plays at Bpm.
public sealed record TempoChangeDocument(int Step, double Bpm);

// A named place in the song.
public sealed record MarkerDocument(int Step, string Name);

public sealed record SongArrangeBlockDocument(
    int Row,
    int Column,
    int RowSpan,
    int ColumnSpan,
    string Label,
    string Detail,
    string Accent);

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
    List<MidiNoteEventDocument>? MidiNoteEvents = null,
    PatternTrackType TrackType = PatternTrackType.MidiChannel,
    string? Source = null,
    double Volume = 0.75,
    bool IsRecordArmed = false,
    string? TrackColor = null,
    string? PluginFilePath = null,
    string? AudioClipPath = null,
    string? InstrumentIcon = null,
    List<EffectDocument>? Effects = null,
    byte[]? PluginState = null,
    byte[]? PluginEditorState = null,
    string? SoundName = null,
    List<MidiControllerEventDocument>? MidiControllerEvents = null,
    double Pan = 0,
    List<AutomationPointDocument>? VolumeAutomation = null,
    List<AutomationPointDocument>? PanAutomation = null,
    Guid? BusId = null,
    List<SendDocument>? Sends = null,
    int AudioClipStartStep = 0);

// How much of a track goes to a bus (see PatternTrackViewModel.Sends).
public sealed record SendDocument(Guid Bus, double Level);

// A point of a track's automation (see AutomationPoint).
public sealed record AutomationPointDocument(double Step, double Value);

// A controller recorded on a track (see MidiControllerEventViewModel).
public sealed record MidiControllerEventDocument(double Step, int Controller, double Value);

// One insert effect of a track: its name (SonVerb, SonDelay...), whether it is on, and its knobs in order.
public sealed record EffectDocument(
    string Name,
    bool IsEnabled,
    List<double> Values,
    string? Preset = null,
    bool PresetModified = false,
    string? PluginPath = null,
    byte[]? PluginState = null,
    byte[]? PluginEditorState = null,
    Guid? PluginInstance = null);

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
    int Velocity,
    double StartOffset = 0,
    double LengthFraction = 0);

public sealed record ArrangeRowDocument(
    string TrackName,
    string Instrument,
    List<ArrangeCellDocument> Cells);

public sealed record ArrangeCellDocument(
    string Label,
    string Detail,
    string Accent);
