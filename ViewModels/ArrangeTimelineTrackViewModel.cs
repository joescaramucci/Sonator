using System.Collections.ObjectModel;
using Sonator.Models;

namespace Sonator.ViewModels;

public sealed class ArrangeTimelineTrackViewModel
{
    public ArrangeTimelineTrackViewModel(
        PatternTrackViewModel sourceTrack,
        ObservableCollection<ArrangeTimelineStepViewModel> steps)
    {
        SourceTrack = sourceTrack;
        Steps = steps;
    }

    public PatternTrackViewModel SourceTrack { get; }

    public string Name => SourceTrack.Name;

    public string DisplayTrackType => SourceTrack.DisplayTrackType;

    public string RoutingDisplay => SourceTrack.RoutingDisplay;

    public PatternTrackType TrackType => SourceTrack.TrackType;

    public ObservableCollection<ArrangeTimelineStepViewModel> Steps { get; }
}
