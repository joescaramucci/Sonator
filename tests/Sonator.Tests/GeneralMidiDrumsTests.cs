using System.Collections.ObjectModel;
using Sonator.ViewModels;
using Xunit;

namespace Sonator.Tests;

public sealed class GeneralMidiDrumsTests
{
    [Theory]
    [InlineData(35, "BD2")]
    [InlineData(36, "BD")]
    [InlineData(38, "SN")]
    [InlineData(42, "HH C")]
    [InlineData(44, "HH P")]
    [InlineData(46, "HH O")]
    [InlineData(49, "CR1")]
    [InlineData(51, "RD1")]
    [InlineData(81, "TRI O")]
    public void TheKeys_AreNamedByTheGeneralMidiMap(int note, string drum)
    {
        Assert.Equal(drum, GeneralMidiDrums.ShortName(note));
    }

    [Theory]
    [InlineData(34)]
    [InlineData(82)]
    public void NotesOutsideTheMap_HaveNoDrum(int note)
    {
        Assert.Null(GeneralMidiDrums.ShortName(note));
        Assert.Null(GeneralMidiDrums.Name(note));
    }

    [Fact]
    public void ADrumTrack_IsKnownByItsChannelOrItsPlugin()
    {
        Assert.True(GeneralMidiDrums.LooksLikeDrums(Track("Track 1", "Piano", 10)));
        var plugin = Track("Track 1", "Piano", 1);
        plugin.PluginFilePath = @"C:\VST3\EZdrummer 3.vst3";
        Assert.True(GeneralMidiDrums.LooksLikeDrums(plugin));
        Assert.False(GeneralMidiDrums.LooksLikeDrums(Track("Bass", "Fingered bass", 2)));
    }

    private static PatternTrackViewModel Track(string name, string instrument, int channel) =>
        new(name, instrument, channel, 36, new ObservableCollection<StepViewModel>());
}
