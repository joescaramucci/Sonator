using System.Collections.ObjectModel;
using System.ComponentModel;
using static Sonator.ViewModels.TimelineFormatting;

namespace Sonator.ViewModels;

// ---- Channels: a track is the same in every pattern ----
// The patterns are the song's sections (intro, verse, chorus...): the track in the same place in each of them is one
// channel, with one plug-in, one rack of effects, one drum kit and one strip in the mixer. The first pattern's track
// leads its channel: the plug-in and the effects run for it, and every pattern's notes play through them. The
// channel's settings (its name, plug-in and sound, fader, pan, mute...) are kept the same in every pattern; each
// pattern keeps its own notes, clip, automation and takes.
public sealed partial class MainViewModel
{
    private volatile IReadOnlyDictionary<PatternTrackViewModel, PatternTrackViewModel> _channelOf =
        new Dictionary<PatternTrackViewModel, PatternTrackViewModel>();

    private IReadOnlyDictionary<PatternTrackViewModel, IReadOnlyList<PatternTrackViewModel>> _channelTracks =
        new Dictionary<PatternTrackViewModel, IReadOnlyList<PatternTrackViewModel>>();

    private readonly HashSet<PatternTrackViewModel> _linkedTracks = new();
    private bool _syncingChannel;

    // The track that leads a track's channel: the first pattern's track in its place. A bus, the master, a kit piece
    // or a track in no pattern leads its own. Any thread.
    public PatternTrackViewModel ChannelOf(PatternTrackViewModel track) =>
        _channelOf.TryGetValue(track, out var channel) ? channel : track;

    // Every pattern's track of a track's channel, the leading one first.
    public IReadOnlyList<PatternTrackViewModel> TracksOfChannel(PatternTrackViewModel track) =>
        _channelTracks.TryGetValue(ChannelOf(track), out var tracks) ? tracks : new[] { track };

    // Where a track is in its pattern, -1 when it is in none.
    public int TrackIndexOf(PatternTrackViewModel track)
    {
        foreach (var pattern in Patterns)
        {
            var index = pattern.Tracks.IndexOf(track);
            if (index >= 0)
            {
                return index;
            }
        }

        return -1;
    }

    // The track of a channel in a pattern: the track in the same place there; the track itself without a pattern.
    public PatternTrackViewModel PartIn(PatternViewModel? pattern, PatternTrackViewModel track)
    {
        var index = TrackIndexOf(track);
        return pattern is not null && index >= 0 && index < pattern.Tracks.Count ? pattern.Tracks[index] : track;
    }

    // ---- The pattern on the timeline, in the song ----

    // The arrangement's sections in order: each pattern played, the song step it starts at and how long it is.
    internal IReadOnlyList<(PatternViewModel Pattern, int Start, int Length)> ArrangeSections()
    {
        var sections = new List<(PatternViewModel Pattern, int Start, int Length)>();
        var start = 0;
        foreach (var pattern in GetArrangePlaybackPatterns())
        {
            var length = PatternBarCount(pattern, StepsPerBar) * StepsPerBar;
            sections.Add((pattern, start, length));
            start += length;
        }

        return sections;
    }

    // Where in the song the pattern on the timeline starts: in the arrangement, the place it is playing at, or the
    // place selected in the pattern list, or its first; 0 when a pattern plays on its own.
    public int ShownSectionStart
    {
        get
        {
            if (!IsArrangeMode || SelectedPattern is not { } shown)
            {
                return 0;
            }

            var sections = ArrangeSections();
            var at = sections.FirstOrDefault(section => CurrentStepIndex >= section.Start && CurrentStepIndex < section.Start + section.Length);
            if (ReferenceEquals(at.Pattern, shown))
            {
                return at.Start;
            }

            if (TryGetSelectedArrangePatternRange(out var startRow, out _, out _)
                && sections.FirstOrDefault(section => section.Start == startRow * StepsPerBar) is { Pattern: { } selected } place
                && ReferenceEquals(selected, shown))
            {
                return place.Start;
            }

            return sections.FirstOrDefault(section => ReferenceEquals(section.Pattern, shown)).Start;
        }
    }

    // A step of the timeline (of the pattern shown) as a step of the song.
    public int SongStepOf(int timelineStep) => timelineStep + ShownSectionStart;

    // A step of the song as a step of the pattern playing there; the step itself when a pattern plays on its own.
    internal double PatternStepOf(double songStep)
    {
        if (!IsArrangeMode)
        {
            return songStep;
        }

        foreach (var (_, start, length) in ArrangeSections())
        {
            if (songStep >= start && songStep < start + length)
            {
                return songStep - start;
            }
        }

        return songStep;
    }

    // Tells the views the pattern on the timeline moved in the song.
    private void RaiseShownSectionChanged()
    {
        RaisePropertyChanged(nameof(ShownSectionStart));
        RaisePropertyChanged(nameof(TimelineLoopStartStep));
        RaisePropertyChanged(nameof(TimelineLoopEndStep));
        RaisePropertyChanged(nameof(RulerFlags));
    }

