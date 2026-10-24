using System.IO;
using System.Text.Json;

namespace Sonator.Services.Effects;

// A named setting of an effect's knobs, in the order of its parameters and in the units the user sees.
// Factory presets come with Sonator; the others are the user's own, saved by name.
public sealed record EffectPreset(string Name, IReadOnlyList<double> Values, bool IsFactory)
{
    public string DisplayName => IsFactory ? Name : $"★ {Name}";
}

public static class FactoryPresets
{
    private static readonly Dictionary<EffectKind, EffectPreset[]> Presets = new()
    {
        // Size %, Damping %, Width %, Pre-delay ms, Mix %
        [EffectKind.Verb] = new[]
        {
            Factory("Small Room", 35, 50, 80, 5, 18),
            Factory("Drum Room", 40, 60, 70, 3, 20),
            Factory("Vocal Ambience", 50, 45, 90, 15, 15),
            Factory("Plate", 70, 20, 100, 10, 25),
            Factory("Large Hall", 85, 35, 100, 25, 30),
            Factory("Cathedral", 95, 25, 100, 40, 35),
        },
        // Time ms, Feedback %, Mix %, Ping-pong, High cut Hz
        [EffectKind.Delay] = new[]
        {
            Factory("Slapback", 110, 0, 25, 0, 7000),
            Factory("Wide Doubler", 25, 0, 35, 1, 12000),
            Factory("Quarter at 120 BPM", 500, 35, 25, 0, 8000),
            Factory("Dotted Eighth at 120 BPM", 375, 40, 22, 0, 6000),
            Factory("Eighth Ping-pong at 120 BPM", 250, 40, 25, 1, 8000),
            Factory("Dub Echo", 600, 65, 30, 0, 2500),
        },
        // Threshold dB, Ratio, Attack ms, Release ms, Makeup dB, Knee dB
        [EffectKind.Compress] = new[]
        {
            Factory("Gentle Glue", -20, 2, 30, 200, 2, 6),
            Factory("Vocal", -22, 3.5, 5, 120, 5, 6),
            Factory("Punchy Drums", -18, 4, 25, 80, 4, 3),
            Factory("Bass Control", -20, 4, 10, 150, 4, 6),
            Factory("Heavy Squash", -30, 10, 1, 60, 10, 2),
            Factory("Limiting", -10, 20, 0.5, 50, 6, 0),
        },
        // Low gain dB, Low Hz, Mid gain dB, Mid Hz, Mid Q, High gain dB, High Hz
        [EffectKind.EQ] = new[]
        {
            Factory("Flat", 0, 120, 0, 1000, 1, 0, 8000),
            Factory("Warmth", 3, 150, -1, 800, 0.8, -1, 9000),
            Factory("Presence", 0, 120, 3, 3000, 1.2, 2, 10000),
            Factory("Bass Boost", 6, 90, 0, 1000, 1, 0, 8000),
            Factory("Cut Mud", 0, 120, -4, 300, 1.5, 0, 8000),
            Factory("Air", 0, 120, 0, 1000, 1, 5, 12000),
            Factory("Telephone", -15, 400, 6, 1500, 1.5, -15, 3500),
        },
        // Key (0 none, 1 C ... 12 B), Minor, Voice 1 and Voice 2 in scale steps, Level 1 %, Level 2 %, Width %, Dry %.
        // The key stays none: set the song's key after choosing one.
        [EffectKind.Harmonizer] = new[]
        {
            Factory("Third Above", 0, 0, 2, 0, 70, 60, 40, 100),
            Factory("Third and Fifth Above", 0, 0, 2, 4, 65, 55, 70, 100),
            Factory("Third Below", 0, 0, -2, 0, 70, 60, 40, 100),
            Factory("Thirds Above and Below", 0, 0, 2, -2, 60, 60, 80, 100),
            Factory("Octaves", 0, 0, 7, -7, 45, 45, 50, 100),
            Factory("Power Fifths", 0, 0, 4, -3, 55, 50, 70, 100),
        },
        // Ceiling dB, Release ms, Drive dB
        [EffectKind.Limiter] = new[]
        {
            Factory("Safety", -0.3, 50, 0),
            Factory("Brickwall", -0.1, 20, 0),
            Factory("Mastering", -1, 100, 3),
            Factory("Loud", -0.3, 80, 6),
        },
    };

    public static IReadOnlyList<EffectPreset> For(EffectKind kind) =>
        Presets.TryGetValue(kind, out var presets) ? presets : Array.Empty<EffectPreset>();

    private static EffectPreset Factory(string name, params double[] values) => new(name, values, IsFactory: true);
}

// The user's own presets, kept in one file for all projects: %AppData%\Sonator\effect-presets.json.
public sealed class EffectPresetStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly string _path;
    private Dictionary<string, List<SavedPreset>> _byEffect;

    public EffectPresetStore(string? path = null)
    {
        _path = path ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Sonator", "effect-presets.json");
        _byEffect = Load(_path);
    }

    // The factory presets first, then the user's, by name.
    public IReadOnlyList<EffectPreset> PresetsFor(EffectDefinition effect) =>
        FactoryPresets.For(effect.Kind)
            .Concat(UserPresets(effect))
            .ToList();

    public IReadOnlyList<EffectPreset> UserPresets(EffectDefinition effect) =>
        (_byEffect.TryGetValue(effect.Name, out var saved) ? saved : new List<SavedPreset>())
            .OrderBy(preset => preset.Name, StringComparer.CurrentCultureIgnoreCase)
            .Select(preset => new EffectPreset(preset.Name, preset.Values, IsFactory: false))
            .ToList();

    // True when a factory preset of this effect already has the name, so it cannot be taken.
    public static bool IsFactoryName(EffectDefinition effect, string name) =>
        FactoryPresets.For(effect.Kind).Any(preset => string.Equals(preset.Name, name.Trim(), StringComparison.CurrentCultureIgnoreCase));

    // Saves the values under the name, replacing a user preset of the same name. Returns the saved preset.
    public EffectPreset Save(EffectDefinition effect, string name, IReadOnlyList<double> values)
    {
        name = name.Trim();
        if (!_byEffect.TryGetValue(effect.Name, out var saved))
        {
            saved = new List<SavedPreset>();
            _byEffect[effect.Name] = saved;
        }

        saved.RemoveAll(preset => string.Equals(preset.Name, name, StringComparison.CurrentCultureIgnoreCase));
        saved.Add(new SavedPreset(name, values.ToList()));
        Write();
        return new EffectPreset(name, values.ToList(), IsFactory: false);
    }

    public bool Delete(EffectDefinition effect, string name)
    {
        if (!_byEffect.TryGetValue(effect.Name, out var saved)
            || saved.RemoveAll(preset => string.Equals(preset.Name, name, StringComparison.CurrentCultureIgnoreCase)) == 0)
        {
            return false;
        }

        Write();
        return true;
    }

    private void Write()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        File.WriteAllText(_path, JsonSerializer.Serialize(_byEffect, JsonOptions));
    }

    // A missing or unreadable file starts with no user presets rather than failing.
    private static Dictionary<string, List<SavedPreset>> Load(string path)
    {
        try
        {
            return File.Exists(path)
                ? JsonSerializer.Deserialize<Dictionary<string, List<SavedPreset>>>(File.ReadAllText(path)) ?? new()
                : new();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return new();
        }
    }

    private sealed record SavedPreset(string Name, List<double> Values);
}
