using System.IO;
using System.Text.Json;

namespace Sonator.Services;

public sealed class AppSettings
{
    public string? VstPluginsPath { get; set; }

    // Endpoint ID of the audio output used by plug-in audio. Null means the Windows default device.
    public string? AudioOutputDeviceId { get; set; }

    // "ASIO" selects the ASIO driver below. Anything else, including null, uses WASAPI.
    public string? AudioBackend { get; set; }

    // CLSID of the ASIO driver, as listed under HKLM\SOFTWARE\ASIO.
    public string? AsioDriverClsid { get; set; }

    // Buffer size in frames and sample rate for ASIO. Null means the driver's own choice.
    public int? AsioBufferFrames { get; set; }

    public double? AsioSampleRate { get; set; }

    // Name of the MIDI input the player last chose, "Off" included. Null means no choice yet.
    public string? MidiInputDeviceName { get; set; }
}

public sealed class AppSettingsService
{
    private readonly string _settingsFilePath;

    public AppSettingsService()
    {
        var folder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Sonator");
        Directory.CreateDirectory(folder);
        _settingsFilePath = Path.Combine(folder, "settings.json");
    }

    public AppSettings Load()
    {
        try
        {
            if (!File.Exists(_settingsFilePath))
            {
                return new AppSettings();
            }

            var json = File.ReadAllText(_settingsFilePath);
            return JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return new AppSettings();
        }
    }

    public void Save(AppSettings settings)
    {
        try
        {
            var json = JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(_settingsFilePath, json);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Non-fatal: settings simply won't persist for this session.
        }
    }
}
