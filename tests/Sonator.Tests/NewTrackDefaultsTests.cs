using Sonator.Models;
using Sonator.ViewModels;
using Xunit;

namespace Sonator.Tests;

public sealed class NewTrackDefaultsTests
{
    [Fact]
    public void NextTrackName_NoTracks_StartsAtOne()
    {
        Assert.Equal("Track 1", NewTrackDefaults.NextTrackName(Array.Empty<string>()));
    }

    [Fact]
    public void NextTrackName_FollowsHighestNumberedTrack()
    {
        Assert.Equal("Track 5", NewTrackDefaults.NextTrackName(new[] { "Track 1", "Track 4", "Kick" }));
    }

    [Fact]
    public void NextTrackName_PrefixIsCaseInsensitive()
    {
        Assert.Equal("Track 3", NewTrackDefaults.NextTrackName(new[] { "track 2" }));
    }

    [Theory]
    [InlineData(new int[0], 1)]
    [InlineData(new[] { 1, 2, 3 }, 4)]
    [InlineData(new[] { 1, 2, 4 }, 5)]
    [InlineData(new[] { 1, 15 }, 16)]
    public void NextChannel_UsesChannelAfterHighestInUse(int[] used, int expected)
    {
        Assert.Equal(expected, NewTrackDefaults.NextChannel(used));
    }

    [Fact]
    public void NextChannel_AfterSixteen_UsesLowestFreeChannel()
    {
        Assert.Equal(1, NewTrackDefaults.NextChannel(new[] { 16, 2 }));
    }

    [Fact]
    public void NextChannel_AllChannelsUsed_FallsBackToSixteen()
    {
        Assert.Equal(16, NewTrackDefaults.NextChannel(Enumerable.Range(1, 16)));
    }

    [Theory]
    [InlineData(PatternTrackType.MidiChannel, "MIDI Channel", "")]
    [InlineData(PatternTrackType.VstInstrument, "VST Instrument", "VSTi")]
    [InlineData(PatternTrackType.AudioInput, "Audio Input", "Microphone / External")]
    public void InstrumentAndSource_MatchTrackType(PatternTrackType trackType, string instrument, string source)
    {
        Assert.Equal(instrument, NewTrackDefaults.InstrumentName(trackType));
        Assert.Equal(source, NewTrackDefaults.SourceName(trackType));
    }
}
