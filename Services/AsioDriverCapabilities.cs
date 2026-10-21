using System.Runtime.InteropServices;
using Sonator.Services.Vst3;

namespace Sonator.Services;

// What an ASIO driver accepts: buffer sizes and sample rates.
public sealed record AsioDriverCapabilities(
    int MinBuffer,
    int MaxBuffer,
    int PreferredBuffer,
    int Granularity,
    double CurrentSampleRate,
    IReadOnlyList<double> SampleRates)
{
    public IReadOnlyList<int> BufferSizes => BufferSizeChoices(MinBuffer, MaxBuffer, PreferredBuffer, Granularity);

    // Granularity follows the ASIO convention: -1 means powers of two, 0 means a single fixed size,
    // and any other value is the step between sizes. The preferred size is always offered.
    public static IReadOnlyList<int> BufferSizeChoices(int min, int max, int preferred, int granularity)
    {
        if (min <= 0 || max < min)
        {
            return Array.Empty<int>();
        }

        var sizes = new SortedSet<int>();
        if (granularity == 0)
        {
            sizes.Add(min);
        }
        else if (granularity < 0)
        {
            for (var size = 1; size <= max; size *= 2)
            {
                if (size >= min)
                {
                    sizes.Add(size);
                }
            }
        }
        else
        {
            for (var size = min; size <= max; size += granularity)
            {
                sizes.Add(size);
            }
        }

        if (preferred >= min && preferred <= max)
        {
            sizes.Add(preferred);
        }

        return sizes.ToList();
    }
}

public static class AsioDriverCapabilityReader
{
    private const int RateCapacity = 16;

    // Reads the capabilities of the driver with the given CLSID. Returns null and an error message on failure,
    // for example when an audio session already holds the driver.
    public static AsioDriverCapabilities? TryRead(string driverClsid, out string? error)
    {
        var rates = new double[RateCapacity];
        var result = Vst3HostInterop.VstHostBridge_GetAsioCapabilities(
            driverClsid,
            out var min,
            out var max,
            out var preferred,
            out var granularity,
            out var currentRate,
            rates,
            RateCapacity,
            out var rateCount);

        if (result == 0)
        {
            error = ReadLastError();
            return null;
        }

        error = null;
        return new AsioDriverCapabilities(
            min,
            max,
            preferred,
            granularity,
            currentRate,
            rates.Take(rateCount).ToList());
    }

    // Opens the driver's own configuration panel. Returns false and an error message on failure.
    public static bool TryOpenControlPanel(string driverClsid, out string? error)
    {
        if (Vst3HostInterop.VstHostBridge_OpenAsioControlPanel(driverClsid) != 0)
        {
            error = null;
            return true;
        }

        error = ReadLastError();
        return false;
    }

    private static string ReadLastError()
    {
        var pointer = Vst3HostInterop.VstHostBridge_GetLastError();
        return pointer == IntPtr.Zero
            ? "Unknown ASIO driver error."
            : Marshal.PtrToStringUTF8(pointer) ?? "Unknown ASIO driver error.";
    }
}
