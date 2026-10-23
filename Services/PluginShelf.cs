using System.IO;
using System.Text.Json;

namespace Sonator.Services;

// How the player has arranged the plug-in list: the order they dragged the plug-ins into, and the ones marked
// as favorites, which come first. Kept by plug-in file in %AppData%\Sonator\plugin-shelf.json.
public sealed class PluginShelf
{
    private sealed record Stored(List<string> Order, List<string> Favorites);

    private readonly string _path;
    private readonly List<string> _order;
    private readonly HashSet<string> _favorites;

    public PluginShelf(string? path = null)
    {
        _path = path ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Sonator", "plugin-shelf.json");
        var stored = Read(_path);
        _order = stored.Order;
        _favorites = new HashSet<string>(stored.Favorites, StringComparer.OrdinalIgnoreCase);
    }

    public bool IsFavorite(string pluginPath) => _favorites.Contains(pluginPath);

    public void SetFavorite(string pluginPath, bool favorite)
    {
        if (favorite ? _favorites.Add(pluginPath) : _favorites.Remove(pluginPath))
        {
            Save();
        }
    }

    // The plug-ins in the order to show them: favorites first, each group in the order the player left it;
    // plug-ins never moved keep the order they came in, after the ones that were.
    public IReadOnlyList<string> Arrange(IEnumerable<string> pluginPaths)
    {
        var place = _order
            .Select((path, index) => (path, index))
            .GroupBy(item => item.path, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First().index, StringComparer.OrdinalIgnoreCase);
        return pluginPaths
            .Select((path, arrival) => (path, arrival))
            .OrderBy(item => IsFavorite(item.path) ? 0 : 1)
            .ThenBy(item => place.TryGetValue(item.path, out var index) ? index : int.MaxValue)
            .ThenBy(item => item.arrival)
            .Select(item => item.path)
            .ToList();
    }

    // Remembers the list as it is now shown, after the player has dragged a plug-in to a new place.
    public void SetOrder(IEnumerable<string> pluginPaths)
    {
        var shown = pluginPaths.ToList();
        // Plug-ins not in the list now (another folder, removed) keep their places after the shown ones.
        var others = _order.Where(path => !shown.Contains(path, StringComparer.OrdinalIgnoreCase)).ToList();
        _order.Clear();
        _order.AddRange(shown);
        _order.AddRange(others);
        Save();
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllText(_path, JsonSerializer.Serialize(
                new Stored(_order, _favorites.Order(StringComparer.OrdinalIgnoreCase).ToList()),
                new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Kept for this session only.
        }
    }

    private static Stored Read(string path)
    {
        try
        {
            if (File.Exists(path) && JsonSerializer.Deserialize<Stored>(File.ReadAllText(path)) is { } stored)
            {
                return new Stored(stored.Order ?? new List<string>(), stored.Favorites ?? new List<string>());
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
        }

        return new Stored(new List<string>(), new List<string>());
    }
}
