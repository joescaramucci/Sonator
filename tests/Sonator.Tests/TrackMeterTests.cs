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
