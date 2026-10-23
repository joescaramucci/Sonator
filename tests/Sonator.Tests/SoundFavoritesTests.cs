using System.IO;
using Sonator.Services.Sounds;
using Xunit;

namespace Sonator.Tests;

public sealed class SoundFavoritesTests
{
    [Fact]
    public void AFavorite_IsKept_AndComesBackNextTime()
    {
        var path = Path.Combine(Path.GetTempPath(), $"sonator-favorites-{Guid.NewGuid():N}.json");
        var sax = new SoundEntry("SAX1", SoundCategories.Woodwinds, "", "CMI V", "cmi.vst3", @"C:\Presets\CMI V\SAX1", "", "", "");
        var pad = new SoundEntry("Warm", SoundCategories.Pad, "", "Jup-8 V4", "j8.vst3", @"C:\Presets\Jup-8 V4\Warm", "", "", "");
        try
        {
            var favorites = new SoundFavorites(path);
            favorites.Set(sax, true);
            favorites.Set(pad, true);
            favorites.Set(pad, false);

            var again = new SoundFavorites(path);
            Assert.True(again.Contains(sax));
            Assert.False(again.Contains(pad));
            Assert.Equal(1, again.Count);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
