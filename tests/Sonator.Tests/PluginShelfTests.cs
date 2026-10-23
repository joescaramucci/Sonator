using System.IO;
using Sonator.Services;
using Xunit;

namespace Sonator.Tests;

public sealed class PluginShelfTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"sonator-shelf-{Guid.NewGuid():N}.json");

    public void Dispose() => File.Delete(_path);

    [Fact]
    public void Favorites_ComeFirst_ThenTheOrderTheyWereDraggedInto()
    {
        var shelf = new PluginShelf(_path);
        var found = new[] { "A", "B", "C", "D" };
        Assert.Equal(found, shelf.Arrange(found)); // nothing arranged yet: as found

        shelf.SetOrder(new[] { "C", "A", "B", "D" });
        shelf.SetFavorite("D", true);

        Assert.Equal(new[] { "D", "C", "A", "B" }, shelf.Arrange(found));

        // A plug-in found later goes after the arranged ones; the shelf is kept for next time.
        var reopened = new PluginShelf(_path);
        Assert.True(reopened.IsFavorite("d"));
        Assert.Equal(new[] { "D", "C", "A", "B", "E" }, reopened.Arrange(new[] { "A", "B", "C", "D", "E" }));
    }

    [Fact]
    public void PluginsNotShown_KeepTheirPlaces()
    {
        var shelf = new PluginShelf(_path);
        shelf.SetOrder(new[] { "X", "A", "B" });
        shelf.SetOrder(new[] { "B", "A" }); // X is not in the list now (another folder)

        Assert.Equal(new[] { "B", "A", "X" }, shelf.Arrange(new[] { "A", "B", "X" }));
    }
}
