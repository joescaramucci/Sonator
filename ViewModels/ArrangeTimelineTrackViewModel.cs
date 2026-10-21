using System.Collections.ObjectModel;
using Sonator.Models;
using Sonator.Services;

namespace Sonator.ViewModels;

public sealed class ArrangeTimelineTrackViewModel
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

    // The clip's length in sixteenth-note steps at the song tempo.
    public double AudioLengthSteps { get; }
}
