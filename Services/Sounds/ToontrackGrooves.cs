using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using Melanchall.DryWetMidi.Core;
using Melanchall.DryWetMidi.Interaction;

namespace Sonator.Services.Sounds;

// The MIDI grooves that come with Toontrack's instruments: drum grooves played by EZdrummer, keys grooves by
// EZkeys. They are files under ProgramData\Toontrack\<product>\MIDI\<library>\<style>\...\<part>.mid; folder
// names start with a number and @ that set their order, and # stands for / (POP#ROCK, 4#4). A groove is
// listed only when the plug-in that plays it is installed.
public static class ToontrackGrooveSource
{
    public static string Root { get; } = @"C:\ProgramData\Toontrack";

    // Which product's grooves each plug-in plays, by the start of the plug-in's name, and their category.
    private static readonly (string Folder, string PluginPrefix, string Category)[] Products =
    {
        ("EZdrummer", "EZdrummer", SoundCategories.DrumGrooves),
        ("EZkeys", "EZkeys", SoundCategories.KeysGrooves),
    };

    public static IReadOnlyList<SoundEntry> Read(IReadOnlyDictionary<string, string> installedPlugins, string? root = null, string? cacheFolder = null)
    {
        var sounds = new List<SoundEntry>();
        foreach (var (folder, prefix, category) in Products)
        {
            var midiRoot = Path.Combine(root ?? Root, folder, "MIDI");
            var plugin = installedPlugins
                .Where(pair => pair.Key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(pair => pair.Key, StringComparer.OrdinalIgnoreCase) // the latest version: EZdrummer 3 before EZdrummer
                .FirstOrDefault();
            if (plugin.Key is null || !Directory.Exists(midiRoot))
            {
                continue;
            }

            var files = GrooveFiles(midiRoot, cacheFolder);
            var styles = StyleNames(files);
            foreach (var relative in files)
            {
                if (ToEntry(relative, Path.Combine(midiRoot, relative), plugin.Key, plugin.Value, category, styles) is { } entry)
                {
                    sounds.Add(entry);
                }
            }
        }

        return sounds;
    }

    // The grooves' files, relative to the MIDI folder. Listing them all takes seconds, so the list is kept and
    // used again until a library is added, removed or updated (its folder changes).
    private static IReadOnlyList<string> GrooveFiles(string midiRoot, string? cacheFolder)
    {
        var libraries = Directory.GetDirectories(midiRoot);
        var stamp = string.Join("|", libraries.OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .Select(path => $"{Path.GetFileName(path)}:{Directory.GetLastWriteTimeUtc(path).Ticks}"));
        var cache = Path.Combine(
            cacheFolder ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Sonator"),
            $"grooves-{Path.GetFileName(Path.GetDirectoryName(midiRoot))}.txt");

        try
        {
            if (File.Exists(cache))
            {
                var lines = File.ReadAllLines(cache);
                if (lines.Length > 0 && lines[0] == stamp)
                {
                    return lines.Skip(1).ToList();
                }
            }
        }
        catch (IOException)
        {
            // Listed again below.
        }

        var files = Directory.EnumerateFiles(midiRoot, "*.mid", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(midiRoot, path))
            .ToList();
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(cache)!);
            File.WriteAllLines(cache, new[] { stamp }.Concat(files));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Without the list kept, the grooves are listed again next time.
        }

        return files;
    }

    // A library can hold several folders of one style ("05@MOTOWN", "11@MOTOWN"): told apart by number in
    // their order ("Motown", "Motown 2"). Keyed by the library's and the style's folder names.
    internal static IReadOnlyDictionary<(string Library, string Style), string> StyleNames(IEnumerable<string> relativePaths)
    {
        var names = new Dictionary<(string Library, string Style), string>();
        var folders = relativePaths
            .Select(path => path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
            .Where(parts => parts.Length > 2)
            .Select(parts => (Library: parts[0], Style: parts[1]))
            .Distinct();
        foreach (var sameName in folders.GroupBy(folder => (folder.Library, Clean(folder.Style))))
        {
            var ordered = sameName.OrderBy(folder => folder.Style, StringComparer.OrdinalIgnoreCase).ToList();
            for (var i = 0; i < ordered.Count; i++)
            {
                names[ordered[i]] = i == 0 ? sameName.Key.Item2 : $"{sameName.Key.Item2} {i + 1}";
            }
        }

        return names;
    }

    // <library>\<style>\...\<part>.mid: the groove is named by its folder and part ("Chorus · Variation 01"),
    // filed under its style (bank) and library (designer).
    internal static SoundEntry? ToEntry(string relativePath, string fullPath, string pluginName, string pluginPath, string category,
        IReadOnlyDictionary<(string Library, string Style), string>? styleNames = null)
    {
        var raw = relativePath.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var parts = raw.Select(Clean).ToArray();
        if (parts.Length < 2)
        {
            return null;
        }

        var library = parts[0];
        var style = parts.Length > 2
            ? styleNames?.GetValueOrDefault((raw[0], raw[1])) ?? parts[1]
            : string.Empty;
        var part = parts[^1];
        var name = parts.Length > 3 ? $"{parts[^2]} · {part}" : part;
        var middle = string.Join(" / ", parts.Skip(2).Take(parts.Length - 3));
        return new SoundEntry(
            name,
            category,
            middle,
            pluginName,
            pluginPath,
            fullPath,
            style,
            library,
            string.Join(' ', parts),
            SoundKind.Groove);
    }

    private static readonly Regex OrderPrefix = new(@"^[0-9A-Za-z\-]*@", RegexOptions.Compiled);

    // "000011@SUPERIOR_DRUMMER_3" → "Superior Drummer 3"; "117-S164@CHORUS" → "Chorus"; "01@EZD_POP#ROCK" →
    // "Ezd Pop/Rock"; "N@14._Hats_16th_Tight_Closed.mid" → "14. Hats 16th Tight Closed".
    internal static string Clean(string part)
    {
        var text = Path.GetExtension(part).Equals(".mid", StringComparison.OrdinalIgnoreCase)
            ? Path.GetFileNameWithoutExtension(part)
            : part;
        text = OrderPrefix.Replace(text, string.Empty).Replace('#', '/').Replace('_', ' ').Trim();
        if (text.Length > 0 && text == text.ToUpperInvariant())
        {
            text = CultureInfo.InvariantCulture.TextInfo.ToTitleCase(text.ToLowerInvariant());
        }

        return text;
    }
}

// A groove's notes, in sixteenth-note steps from its start, each at its exact place within its step.
public static class GrooveReader
{
    public static IReadOnlyList<(double Start, double Length, int NoteNumber, int Velocity)> Read(string path)
    {
        var midi = MidiFile.Read(path);
        if (midi.TimeDivision is not TicksPerQuarterNoteTimeDivision division || division.TicksPerQuarterNote <= 0)
        {
            throw new InvalidDataException("The groove's MIDI file has no ticks per quarter note.");
        }

        var ticksPerStep = division.TicksPerQuarterNote / 4.0;
        return midi.GetNotes()
            .Select(note => (
                Start: note.Time / ticksPerStep,
                Length: Math.Max(0.05, note.Length / ticksPerStep),
                NoteNumber: (int)note.NoteNumber,
                Velocity: Math.Max(1, (int)note.Velocity)))
            .OrderBy(note => note.Start)
            .ToList();
    }
}
