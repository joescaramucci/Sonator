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

    // An audio track's lane shows its clip, not steps.
    public bool HasStepCells => TrackType != PatternTrackType.AudioInput;

    // The clip drawn on an audio track, or null when the track has no clip.
    public AudioWaveformSummary? AudioWaveform { get; }

    public string AudioPathData => AudioWaveform?.PathData ?? string.Empty;

    // The track's blocks of notes along the timeline, each pattern's own where the pattern plays.
    public IReadOnlyList<TimelineBlockViewModel> Blocks { get; init; } = Array.Empty<TimelineBlockViewModel>();

    // The clip's length in sixteenth-note steps at the song tempo.
    public double AudioLengthSteps { get; }

    // Where the clip starts, in sixteenth-note steps.
    public int AudioStartStep => SourceTrack.AudioClipStartStep;

    public bool HasAudioClip => AudioWaveform is not null;
}
