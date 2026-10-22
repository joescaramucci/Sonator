namespace Sonator.ViewModels;

// Position and length helpers for the timeline. A bar holds stepsPerBar sixteenth-note steps: 16 in 4/4.
internal static class TimelineFormatting
{
    private const int DefaultStepsPerTrack = 16;

    // Formats an absolute step as "bar.beat", both counted from 1 as musicians do: "001.1", then "001.2".
    // The beat takes two digits when a bar has more than nine beats.
    public static string FormatBarBeat(int stepIndex, int stepsPerBar = 16, int stepsPerBeat = 4)
    {
        var bar = stepIndex / stepsPerBar + 1;
        var beat = stepIndex % stepsPerBar / stepsPerBeat + 1;
        var beatsPerBar = stepsPerBar / stepsPerBeat;

        return beatsPerBar > 9 ? $"{bar:D3}.{beat:D2}" : $"{bar:D3}.{beat}";
    }

    // A pattern lasts as many bars as its longest track, and at least one bar.
    public static int PatternBarCount(PatternViewModel pattern, int stepsPerBar = 16)
    {
        var stepCount = pattern.Tracks
            .Select(track => track.Steps.Count)
            .DefaultIfEmpty(DefaultStepsPerTrack)
            .Max();
        return Math.Max(1, (stepCount + stepsPerBar - 1) / stepsPerBar);
    }
}
