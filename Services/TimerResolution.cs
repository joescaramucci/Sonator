using System.Runtime.InteropServices;

namespace Sonator.Services;

// Windows wakes sleeping threads on a ~15.6 ms tick by default, which makes Task.Delay
// too coarse for step timing. Raising the resolution to 1 ms while playback runs keeps
// step jitter small. The request is reference-counted by Windows, so each instance must be disposed.
internal sealed class TimerResolution : IDisposable
{
    private const uint PeriodMilliseconds = 1;
    private bool _disposed;

    private TimerResolution()
    {
        timeBeginPeriod(PeriodMilliseconds);
    }

    public static TimerResolution Request() => new();

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        timeEndPeriod(PeriodMilliseconds);
    }

    [DllImport("winmm.dll")]
    private static extern uint timeBeginPeriod(uint uPeriod);

    [DllImport("winmm.dll")]
    private static extern uint timeEndPeriod(uint uPeriod);
}
