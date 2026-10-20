namespace Sonator.Services;

// Collects VST plug-ins from the default folder and an optional user folder.
// Each file path appears once, and the result is ordered by plug-in name.
internal sealed class VstPluginScanner
{
    private readonly Func<string, IReadOnlyList<(string Name, string FilePath, string Kind)>> _scanDirectory;
    private readonly Func<string, bool> _directoryExists;

    public VstPluginScanner(
        Func<string, IReadOnlyList<(string Name, string FilePath, string Kind)>> scanDirectory,
        Func<string, bool> directoryExists)
    {
        _scanDirectory = scanDirectory;
        _directoryExists = directoryExists;
    }

    public VstScanResult Scan(string defaultDirectory, string? userDirectory)
    {
        var scannedFolders = new List<string>();
        var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var plugins = new List<(string Name, string FilePath, string Kind)>();

        if (_directoryExists(defaultDirectory))
        {
            scannedFolders.Add(defaultDirectory);
            AddPlugins(defaultDirectory, seenPaths, plugins);
        }

        if (!string.IsNullOrWhiteSpace(userDirectory)
            && !string.Equals(userDirectory, defaultDirectory, StringComparison.OrdinalIgnoreCase))
        {
            scannedFolders.Add(userDirectory);
            AddPlugins(userDirectory, seenPaths, plugins);
        }

        var ordered = plugins
            .OrderBy(plugin => plugin.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        return new VstScanResult(ordered, scannedFolders);
    }

    private void AddPlugins(
        string directory,
        HashSet<string> seenPaths,
        List<(string Name, string FilePath, string Kind)> plugins)
    {
        foreach (var plugin in _scanDirectory(directory))
        {
            if (seenPaths.Add(plugin.FilePath))
            {
                plugins.Add(plugin);
            }
        }
    }
}

internal sealed record VstScanResult(
    IReadOnlyList<(string Name, string FilePath, string Kind)> Plugins,
    IReadOnlyList<string> ScannedFolders);
