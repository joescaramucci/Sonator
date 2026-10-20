using Sonator.Models;

namespace Sonator.ViewModels;

// Defaults offered when a track is added to the selected pattern.
internal static class NewTrackDefaults
{
    private const int MinChannel = 1;
    private const int MaxChannel = 16;

    // Next name after the highest "Track N" already used. Other names are ignored.
    public static string NextTrackName(IEnumerable<string> trackNames)
    {
        var nextIndex = trackNames
            .Select(name => name.StartsWith("Track ", StringComparison.OrdinalIgnoreCase) &&
                            int.TryParse(name["Track ".Length..], out var index)
                ? index
                : 0)
            .DefaultIfEmpty(0)
            .Max() + 1;
        return $"Track {nextIndex}";
    }

    // Channel after the highest one in use. Once channel 16 is reached, the lowest free channel is used.
    public static int NextChannel(IEnumerable<int> usedChannels)
    {
        var channels = usedChannels.ToList();
        var highestChannel = channels.DefaultIfEmpty(0).Max();
        if (highestChannel < MaxChannel)
        {
            return highestChannel + 1;
        }

        var freeChannel = Enumerable.Range(MinChannel, MaxChannel)
            .FirstOrDefault(candidate => !channels.Contains(candidate));
        return freeChannel == 0 ? MaxChannel : freeChannel;
    }

    public static string InstrumentName(PatternTrackType trackType)
    {
        return trackType switch
        {
            PatternTrackType.VstInstrument => "VST Instrument",
            PatternTrackType.AudioInput => "Audio Input",
            _ => "MIDI Channel"
        };
    }

    public static string SourceName(PatternTrackType trackType)
    {
        return trackType switch
        {
            PatternTrackType.VstInstrument => "VSTi",
            PatternTrackType.AudioInput => "Microphone / External",
            _ => string.Empty
        };
    }
}
