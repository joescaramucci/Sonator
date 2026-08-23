using System.IO;

namespace Sonator.Services;

public sealed class VstPluginService
{
    /// <summary>
    /// Scans a folder for VST3 (single-file or bundle) and legacy VST2 (.dll) plug-ins.
    /// Directories that are themselves VST3 bundles (name ending in .vst3) are recorded
    /// as a single plug-in and NOT scanned recursively, since bundles can contain large
    /// resource/sample folders that would make the scan very slow.
    /// </summary>
    public IReadOnlyList<(string Name, string FilePath, string Kind)> ScanDirectory(string directoryPath)
    {
        var results = new List<(string Name, string FilePath, string Kind)>();

        if (string.IsNullOrWhiteSpace(directoryPath) || !Directory.Exists(directoryPath))
        {
            return results;
        }

        ScanDirectoryRecursive(directoryPath, results);

        return results
            .OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static void ScanDirectoryRecursive(string directoryPath, List<(string Name, string FilePath, string Kind)> results)
    {
        IEnumerable<string> entries;
        try
        {
            entries = Directory.EnumerateFileSystemEntries(directoryPath);
        }
        catch (IOException)
        {
            return;
        }
        catch (UnauthorizedAccessException)
        {
            return;
        }

        foreach (var entry in entries)
        {
            var isVst3Bundle = Directory.Exists(entry)
                && string.Equals(Path.GetExtension(entry), ".vst3", StringComparison.OrdinalIgnoreCase);

            if (isVst3Bundle)
            {
                results.Add((Path.GetFileNameWithoutExtension(entry), entry, "VST3"));
                continue; // Don't descend into the bundle's internal Contents/Resources.
            }

            if (File.Exists(entry))
            {
                var extension = Path.GetExtension(entry);
                if (string.Equals(extension, ".vst3", StringComparison.OrdinalIgnoreCase))
                {
                    results.Add((Path.GetFileNameWithoutExtension(entry), entry, "VST3"));
                }
                else if (string.Equals(extension, ".dll", StringComparison.OrdinalIgnoreCase))
                {
                    results.Add((Path.GetFileNameWithoutExtension(entry), entry, "VST2"));
                }

                continue;
            }

            if (Directory.Exists(entry))
            {
                ScanDirectoryRecursive(entry, results);
            }
        }
    }
}
