using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;

namespace Sonator.Services.Sounds;

// The sounds in folders the player chooses: audio samples (loops and one-shots, in WAV, AIFF, FLAC, MP3 or OGG),
// and Kontakt instruments (.nki), each filed by its name and the folders it is in. A Kontakt library is the
// folder that holds an Instruments folder (or the first folder under the one chosen); what it weighs is all
// its files together, its samples mostly.
public static class FolderSoundSource
{
    private static readonly string[] AudioExtensions = { ".wav", ".aif", ".aiff", ".flac", ".mp3", ".ogg" };

    // A sample's tempo in its name: "Groove 120bpm", "Loop_92_BPM", "Drums 128 Am".
    private static readonly Regex BpmInName = new(@"(?<![\d.])(\d{2,3}(?:[.,]\d+)?)\s*-?_?\s*bpm\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // A bare number in a loop's name that can be its tempo: "Funk Loop 98", "Break_174_Am".
    private static readonly Regex BareNumber = new(@"(?<![\d.])(\d{2,3})(?![\d.])", RegexOptions.Compiled);

    // Words of a loop, in its name or folders.
    private static readonly string[] LoopWords = { "loop", "loops", "groove", "grooves", "beat", "break", "phrase" };

    public static IReadOnlyList<SoundEntry> Read(IEnumerable<string> folders, IReadOnlyDictionary<string, string> installedPlugins)
    {
        var kontakt = KontaktPlugin(installedPlugins);
        var sounds = new List<SoundEntry>();
        var libraryBytes = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true };
        foreach (var folder in folders.Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            IEnumerable<string> files;
            try
            {
                files = Directory.EnumerateFiles(folder, "*", options).ToList();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue; // a folder that cannot be read is left out
            }

            // Hidden folders hold the makers' own files: Kontakt's previews of its instruments (heard in their place,
            // see PreviewOf), Maschine's of its kits.
            var listed = files
                .Where(file => !Path.GetRelativePath(folder, file).Split(Path.DirectorySeparatorChar).Any(part => part.StartsWith('.')))
                .ToList();
            // The audio inside a Kontakt library is its instruments' samples, not samples of their own.
            var libraries = listed
                .Where(file => file.EndsWith(".nki", StringComparison.OrdinalIgnoreCase))
                .Select(file => LibraryFolder(folder, file).TrimEnd('\\') + "\\")
                .Where(library => !string.Equals(library, folder.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            foreach (var file in listed)
            {
                var extension = Path.GetExtension(file).ToLowerInvariant();
                if (extension != ".nki" && libraries.Any(library => file.StartsWith(library, StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }
                if (extension == ".nki" && kontakt is { } plugin)
                {
                    var library = LibraryFolder(folder, file);
                    if (!libraryBytes.TryGetValue(library, out var bytes))
                    {
                        bytes = libraryBytes[library] = FolderBytes(library);
                    }

                    sounds.Add(KontaktEntry(folder, file, library, bytes, plugin.Name, plugin.Path));
                }
                else if (AudioExtensions.Contains(extension) && SampleEntry(folder, file) is { } sample)
                {
                    sounds.Add(sample);
                }
            }
        }

        // A library reached from two folders chosen (one inside the other, or a copy) is listed once.
        return sounds
            .GroupBy(sound => (sound.Kind, sound.Designer, sound.Bank, sound.Name))
            .Select(group => group.First())
            .ToList();
    }

    // The recording Kontakt keeps of an instrument, heard in its place before it is loaded: its .previews
    // folder's "<name>.nki.ogg". Null when it has none.
    public static string? PreviewOf(SoundEntry instrument)
    {
        if (instrument.Kind != SoundKind.KontaktInstrument || Path.GetDirectoryName(instrument.PresetPath) is not { } folder)
        {
            return null;
        }

        var name = Path.GetFileName(instrument.PresetPath);
        return new[] { ".ogg", ".wav", ".mp3" }
            .Select(extension => Path.Combine(folder, ".previews", name + extension))
            .FirstOrDefault(File.Exists);
    }

    // The Kontakt that plays the instruments: the plain one of the latest version ("Kontakt 7"), else any.
    internal static (string Name, string Path)? KontaktPlugin(IReadOnlyDictionary<string, string> installedPlugins)
    {
        var kontakts = installedPlugins.Where(pair => pair.Key.StartsWith("Kontakt", StringComparison.OrdinalIgnoreCase)).ToList();
        var plain = kontakts.Where(pair => Regex.IsMatch(pair.Key, @"^Kontakt( \d+)?$", RegexOptions.IgnoreCase))
            .OrderByDescending(pair => pair.Key, StringComparer.OrdinalIgnoreCase);
        var chosen = plain.Concat(kontakts.OrderByDescending(pair => pair.Key, StringComparer.OrdinalIgnoreCase)).FirstOrDefault();
        return chosen.Key is null ? null : (chosen.Key, chosen.Value);
    }

    // The library an instrument belongs to: the folder holding its Instruments folder, or the first folder
    // under the one chosen, or the chosen folder itself.
    internal static string LibraryFolder(string root, string file)
    {
        var relative = Path.GetRelativePath(root, file).Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var instruments = Array.FindIndex(relative, part => part.Equals("Instruments", StringComparison.OrdinalIgnoreCase));
        var depth = instruments >= 0 ? instruments : relative.Length > 1 ? 1 : 0; // the library itself chosen: its Instruments are at 0
        return depth == 0 ? root : Path.Combine(new[] { root }.Concat(relative.Take(depth)).ToArray());
    }

    private static long FolderBytes(string folder)
    {
        try
        {
            return new DirectoryInfo(folder)
                .EnumerateFiles("*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true })
                .Sum(file => file.Length);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }

    // <library>\Instruments\<folders>\<name>.nki: named by its file, filed in its folders ("Albion One
    // Orchestra / Strings"), its library its designer.
    internal static SoundEntry KontaktEntry(string root, string file, string library, long libraryBytes, string pluginName, string pluginPath)
    {
        var name = Path.GetFileNameWithoutExtension(file);
        var folders = Path.GetRelativePath(library, Path.GetDirectoryName(file)!)
            .Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .Where(part => part != "." && !part.Equals("Instruments", StringComparison.OrdinalIgnoreCase))
            .Select(part => part.Trim('_', ' '))
            .Where(part => part.Length > 0)
            .ToList();
        var bank = string.Join(" / ", folders);
        var libraryName = Path.GetFileName(library.TrimEnd('\\', '/'));
        return new SoundEntry(
            name,
            SoundCategories.Categorize(name, string.Join(" | ", folders.AsEnumerable().Reverse()), libraryName),
            $"Kontakt · {SoundEntry.WeightOf(libraryBytes)} library",
            pluginName,
            pluginPath,
            file,
            bank,
            libraryName,
            $"kontakt {libraryName} {bank}",
            SoundKind.KontaktInstrument,
            Bytes: libraryBytes);
    }

    // <pack>\<folders>\<name>.wav: named by its file, filed in its folder, its pack its designer; a loop's
    // tempo (when its name says it) among its tags.
    internal static SoundEntry? SampleEntry(string root, string file)
    {
        var name = Path.GetFileNameWithoutExtension(file);
        var folders = Path.GetRelativePath(root, Path.GetDirectoryName(file)!)
            .Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .Where(part => part != ".")
            .ToList();
        var pack = folders.FirstOrDefault() ?? Path.GetFileName(root.TrimEnd('\\', '/'));
        var bank = folders.LastOrDefault() ?? string.Empty;
        var words = $"{name} {string.Join(' ', folders)}".Replace('_', ' ').Replace('-', ' ');
        var loop = LoopWords.Any(word => Regex.IsMatch(words, $@"\b{word}\b", RegexOptions.IgnoreCase)) || BpmOf(name) is not null;
        var bpm = BpmOf(name, loop);
        long bytes;
        try
        {
            bytes = new FileInfo(file).Length;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        var category = SoundCategories.Categorize(name, string.Join(" | ", folders.AsEnumerable().Reverse()), pack);
        return new SoundEntry(
            name,
            category,
            loop ? bpm is { } tempo ? $"Loop · {tempo.ToString("0.##", CultureInfo.InvariantCulture)} BPM" : "Loop" : "One-shot",
            "Audio file",
            string.Empty,
            file,
            bank,
            pack,
            $"sample {(loop ? "loop" : "one-shot")} {string.Join(' ', folders)}",
            SoundKind.Sample,
            Bytes: bytes);
    }

    // A sample's tempo from its name: one followed by BPM, or, for a loop, a bare number a tempo can be.
    public static double? BpmOf(string name, bool loop = false)
    {
        var spaced = name.Replace('_', ' ');
        if (BpmInName.Match(spaced) is { Success: true } said
            && double.TryParse(said.Groups[1].Value.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var bpm)
            && bpm is >= 40 and <= 250)
        {
            return bpm;
        }

        if (loop)
        {
            foreach (Match number in BareNumber.Matches(spaced))
            {
                if (int.TryParse(number.Value, out var tempo) && tempo is >= 60 and <= 200)
                {
                    return tempo;
                }
            }
        }

        return null;
    }
}
