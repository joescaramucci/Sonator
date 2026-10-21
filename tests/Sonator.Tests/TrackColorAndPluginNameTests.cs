using Sonator.ViewModels;
using Xunit;

namespace Sonator.Tests;

public sealed class TrackColorAndPluginNameTests
{
    [Fact]
    public void DefaultPattern_GivesEachOfItsEightTracksAnotherColour()
    {
        var pattern = PatternFactory.CreatePattern("Pattern 1", "Test", "01");

        var colours = pattern.Tracks.Select(track => track.TrackColorBrush.Color).ToList();

        Assert.Equal(8, colours.Count);
        Assert.Equal(8, colours.Distinct().Count());
    }

    [Theory]
    [InlineData(@"C:\Program Files\Common Files\VST3\ARP 2600 V3.vst3", "ARP 2600 V3")]
    [InlineData(@"C:\Program Files\Common Files\VST3\Bundle.vst3\", "Bundle")]
    public void PluginName_IsTheFileNameWithoutItsExtension(string path, string expected)
    {
        var track = PatternFactory.CreatePattern("Pattern 1", "Test", "01").Tracks[0];

        track.PluginFilePath = path;

        Assert.True(track.HasPlugin);
        Assert.Equal(expected, track.PluginName);
    }

    [Fact]
    public void TrackWithoutPlugin_HasNoPluginName()
    {
        var track = PatternFactory.CreatePattern("Pattern 1", "Test", "01").Tracks[0];

        Assert.False(track.HasPlugin);
        Assert.Null(track.PluginName);
    }
}
