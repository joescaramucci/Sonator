using System.IO;
using Sonator.Services.Sounds;
using Xunit;

namespace Sonator.Tests;

public sealed class RecentSoundsTests
{
    private static SoundEntry Sound(int number) =>
        new($"Sound {number}", SoundCategories.Pad, "", "Jup-8 V4", "j8.vst3", $@"C:\Presets\Jup-8 V4\Sound {number}", "", "", "");

    [Fact]
    public void TheLastUsed_ComeLatestFirst_OnceEach_AndAreKept()
    {
        var path = Path.Combine(Path.GetTempPath(), $"sonator-recent-{Guid.NewGuid():N}.json");
        var sounds = Enumerable.Range(1, 3).Select(Sound).ToList();
        try
        {
            var recent = new RecentSounds(path);
            recent.Used(sounds[0]);
            recent.Used(sounds[1]);
            recent.Used(sounds[0]);

            var again = new RecentSounds(path);
            Assert.Equal(new[] { "Sound 1", "Sound 2" }, again.Of(sounds).Select(sound => sound.Name));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void OnlyTheLatestFifty_AreKept()
    {
        var path = Path.Combine(Path.GetTempPath(), $"sonator-recent-{Guid.NewGuid():N}.json");
        var sounds = Enumerable.Range(1, 60).Select(Sound).ToList();
        try
        {
            var recent = new RecentSounds(path);
            foreach (var sound in sounds)
            {
                recent.Used(sound);
            }

            Assert.Equal(RecentSounds.Most, recent.Count);
            Assert.Equal("Sound 60", recent.Of(sounds).First().Name);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
