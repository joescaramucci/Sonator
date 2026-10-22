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

    // Raised after the plug-in changes, on the thread that changed it.
    public static event Action? CurrentChanged;

    public static void Set(Vst3ModuleHandle handle)
    {
        lock (Sync)
        {
            _current = handle;
        }

        CurrentChanged?.Invoke();
    }

    public static void Clear(Vst3ModuleHandle handle)
    {
        lock (Sync)
        {
            if (!ReferenceEquals(_current, handle))
            {
                return;
            }

            _current = null;
        }

        CurrentChanged?.Invoke();
    }
}
