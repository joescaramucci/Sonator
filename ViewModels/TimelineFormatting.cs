namespace Sonator.ViewModels;

// Position and length helpers for the timeline. A bar holds stepsPerBar sixteenth-note steps: 16 in 4/4.
internal static class TimelineFormatting
{
    private const int DefaultStepsPerTrack = 16;

    // Formats an absolute step as "bar quarter eighth sixteenth", each field zero-based.
    public static string FormatSubdivisionPosition(int stepIndex, int stepsPerBar = 16)
    {
        var bar = stepIndex / stepsPerBar;
        var remainder = stepIndex % stepsPerBar;
        var quarter = remainder / 4;
        var eighth = (remainder % 4) / 2;
        var sixteenth = remainder % 2;

        return $"{bar:D2} {quarter:D2} {eighth:D2} {sixteenth:D2}";
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
