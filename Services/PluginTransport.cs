namespace Sonator.Services;

// The song's transport as the plug-ins read it with each block of sound (VST3's process context): the tempo in
// BPM, the time signature, where the song is in quarter notes from its start, whether it plays, and whether it
// has just jumped there (a start, a loop going back, a seek) rather than moved on by itself.
public readonly record struct PluginTransport(double Tempo, int Numerator, int Denominator, double QuarterNotes, bool Playing, bool Jump)
{
    // Hands the transport to the native audio engine. Without the bridge (tests, a missing DLL) nothing happens.
    public static void Send(PluginTransport transport)
    {
        try
        {
            Vst3.Vst3HostInterop.VstHostBridge_SetTransport(
                transport.Tempo,
                transport.Numerator,
                transport.Denominator,
                transport.QuarterNotes,
                transport.Playing ? 1 : 0,
                transport.Jump ? 1 : 0);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
        }
    }
}
