using Sonator.Services;

namespace Sonator.ViewModels;

// An audio clip on the timeline, in its section: the track of the pattern it belongs to, where that section starts
// on the timeline, and its wave. A section played again shows its clip again, fainter.
public sealed class TimelineAudioClipViewModel
{
    public TimelineAudioClipViewModel(PatternTrackViewModel sourceTrack, PatternViewModel? pattern, int sectionStartStep,
        AudioWaveformSummary waveform, double clipTempo, double songTempo, bool isRepeat)
    {
        SourceTrack = sourceTrack;
        Pattern = pattern;
        SectionStartStep = sectionStartStep;
        AudioWaveform = waveform;
        AudioLengthSteps = waveform.DurationSeconds * clipTempo / 60.0 * 4;
        AudioSeconds = songTempo > 0 ? AudioLengthSteps * 15 / songTempo : waveform.DurationSeconds;
        IsRepeat = isRepeat;
    }

    // The pattern's track whose clip this is.
    public PatternTrackViewModel SourceTrack { get; }

    public PatternViewModel? Pattern { get; }

    // Where the clip's section starts on the timeline, in steps.
    public int SectionStartStep { get; }

    // Where the clip starts on the timeline, in steps.
    public int StartStep => SectionStartStep + SourceTrack.AudioClipStartStep;

    public double AudioLengthSteps { get; }

    // How long it plays, in seconds: stretched to the song's tempo when it follows it.
    public double AudioSeconds { get; }

    public AudioWaveformSummary AudioWaveform { get; }

    public bool IsRepeat { get; }
}
