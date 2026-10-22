using Sonator.ViewModels;
using Xunit;

namespace Sonator.Tests;

public sealed class TrackMeterTests
{
    [Fact]
    public void RecordedNote_LightsTheMeterOnTheStepItStarts()
    {
        var track = PatternFactory.CreatePattern("Pattern 1", "Test", "01").Tracks[0];
        track.MidiNoteEvents = new[] { new MidiNoteEventViewModel(4, 2, 60, 100, StartOffset: 0.3) };

        Assert.Equal(0, TrackMeterCalculator.HitVelocity(track, 3));
        Assert.Equal(100, TrackMeterCalculator.HitVelocity(track, 4));
        Assert.Equal(0, TrackMeterCalculator.HitVelocity(track, 5));
    }

    [Fact]
    public void RecordedNotesPastTheGrid_StillLightTheMeter()
    {
        var track = PatternFactory.CreatePattern("Pattern 1", "Test", "01").Tracks[0];
        track.MidiNoteEvents = new[] { new MidiNoteEventViewModel(40, 1, 60, 90) };

        Assert.Equal(90, TrackMeterCalculator.HitVelocity(track, 40));
    }

    [Fact]
    public void Playback_RaisesTheTrackLevel_AndReleaseLetsItFall()
    {
        var tracks = PatternFactory.CreatePattern("Pattern 1", "Test", "01").Tracks;
        var track = tracks[0];
        track.Volume = 1;
        track.MidiNoteEvents = new[] { new MidiNoteEventViewModel(0, 1, 60, 127) };

        new TrackMeterCalculator().Update(tracks, 0, 1);
        Assert.Equal(1, track.SignalLevel, 3);

        for (var i = 0; i < 40 && TrackMeterCalculator.Release(tracks); i++)
        {
        }

        Assert.Equal(0, track.SignalLevel);
    }
}

public sealed class TrackAudioMeterTests
{
    [Fact]
    public void HeldSound_KeepsTheMeterUp_ForAsLongAsItLasts()
    {
        var track = PatternFactory.CreatePattern("Pattern 1", "Test", "01").Tracks[0];
        track.PluginFilePath = @"C:\VST3\Synth.vst3";
        Assert.True(track.HasOwnAudio);

        // A chord held at -12 dBFS: every refresh measures the same peak, so the meter stays where it is.
        for (var refresh = 0; refresh < 40; refresh++)
        {
            TrackMeterCalculator.ShowAudioLevel(track, Math.Pow(10, -12 / 20.0));
        }

        Assert.Equal((-12 + 42) / 42.0, track.SignalLevel, 3);

        // Released: it falls back gently, not at once.
        TrackMeterCalculator.ShowAudioLevel(track, 0);
        Assert.Equal((-12 + 42) / 42.0 - 0.03, track.SignalLevel, 3);
    }

    [Fact]
    public void LoudPlaying_ReachesTheRedLeds()
    {
        var track = PatternFactory.CreatePattern("Pattern 1", "Test", "01").Tracks[0];
        track.PluginFilePath = @"C:\VST3\Synth.vst3";

        // -6 dBFS lights ten of the twelve LEDs, so the first of the three red ones; -9 dB is where the red starts.
        TrackMeterCalculator.ShowAudioLevel(track, Math.Pow(10, -6 / 20.0));
        Assert.Equal(10, (int)Math.Round(track.SignalLevel * 12));

        // Very soft playing, -40 dBFS, still shows on the meter.
        track.SignalLevel = 0;
        TrackMeterCalculator.ShowAudioLevel(track, Math.Pow(10, -40 / 20.0));
        Assert.True(track.SignalLevel > 0);
    }

    [Fact]
    public void PlaybackAndLiveNotes_LeaveMeasuredTracksAlone()
    {
        var tracks = PatternFactory.CreatePattern("Pattern 1", "Test", "01").Tracks;
        var audio = tracks[0];
        audio.PluginFilePath = @"C:\VST3\Synth.vst3";
        audio.Steps[0].IsActive = true;
        audio.SignalLevel = 0.4;

        new TrackMeterCalculator().Update(tracks, 0, 1);
        TrackMeterCalculator.Release(tracks);

        Assert.Equal(0.4, audio.SignalLevel, 6);
    }
}
