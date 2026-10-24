using System.IO;
using System.Text.Json;

namespace Sonator.Services.Sounds;

// The sounds last loaded on a track from the sound browser, the latest first, kept by their preset or groove file
// in %AppData%\Sonator\recent-sounds.json.
public sealed class RecentSounds
{
    public const int Most = 50;

    private readonly string _path;
    private readonly List<string> _files;

    public RecentSounds(string? path = null)
    {
        _path = path ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Sonator", "recent-sounds.json");
        _files = Read(_path).Take(Most).ToList();
    }

    public int Count => _files.Count;

    // The sounds given that were used, the latest first.
    public IEnumerable<SoundEntry> Of(IEnumerable<SoundEntry> sounds)
    {
        var byFile = sounds
            .GroupBy(sound => sound.PresetPath, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
        return _files.Where(byFile.ContainsKey).Select(file => byFile[file]);
    }

    // Puts the sound at the top, and saves the list at once.
    public void Used(SoundEntry sound)
    {
        _files.RemoveAll(file => string.Equals(file, sound.PresetPath, StringComparison.OrdinalIgnoreCase));
        _files.Insert(0, sound.PresetPath);
        if (_files.Count > Most)
        {
            _files.RemoveRange(Most, _files.Count - Most);
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllText(_path, JsonSerializer.Serialize(_files, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Kept for this session only.
        }
    }

    private static IEnumerable<string> Read(string path)
    {
        try
        {
            return File.Exists(path)
                ? JsonSerializer.Deserialize<List<string>>(File.ReadAllText(path)) ?? new List<string>()
                : Enumerable.Empty<string>();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return Enumerable.Empty<string>();
        }
    }
}
