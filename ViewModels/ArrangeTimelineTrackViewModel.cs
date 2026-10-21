using System.Collections.ObjectModel;
using Sonator.Models;
using Sonator.Services;

namespace Sonator.ViewModels;

public sealed class ArrangeTimelineTrackViewModel : ViewModelBase
{
    public ArrangeTimelineTrackViewModel(
        PatternTrackViewModel sourceTrack,
        ObservableCollection<ArrangeTimelineStepViewModel> steps,
        AudioWaveformSummary? audioWaveform = null,
        double tempoBpm = 120)
    {
        SourceTrack = sourceTrack;
        Steps = steps;
        AudioWaveform = audioWaveform;
        AudioLengthSteps = audioWaveform is null ? 0 : audioWaveform.DurationSeconds * tempoBpm / 60.0 * 4;
    }

    public PatternTrackViewModel SourceTrack { get; }

    public string Name => SourceTrack.Name;

    public string DisplayTrackType => SourceTrack.DisplayTrackType;

    public string RoutingDisplay => SourceTrack.RoutingDisplay;

    public PatternTrackType TrackType => SourceTrack.TrackType;

    public ObservableCollection<ArrangeTimelineStepViewModel> Steps { get; }

    // The clip drawn on an audio track, or null when the track has no clip.
    public AudioWaveformSummary? AudioWaveform { get; }

    public string AudioPathData => AudioWaveform?.PathData ?? string.Empty;

    // The recorded notes of the track as one block, in sixteenth steps from the start of the pattern.
    public bool HasBlock => BlockLengthSteps > 0;

    // True while this track's block is the one being worked on: it is drawn highlighted.
    private bool _isBlockSelected;

    public bool IsBlockSelected
    {
        get => _isBlockSelected;
        set => SetProperty(ref _isBlockSelected, value);
    }

    public int BlockStartStep => SourceTrack.MidiNoteEvents.Count == 0
        ? 0
        : SourceTrack.MidiNoteEvents.Min(note => note.StepIndex);

    public double BlockLengthSteps => SourceTrack.MidiNoteEvents.Count == 0
        ? 0
        : SourceTrack.MidiNoteEvents.Max(note => note.StepIndex + Math.Max(1, note.DurationSteps)) - BlockStartStep;

    // The clip's length in sixteenth-note steps at the song tempo.
    public double AudioLengthSteps { get; }
}
