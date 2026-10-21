namespace Sonator.Services;

// The audio backend and device that plug-in audio uses.
// AsioBufferFrames and AsioSampleRate are used only with ASIO; 0 keeps the driver's own choice.
public sealed record AudioOutputSelection(
    bool UseAsio,
    string? WasapiDeviceId,
    string? AsioDriverClsid,
    int AsioBufferFrames = 0,
    double AsioSampleRate = 0.0)
{
    public const string AsioBackendName = "ASIO";

    public static AudioOutputSelection FromSettings(AppSettings settings)
    {
        return new AudioOutputSelection(
            UseAsio: string.Equals(settings.AudioBackend, AsioBackendName, StringComparison.Ordinal),
            WasapiDeviceId: settings.AudioOutputDeviceId,
            AsioDriverClsid: settings.AsioDriverClsid,
            AsioBufferFrames: settings.AsioBufferFrames ?? 0,
            AsioSampleRate: settings.AsioSampleRate ?? 0.0);
    }
}
