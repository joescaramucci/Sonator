using System.Collections.ObjectModel;
using Sonator.Models;
using Sonator.ViewModels;
using Xunit;

namespace Sonator.Tests;

public sealed class PluginTrackTypeTests
{
    private static PatternTrackViewModel Track(PatternTrackType type, string instrument) =>
        new("Track 1", instrument, 1, 60, new ObservableCollection<StepViewModel>(), trackType: type);

    [Fact]
    public void AMidiTrackGivenAPlugIn_BecomesAVstTrackNamedAfterIt()
    {
        var track = Track(PatternTrackType.MidiChannel, "MIDI Channel");

        track.PluginFilePath = @"C:\Program Files\Common Files\VST3\Jun-6 V.vst3";

        Assert.Equal(PatternTrackType.VstInstrument, track.TrackType);
        Assert.Equal("VST", track.DisplayTrackType);
        Assert.Equal("Jun-6 V", track.Instrument);
    }

    [Fact]
    public void AnInstrumentNamedByThePlayer_KeepsItsName()
    {
        var track = Track(PatternTrackType.MidiChannel, "Bass");

        track.PluginFilePath = @"C:\VST3\Jun-6 V.vst3";

        Assert.Equal(PatternTrackType.VstInstrument, track.TrackType);
        Assert.Equal("Bass", track.Instrument);
    }

    [Fact]
    public void AnAudioTrackWithAClip_StaysAnAudioTrack()
    {
        var track = Track(PatternTrackType.AudioInput, "Audio clip");
        track.AudioClipPath = @"C:\clips\a.wav";

        track.PluginFilePath = @"C:\VST3\Jun-6 V.vst3";

        Assert.Equal(PatternTrackType.AudioInput, track.TrackType);
    }
}
