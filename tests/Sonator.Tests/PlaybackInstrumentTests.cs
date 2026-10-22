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
    public void EveryPluginTrackWithNotes_Plays_NotJustOne()
    {
        var tracks = PluginTracks();
        tracks[1].MidiNoteEvents = new[] { new MidiNoteEventViewModel(0, 1, 60, 100) };
        tracks[3].MidiNoteEvents = new[] { new MidiNoteEventViewModel(0, 1, 64, 100) };

        var playing = MainViewModel.PlaybackInstrumentTracks(tracks, tracks[0], recording: false);

        Assert.Equal(new[] { tracks[1], tracks[3] }, playing);
    }

    [Fact]
    public void TheTrackRecordedOn_IsHeard_EvenWithoutNotesYet()
    {
        var tracks = PluginTracks();
        tracks[1].MidiNoteEvents = new[] { new MidiNoteEventViewModel(0, 1, 60, 100) };

        var playing = MainViewModel.PlaybackInstrumentTracks(tracks, tracks[6], recording: true);

        Assert.Equal(new[] { tracks[1], tracks[6] }, playing);
        Assert.DoesNotContain(tracks[6], MainViewModel.PlaybackInstrumentTracks(tracks, tracks[6], recording: false));
    }
}
