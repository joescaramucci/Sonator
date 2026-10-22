using System.IO;

namespace Sonator.ViewModels;

// Decides which plug-ins the list shows: the ones found in the scanned folders and the files added one
// by one, without the removed ones, each once, sorted by name.
internal static class VstPluginListRules
{
    public static IReadOnlyList<(string Name, string FilePath, string Kind)> Arrange(
        IEnumerable<(string Name, string FilePath, string Kind)> scanned,
        IEnumerable<string> addedFiles,
        IEnumerable<string> removedFiles)
    {
        var removed = new HashSet<string>(removedFiles, StringComparer.OrdinalIgnoreCase);
        var added = addedFiles
            .Where(path => File.Exists(path) || Directory.Exists(path))
            .Select(path => (Name: Path.GetFileNameWithoutExtension(path.TrimEnd('\\', '/')), FilePath: path, Kind: "VST3"));

        return scanned
            .Concat(added)
            .Where(plugin => !removed.Contains(plugin.FilePath))
            .GroupBy(plugin => plugin.FilePath, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .OrderBy(plugin => plugin.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }
}
