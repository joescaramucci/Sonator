namespace Sonator.Services.Vst3;

// Remembers the plug-in that receives notes from the MIDI keyboard: the one whose editor started real-time audio.
internal static class LiveInstrumentRegistry
{
    private static readonly object Sync = new();
    private static Vst3ModuleHandle? _current;

    public static Vst3ModuleHandle? Current
    {
        get
        {
            lock (Sync)
            {
                return _current;
            }
        }
    }

    public static void Set(Vst3ModuleHandle handle)
    {
        lock (Sync)
        {
            _current = handle;
        }
    }

    public static void Clear(Vst3ModuleHandle handle)
    {
        lock (Sync)
        {
            if (ReferenceEquals(_current, handle))
            {
                _current = null;
            }
        }
    }
}
