using System.Diagnostics;
using System.Runtime.InteropServices;
using Sonator.Services.Vst3;

namespace Sonator.Services;

// One reading of how hard the PC is working. Loads are shares from 0 to 1.
// DspPeak and DspAverage are null while no plug-in is playing audio.
public sealed record SystemHealthSample(
    double SystemCpu,
    double SonatorCpu,
    long SonatorMemoryBytes,
    double SystemMemoryLoad,
    long SystemMemoryUsedBytes,
    long SystemMemoryTotalBytes,
    double? DspPeak,
    double? DspAverage,
    long Dropouts);

// Samples the system and Sonator's CPU use, memory, and the plug-ins' audio load.
// Each reading covers the time since the previous one.
public sealed class SystemHealthMonitor
{
    private readonly Process _process = Process.GetCurrentProcess();
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private long _lastIdle;
    private long _lastKernel;
    private long _lastUser;
    private TimeSpan _lastProcessCpu;
    private TimeSpan _lastWall;

    public SystemHealthMonitor()
    {
        GetSystemTimes(out _lastIdle, out _lastKernel, out _lastUser);
        _lastProcessCpu = _process.TotalProcessorTime;
        _lastWall = _clock.Elapsed;
    }

    public SystemHealthSample Sample()
    {
        GetSystemTimes(out var idle, out var kernel, out var user);
        var systemCpu = CpuShare(idle - _lastIdle, kernel - _lastKernel, user - _lastUser);
        (_lastIdle, _lastKernel, _lastUser) = (idle, kernel, user);

        _process.Refresh();
        var processCpu = _process.TotalProcessorTime;
        var wall = _clock.Elapsed;
        var sonatorCpu = ProcessShare(processCpu - _lastProcessCpu, wall - _lastWall, Environment.ProcessorCount);
        (_lastProcessCpu, _lastWall) = (processCpu, wall);

        var memory = new MemoryStatusEx { Length = (uint)Marshal.SizeOf<MemoryStatusEx>() };
        var hasMemory = GlobalMemoryStatusEx(ref memory);
        var systemMemoryLoad = hasMemory ? memory.MemoryLoad / 100.0 : 0;
        var totalMemory = hasMemory ? (long)memory.TotalPhys : 0;
        var usedMemory = hasMemory ? (long)(memory.TotalPhys - memory.AvailPhys) : 0;

        double? dspPeak = null;
        double? dspAverage = null;
        long dropouts = 0;
        try
        {
            if (Vst3HostInterop.VstHostBridge_GetDspLoad(out var peak, out var average, out dropouts) != 0)
            {
                (dspPeak, dspAverage) = (peak, average);
            }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            // Without the native bridge there is no audio load to show.
        }

        return new SystemHealthSample(systemCpu, sonatorCpu, _process.WorkingSet64, systemMemoryLoad, usedMemory, totalMemory, dspPeak, dspAverage, dropouts);
    }

    public static void ResetDropouts()
    {
        try
        {
            Vst3HostInterop.VstHostBridge_ResetDropouts();
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
        }
    }

    // GetSystemTimes counts idle time inside kernel time, so busy time is kernel + user - idle.
    internal static double CpuShare(long idleDelta, long kernelDelta, long userDelta)
    {
        var total = kernelDelta + userDelta;
        return total <= 0 ? 0 : Math.Clamp((double)(total - idleDelta) / total, 0, 1);
    }

    // A process can use every core, so its CPU time is shared out over the cores' wall time.
    internal static double ProcessShare(TimeSpan cpuDelta, TimeSpan wallDelta, int cores)
    {
        var available = wallDelta.TotalSeconds * Math.Max(1, cores);
        return available <= 0 ? 0 : Math.Clamp(cpuDelta.TotalSeconds / available, 0, 1);
    }

    [DllImport("kernel32.dll")]
    private static extern bool GetSystemTimes(out long idleTime, out long kernelTime, out long userTime);

    [DllImport("kernel32.dll")]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx buffer);

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatusEx
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhys;
        public ulong AvailPhys;
        public ulong TotalPageFile;
        public ulong AvailPageFile;
        public ulong TotalVirtual;
        public ulong AvailVirtual;
        public ulong AvailExtendedVirtual;
    }
}
