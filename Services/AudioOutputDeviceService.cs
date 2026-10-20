using System.Text;
using Sonator.Services.Vst3;

namespace Sonator.Services;

public sealed record AudioOutputDevice(string Id, string Name);

// Lists the active Windows audio output devices through the native bridge.
// If the bridge is missing or too old to export the call, the list is empty.
public sealed class AudioOutputDeviceService
{
    private const int MaxAttempts = 3;

    public IReadOnlyList<AudioOutputDevice> GetOutputDevices()
    {
        try
        {
            for (var attempt = 0; attempt < MaxAttempts; attempt++)
            {
                var required = Vst3HostInterop.VstHostBridge_ListAudioOutputs(null, 0);
                if (required <= 0)
                {
                    break;
                }

                // A device can be added between the two calls, so retry if the buffer turns out too small.
                var buffer = new StringBuilder(required);
                var written = Vst3HostInterop.VstHostBridge_ListAudioOutputs(buffer, buffer.Capacity);
                if (written > 0 && written <= buffer.Capacity)
                {
                    return Parse(buffer.ToString());
                }
            }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            // The bridge is missing or does not match this build: no devices can be listed.
        }

        return Array.Empty<AudioOutputDevice>();
    }

    // Each line is "<id>\t<name>". Lines that do not match this shape are ignored.
    internal static IReadOnlyList<AudioOutputDevice> Parse(string listing)
    {
        return listing
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.TrimEnd('\0', '\r').Split('\t'))
            .Where(parts => parts.Length == 2 && parts[0].Length > 0 && parts[1].Length > 0)
            .Select(parts => new AudioOutputDevice(parts[0], parts[1]))
            .ToList();
    }
}
