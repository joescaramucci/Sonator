using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Sonator.Models;

namespace Sonator.Services;

public sealed class TrackClipboardService
{
    private readonly string _clipboardPath;
    private readonly JsonSerializerOptions _options;

    public TrackClipboardService()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        _clipboardPath = Path.Combine(appData, "Sonator", "track-clipboard.json");
        _options = new JsonSerializerOptions
        {
            WriteIndented = true
        };
        _options.Converters.Add(new JsonStringEnumConverter());
    }

    public void Save(TrackDocument trackDocument)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_clipboardPath)!);
        var json = JsonSerializer.Serialize(trackDocument, _options);
        File.WriteAllText(_clipboardPath, json);
    }

    public TrackDocument? Load()
    {
        if (!File.Exists(_clipboardPath))
        {
            return null;
        }

        var json = File.ReadAllText(_clipboardPath);
        return JsonSerializer.Deserialize<TrackDocument>(json, _options);
    }
}
