namespace Sonator.ViewModels;

// Converts a playback step into the elapsed time shown by the transport, as hours, minutes and seconds.
internal static class TransportClock
{
    private const double FallbackTempoBpm = 1;
    private const int StepsPerBeat = 4;

    // The clock at the start of the song.
    public const string Zero = "00:00:00";

    // Uses the same fallback as the sequencer engine for tempos that are zero, negative, or not finite.
    public static string FormatPosition(int stepIndex, double tempoBpm)
    {
        var tempo = !double.IsFinite(tempoBpm) || tempoBpm <= 0 ? FallbackTempoBpm : tempoBpm;
        var elapsed = TimeSpan.FromSeconds(stepIndex * 60.0 / tempo / StepsPerBeat);
        return $"{(int)elapsed.TotalHours:D2}:{elapsed.Minutes:D2}:{elapsed.Seconds:D2}";
    }
}
