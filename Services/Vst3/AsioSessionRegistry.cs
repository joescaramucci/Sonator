namespace Sonator.Services.Vst3;

// Remembers the plug-in module whose ASIO session is running, so the main window can measure its loopback latency
// without opening a plug-in editor first.
internal static class AsioSessionRegistry
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
