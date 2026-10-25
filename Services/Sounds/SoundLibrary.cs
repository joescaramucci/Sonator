using System.IO;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace Sonator.Services.Sounds;

// What loading a sound does: a preset becomes the plug-in's sound (an NKS preset's state wrapped first, see
// NksSoundSource.StateFor); a groove's notes go into the track.
public enum SoundKind
{
    Preset,
    Groove,
    NksPreset,
}

// One sound a plug-in can play: a preset, or a groove of notes to play it with, where it is stored, and how
// it is filed. PresetPath is the preset's file, or the groove's MIDI file. Genres are the music genres its
// maker files it under, split by |, when the maker does (Arturia does).
public sealed record SoundEntry(
    string Name,
    string Category,
    string Subcategory,
    string PluginName,
    string PluginPath,
    string PresetPath,
    string Bank,
    string Designer,
    string Tags,
    SoundKind Kind = SoundKind.Preset,
    string Genres = "")
{
    // Everything a search looks through, in lower case.
    public string SearchText { get; } = $"{Name} {Category} {Subcategory} {PluginName} {Bank} {Designer} {Tags}".ToLowerInvariant();
}

// The categories sounds are grouped in, whatever each maker calls them, in the order they are listed.
public static class SoundCategories
{
    public const string Piano = "Piano";
    public const string ElectricPiano = "Electric Piano";
    public const string Organ = "Organ";
    public const string SynthKeys = "Synth Keys";
    public const string Strings = "Strings";
    public const string Brass = "Brass";
    public const string Woodwinds = "Woodwinds";
    public const string Guitar = "Guitar & Plucked";
    public const string Bells = "Bells & Mallets";
    public const string Voices = "Choir & Voice";
    public const string Bass = "Bass";
    public const string Lead = "Lead";
    public const string Pad = "Pad";
    public const string Pluck = "Pluck";
    public const string Sequence = "Sequence & Arp";
    public const string Drums = "Drums & Percussion";
    public const string SoundFx = "Sound FX";
    public const string Other = "Other";
    public const string DrumGrooves = "Drum Grooves";
    public const string KeysGrooves = "Keys Grooves";

    public static IReadOnlyList<string> All { get; } = new[]
    {
        Piano, ElectricPiano, Organ, SynthKeys, Strings, Brass, Woodwinds, Guitar, Bells, Voices,
        Bass, Lead, Pad, Pluck, Sequence, Drums, SoundFx, Other, DrumGrooves, KeysGrooves,
    };

    // Rules tried in order on the sound's name first, then on how its maker files it: the first that matches
    // gives the category. Acoustic instruments are known by name too, as makers file some oddly (a CMI
    // saxophone filed under Organ, for one), and the narrower rules come before the wider ones (bassoon
    // before bass, electric piano before piano).
    private static readonly (Regex Pattern, string Category)[] Rules =
    {
        (Word(@"sax\w*|saxophone|clarinet\w*|flute\w*|oboe|bassoon|piccolo|recorder|woodwinds?|winds acoustic|winds hybrid|winds synth|pan ?pipes?"), Woodwinds),
        (Word(@"trumpets?|trombones?|tubas?|horns?|brass\w*|flugelhorn|cornet"), Brass),
        (Word(@"violins?|violas?|cellos?|contrabass|fiddle|strings?|string ensemble|bowed strings|orchestra\w*|pizzicato|pizz"), Strings),
        (Word(@"guitars?|gtr|banjo|mandolin|harp|sitar|koto|plucked strings"), Guitar),
        (Word(@"electric piano|e\.? ?piano|ep|rhodes?|wurli\w*|clavinet|clav|stage ?73|digital piano|classic ep|creative ep"), ElectricPiano),
        (Word(@"pianos?|grand|upright|acoustic grand|acoustic upright"), Piano),
        (Word(@"organs?|tonewheel|b-?3|farfisa|combo organ|church"), Organ),
        (Word(@"bells?|mallets?|marimba|vibraphone|vibes|glocken\w*|celesta|xylophone|kalimba|chimes?"), Bells),
        (Word(@"choirs?|vocals?|voices?|vox|vocoder|chant\w*|singers?"), Voices),
        (Word(@"drums?|drum loop|drum kit|kick|snare|hi-?hats?|high hat|toms?|claps?|cymbals?|percussion|perc|percussives?|beats?|grooves?"), Drums),
        (Word(@"bass\w*|sub bass"), Bass),
        (Word(@"sequences?|seq|arp\w*|melodic sequence|rhythmic sequence"), Sequence),
        (Word(@"leads?|solo lead|hard lead|soft lead"), Lead),
        (Word(@"pads?|atmospheres?|drones?|textures?|evolving pad"), Pad),
        (Word(@"plucks?|plucked\w*"), Pluck),
        (Word(@"keys|keyboards?|synth keys?|classic synth keys|lofi keys|plucked keys"), SynthKeys),
        (Word(@"sfx|fx|sound effects|noise|sweeps?|hits?|machines?|nature sounds|soundtrack|ambient sfx|creative sfx"), SoundFx),
    };

    // Instruments whose name is plain enough to be found inside a preset's name run together, as in "BARSAX1".
    private static readonly (Regex Pattern, string Category)[] InstrumentsInNames =
    {
        (Inside("sax|clarinet|flute|oboe|bassoon|piccolo"), Woodwinds),
        (Inside("trumpet|trombone|tuba|flugel"), Brass),
        (Inside("violin|viola|cello|fiddle"), Strings),
        (Inside("guitar|banjo|mandolin|sitar"), Guitar),
    };