    // The loop's range on the timeline of the pattern shown.
    public int TimelineLoopStartStep => LoopStartStep - ShownSectionStart;

    public int TimelineLoopEndStep => LoopEndStep - ShownSectionStart;

    // True when the track is in one of the song's patterns.
    public bool IsInSong(PatternTrackViewModel track) => Patterns.Any(pattern => pattern.Tracks.Contains(track));

    // Makes every pattern have the same tracks, in the same places, and links each place's tracks into one channel,
    // the first pattern's leading. Called whenever the patterns or their tracks change.
    internal void LinkChannels()
    {
        foreach (var track in _linkedTracks)
        {
            track.PropertyChanged -= OnChannelTrackChanged;
        }

        _linkedTracks.Clear();
        if (Patterns.Count == 0)
        {
            _channelOf = new Dictionary<PatternTrackViewModel, PatternTrackViewModel>();
            _channelTracks = new Dictionary<PatternTrackViewModel, IReadOnlyList<PatternTrackViewModel>>();
            return;
        }

        // A place a pattern lacks gets a track of that channel there, with no notes.
        var places = Patterns.Max(pattern => pattern.Tracks.Count);
        var leads = new List<PatternTrackViewModel>();
        for (var index = 0; index < places; index++)
        {
            leads.Add(Patterns.First(pattern => index < pattern.Tracks.Count).Tracks[index]);
        }

        foreach (var pattern in Patterns)
        {
            while (pattern.Tracks.Count < places)
            {
                pattern.Tracks.Add(EmptyPartOf(leads[pattern.Tracks.Count], StepCountOf(pattern)));
            }
        }

        var channelOf = new Dictionary<PatternTrackViewModel, PatternTrackViewModel>();
        var channelTracks = new Dictionary<PatternTrackViewModel, IReadOnlyList<PatternTrackViewModel>>();
        _syncingChannel = true;
        try
        {
            for (var index = 0; index < places; index++)
            {
                var lead = Patterns[0].Tracks[index];
                var tracks = Patterns.Select(pattern => pattern.Tracks[index]).ToList();
                foreach (var track in tracks)
                {
                    track.JoinChannel(lead);
                    channelOf[track] = lead;
                    if (_linkedTracks.Add(track))
                    {
                        track.PropertyChanged += OnChannelTrackChanged;
                    }
                }

                channelTracks[lead] = tracks;
            }
        }
        finally
        {
            _syncingChannel = false;
        }

        _channelOf = channelOf;
        _channelTracks = channelTracks;
    }

    // A setting of the channel changed on one pattern's track: every other pattern's track of the channel takes it.
    private void OnChannelTrackChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_syncingChannel || sender is not PatternTrackViewModel track || e.PropertyName is null
            || !PatternTrackViewModel.ChannelProperties.Contains(e.PropertyName))
        {
            return;
        }

        _syncingChannel = true;
        try
        {
            foreach (var other in TracksOfChannel(track))
            {
                if (!ReferenceEquals(other, track))
                {
                    other.CopyChannelProperty(track, e.PropertyName);
                }
            }
        }
        finally
        {
            _syncingChannel = false;
        }
    }

    // How many steps a pattern's tracks have: its length.
    private int StepCountOf(PatternViewModel pattern) =>
        pattern.Tracks.Select(track => track.Steps.Count).DefaultIfEmpty(StepsPerBar).Max();

    // A track of a channel for a pattern that has none in its place: the channel's settings, no notes.
    private static PatternTrackViewModel EmptyPartOf(PatternTrackViewModel lead, int steps)
    {
        var track = new PatternTrackViewModel(
            lead.Name,
            lead.Instrument,
            lead.Channel,
            lead.NoteNumber,
            new ObservableCollection<StepViewModel>(Enumerable.Range(0, Math.Max(1, steps)).Select(index => new StepViewModel(index, false))),
            lead.ProgramNumber,
            trackType: lead.TrackType,
            source: lead.Source,
            volume: lead.Volume,
            trackColorBrush: lead.TrackColorBrush);
        track.JoinChannel(lead);
        return track;
    }

    // A new pattern for the song: every channel in its place, no notes, bars long.
    private PatternViewModel NewSectionPattern(string name, string summary, string slot, int bars)
    {
        var tracks = new ObservableCollection<PatternTrackViewModel>(
            (Patterns.FirstOrDefault()?.Tracks ?? new ObservableCollection<PatternTrackViewModel>())
                .Select(lead => EmptyPartOf(lead, bars * StepsPerBar)));
        return tracks.Count > 0
            ? new PatternViewModel(name, summary, slot, tracks)
            : PatternFactory.CreatePattern(name, summary, slot);
    }
}
