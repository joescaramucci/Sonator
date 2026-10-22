using Sonator.ViewModels;
using Xunit;

namespace Sonator.Tests;

public sealed class PlaybackInstrumentTests
{
    private static IReadOnlyList<PatternTrackViewModel> PluginTracks()
    {
        var tracks = PatternFactory.CreatePattern("Pattern 1", "Test", "01").Tracks;
        for (var i = 0; i < tracks.Count; i++)
        {
            tracks[i].PluginFilePath = $@"C:\VST3\Synth{i}.vst3";
        }

        return tracks;
    }

    [Fact]
    public void PlaysTheRecordedTrack_NotTheFirstPluginTrack()
    {
        var tracks = PluginTracks();
        tracks[3].MidiNoteEvents = new[] { new MidiNoteEventViewModel(0, 1, 60, 100) };

        // The recorded track plays even when another track is selected.
        Assert.Same(tracks[3], MainViewModel.PlaybackInstrumentTrack(tracks, tracks[0], recording: false));
        Assert.Same(tracks[3], MainViewModel.PlaybackInstrumentTrack(tracks, null, recording: false));
    }

    [Fact]
    public void SelectedTrackWithNotes_OrBeingRecordedOn_IsTheOneHeard()
    {
        var tracks = PluginTracks();
        tracks[1].MidiNoteEvents = new[] { new MidiNoteEventViewModel(0, 1, 60, 100) };
        tracks[5].MidiNoteEvents = new[] { new MidiNoteEventViewModel(0, 1, 64, 100) };

        Assert.Same(tracks[5], MainViewModel.PlaybackInstrumentTrack(tracks, tracks[5], recording: false));
        Assert.Same(tracks[6], MainViewModel.PlaybackInstrumentTrack(tracks, tracks[6], recording: true));
    }
}
