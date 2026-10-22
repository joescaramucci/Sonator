using Sonator.Services;
using Sonator.ViewModels;
using Xunit;

namespace Sonator.Tests;

public sealed class RecordedVelocityTests
{
    [Fact]
    public void PluginTracks_PlayTheNotesAsTheyWerePlayed()
    {
        using var engine = new SequencerEngine { MasterVolume = 0.75 };
        var track = PatternFactory.CreatePattern("Pattern 1", "Test", "01").Tracks[0];
        track.Volume = 0.75;
        track.PluginFilePath = @"C:\VST3\DX7 V.vst3";

        // Pianissimo stays pianissimo and fortissimo stays fortissimo, whatever the faders.
        Assert.Equal(20, engine.PlaybackVelocity(track, 20));
        Assert.Equal(127, engine.PlaybackVelocity(track, 127));
    }

    [Fact]
    public void ExternalMidiTracks_StillHearTheVolumeInTheVelocity()
    {
        using var engine = new SequencerEngine { MasterVolume = 1 };
        var track = PatternFactory.CreatePattern("Pattern 1", "Test", "01").Tracks[0];
        track.PluginFilePath = null;
        track.Volume = 0.5;

        Assert.Equal(50, engine.PlaybackVelocity(track, 100));
    }

    [Fact]
    public void TrackVolume_IsAGainOnTheSound_UnityAtTheDefaults()
    {
        Assert.Equal(1, TrackEffectsHost.Gain(0.75, 0.75), 6);
        Assert.Equal(0, TrackEffectsHost.Gain(0, 0.75), 6);
        Assert.Equal(0.5, TrackEffectsHost.Gain(0.375, 0.75), 6);
        Assert.True(TrackEffectsHost.Gain(1, 1) > 1);
    }
}
