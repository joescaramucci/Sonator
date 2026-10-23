using System.IO;
using System.Text.Json;

namespace Sonator.Services.Sounds;

// The sounds marked as favorites in the sound browser, kept by their preset or groove file, in
// %AppData%\Sonator\favorite-sounds.json.
public sealed class SoundFavorites
{
    private readonly string _path;
    private readonly HashSet<string> _files;

    public SoundFavorites(string? path = null)
    {
        _path = path ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Sonator", "favorite-sounds.json");
        _files = new HashSet<string>(Read(_path), StringComparer.OrdinalIgnoreCase);
    }

    public int Count => _files.Count;

    public bool Contains(SoundEntry sound) => _files.Contains(sound.PresetPath);

    // Marks or unmarks the sound, and saves the list at once.
    public void Set(SoundEntry sound, bool favorite)
    {
        var changed = favorite ? _files.Add(sound.PresetPath) : _files.Remove(sound.PresetPath);
        if (!changed)
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllText(_path, JsonSerializer.Serialize(_files.Order(StringComparer.OrdinalIgnoreCase), new JsonSerializerOptions { WriteIndented = true }));
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
