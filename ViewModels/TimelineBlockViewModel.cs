using System.Windows.Media;

namespace Sonator.ViewModels;

// A block of notes as the timeline draws it: the track it belongs to, the block in that track's pattern, and
// where the pattern sits on the timeline. A block of a pattern played again later is a repeat: it is drawn
// dimmer, and working on it works on the pattern.
public sealed class TimelineBlockViewModel : ViewModelBase
{
    public TimelineBlockViewModel(PatternTrackViewModel track, NoteBlock block, int offsetSteps, bool isRepeat)
    {
        Track = track;
        Block = block;
        OffsetSteps = offsetSteps;
        IsRepeat = isRepeat;
    }

    public PatternTrackViewModel Track { get; }

    public NoteBlock Block { get; }

    public int OffsetSteps { get; }

    public bool IsRepeat { get; }

    public int StartStep => OffsetSteps + Block.StartStep;

    public double LengthSteps => Block.LengthSteps;

    public SolidColorBrush TrackColorBrush => Track.TrackColorBrush;

    private bool _isSelected;

    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }
}
