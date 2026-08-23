using System.IO;

namespace Sonator.Services;

public sealed class VstPluginService
{
    private static readonly string[] SupportedExtensions = { ".vst3", ".dll" };

    public IReadOnlyList<(string Name, string FilePath, string Kind)> ScanDirectory(string directoryPath)
    {
        var results = new List<(string Name, string FilePath, string Kind)>();

        if (string.IsNullOrWhiteSpace(directoryPath) || !Directory.Exists(directoryPath))
        {
            return results;
        }

        try
        {
            var files = Directory.EnumerateFiles(directoryPath, "*.*", SearchOption.AllDirectories)
                .Where(file => SupportedExtensions.Contains(Path.GetExtension(file), StringComparer.OrdinalIgnoreCase))
                .OrderBy(file => Path.GetFileNameWithoutExtension(file), StringComparer.OrdinalIgnoreCase);

            foreach (var file in files)
            {
                var extension = Path.GetExtension(file);
                var kind = string.Equals(extension, ".vst3", StringComparison.OrdinalIgnoreCase) ? "VST3" : "VST2";
                results.Add((Path.GetFileNameWithoutExtension(file), file, kind));
            }
        }
        catch (IOException)
        {
            // Ignore folders that cannot be enumerated (permissions, removable media, etc.).
        }
        catch (UnauthorizedAccessException)
        {
            // Ignore folders that cannot be enumerated (permissions, removable media, etc.).
        }

        return results;
    }
}
