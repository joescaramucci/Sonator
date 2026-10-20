namespace Sonator.Services;

// The audio backend and device that plug-in audio uses.
public sealed record AudioOutputSelection(bool UseAsio, string? WasapiDeviceId, string? AsioDriverClsid)
{
    public const string AsioBackendName = "ASIO";

    public static AudioOutputSelection FromSettings(AppSettings settings)
    {
        return new AudioOutputSelection(
            UseAsio: string.Equals(settings.AudioBackend, AsioBackendName, StringComparison.Ordinal),
            WasapiDeviceId: settings.AudioOutputDeviceId,
            AsioDriverClsid: settings.AsioDriverClsid);
    }
}