    private static Regex Word(string alternatives) =>
        new($@"\b(?:{alternatives})\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static Regex Inside(string alternatives) =>
        new($"(?:{alternatives})", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    // The category of a sound from its name and how its maker files it (type and subtype).
    public static string Categorize(string name, string type, string subtype)
    {
        name = name.Replace('_', ' '); // "Elec_Piano" is two words
        // The name says what an acoustic instrument is; the maker's type says what a synth sound is for.
        foreach (var (pattern, category) in InstrumentsInNames.Concat(Rules.Take(4)))
        {
            if (pattern.IsMatch(name))
            {
                return category;
            }
        }

        var filed = $"{subtype} | {type}";
        foreach (var (pattern, category) in Rules)
        {
            if (pattern.IsMatch(filed))
            {
                return category;
            }
        }

        foreach (var (pattern, category) in Rules)
        {
            if (pattern.IsMatch(name))
            {
                return category;
            }
        }

        return Other;
    }
}

public static class SoundSearch
{
    // The sounds in the category (all of them when it is null) whose text holds every word of the query:
    // those with the words in their name first, then the others, each in name order.
    public static IEnumerable<SoundEntry> Filter(IEnumerable<SoundEntry> sounds, string? query, string? category)
    {
        var words = (query ?? string.Empty).ToLowerInvariant()
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return sounds
            .Where(sound =>
                (category is null || sound.Category == category)
                && words.All(word => sound.SearchText.Contains(word, StringComparison.Ordinal)))
            .OrderBy(sound => words.All(word => sound.Name.Contains(word, StringComparison.OrdinalIgnoreCase)) ? 0 : 1)
            .ThenBy(sound => sound.Name, StringComparer.OrdinalIgnoreCase);
    }
}

// Reads the sounds of Arturia's instruments from Arturia's own preset database, the one its preset browser
// uses. A sound is listed only when its instrument's plug-in is installed: the folder its presets are in is
// named as the plug-in is (CS-80 V4, Mini V3...).
public static class ArturiaSoundSource
{
    public static string DatabasePath { get; } = @"C:\ProgramData\Arturia\Presets\db.db3";

    public static IReadOnlyList<SoundEntry> Read(IReadOnlyDictionary<string, string> installedPlugins, string? databasePath = null)
    {
        var source = databasePath ?? DatabasePath;
        if (!File.Exists(source))
        {
            return Array.Empty<SoundEntry>();
        }

        // Read from a copy, so Arturia's plug-ins, which may have it open, are never held up or locked out.
        var copy = Path.Combine(Path.GetTempPath(), $"sonator-arturia-{Environment.ProcessId}.db3");
        File.Copy(source, copy, overwrite: true);
        try
        {
            var sounds = new List<SoundEntry>();
            using (var connection = new SqliteConnection($"Data Source={copy};Mode=ReadOnly;Pooling=False"))
            {
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = """
                    SELECT p.name, IFNULL(t.name, ''), IFNULL(s.name, ''), p.file_path, IFNULL(d.name, ''),
                           IFNULL((SELECT group_concat(c.name, ' ') FROM Preset_Characteristics pc
                                   JOIN Characteristics c ON c.key_id = pc.characteristic_key
                                   WHERE pc.preset_key = p.key_id), ''),
                           IFNULL((SELECT group_concat(c.name, '|') FROM Preset_Characteristics pc
                                   JOIN Characteristics c ON c.key_id = pc.characteristic_key
                                   JOIN Characteristics_Types ct ON ct.key_id = c.type_key
                                   WHERE pc.preset_key = p.key_id AND ct.name = 'Genres'), '')
                    FROM Preset_Id p
                    LEFT JOIN Types_V2 t ON t.key_id = p.type_v2
                    LEFT JOIN Subtypes_V2 s ON s.key_id = p.subtype_v2
                    LEFT JOIN Sound_Designers d ON d.key_id = p.sound_designer
                    WHERE IFNULL(p.hide_in_browser, 0) = 0
                    """;
                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    if (ToEntry(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
                            reader.GetString(4), reader.GetString(5), installedPlugins, reader.GetString(6)) is { } entry)
                    {
                        sounds.Add(entry);
                    }
                }
            }

            return sounds;
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            try
            {
                File.Delete(copy);
            }
            catch (IOException)
            {
                // Left for the system's temporary-file clean-up.
            }
        }
    }

    // A preset is at .../Presets/<plug-in>/<Factory or User>/<bank>/<name>. Null when its plug-in is not
    // installed, its file is gone, or it is one of the templates presets start from.
    internal static SoundEntry? ToEntry(string name, string type, string subtype, string filePath, string designer,
        string tags, IReadOnlyDictionary<string, string> installedPlugins, string genres = "")
    {
        var parts = filePath.Replace('\\', '/').Split('/');
        var presets = Array.FindIndex(parts, part => part.Equals("Presets", StringComparison.OrdinalIgnoreCase));
        if (presets < 0 || presets + 3 >= parts.Length
            || !installedPlugins.TryGetValue(parts[presets + 1], out var pluginPath)
            || type.Contains("Template", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var pluginName = parts[presets + 1];
        var bank = parts.Length - 2 > presets + 2 ? parts[^2] : string.Empty;
        return new SoundEntry(
            name,
            SoundCategories.Categorize(name, type, subtype),
            string.IsNullOrWhiteSpace(subtype) ? type : subtype,
            pluginName,
            pluginPath,
            filePath,
            bank,
            designer,
            $"{type} {tags}",
            Genres: genres);
    }
}
