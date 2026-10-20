namespace Sonator.ViewModels;

// Converts a playback step into the elapsed time shown by the transport.
internal static class TransportClock
{
    private const double FallbackTempoBpm = 1;
    private const int StepsPerBeat = 4;

    // Uses the same fallback as the sequencer engine for tempos that are zero, negative, or not finite.
    public static string FormatPosition(int stepIndex, double tempoBpm)
    {
        var tempo = !double.IsFinite(tempoBpm) || tempoBpm <= 0 ? FallbackTempoBpm : tempoBpm;
        var seconds = stepIndex * 60.0 / tempo / StepsPerBeat;
        return TimeSpan.FromSeconds(seconds).ToString(@"mm\:ss\.ff");
    }
}
