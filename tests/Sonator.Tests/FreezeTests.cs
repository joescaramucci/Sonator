using Sonator.Models;
using Sonator.Services;
using Sonator.ViewModels;
using Xunit;

namespace Sonator.Tests;

public sealed class FreezeTests
{
    private static void OnSta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null)
        {
            throw new Xunit.Sdk.XunitException(failure.ToString());
        }
    }

    private static PatternTrackViewModel SynthTrack()
    {
        var track = PatternFactory.CreatePattern("Pattern 1", "Test", "01").Tracks[0];
        track.PluginFilePath = @"C:\VST3\Synth.vst3";
        track.MidiNoteEvents = new[] { new MidiNoteEventViewModel(0, 4, 60, 100) };
        return track;
    }

    [Fact]
    public void AFrozenTrack_KeepsItsSoundWhenSaved_AndItsPluginDoesNotStart()
    {
        var track = SynthTrack();
        Assert.Contains(track, MainViewModel.PlaybackInstrumentTracks(new[] { track }, null, recording: false));
        Assert.False(track.IsFrozen);
        Assert.StartsWith("Freeze", track.FreezeMenuHeader);

        track.FrozenAudioPath = @"C:\Songs\Song Audio\synth.wav";

        Assert.True(track.IsFrozen);
        Assert.StartsWith("Unfreeze", track.FreezeMenuHeader);
        Assert.Empty(MainViewModel.PlaybackInstrumentTracks(new[] { track }, null, recording: false));
        var restored = ProjectDocumentMapper.ApplyTrackDocument(ProjectDocumentMapper.BuildTrackDocument(track));
        Assert.Equal(@"C:\Songs\Song Audio\synth.wav", restored.FrozenAudioPath);
    }

    [Fact]
    public void AFrozenSound_IsOneOfTheProjectsAudioFiles()
    {
        var track = SynthTrack();
        track.FrozenAudioPath = @"C:\Users\me\AppData\Roaming\Sonator\audio\frozen.wav";
        var pattern = new PatternDocument("Pattern 1", "", "01", new List<TrackDocument> { ProjectDocumentMapper.BuildTrackDocument(track) });
        var document = new ProjectDocument("1.4", 120, 8, "4/4", null, null, 0, 15, new List<PatternDocument> { pattern }, new List<ArrangeRowDocument>(), new List<string[]>());

        Assert.Contains(@"C:\Users\me\AppData\Roaming\Sonator\audio\frozen.wav", ProjectAudioFiles.ClipsOf(document));
        var unpacked = ProjectAudioFiles.Unpack(document with
        {
            Patterns = new List<PatternDocument> { pattern with { Tracks = new List<TrackDocument> { pattern.Tracks[0] with { FrozenAudio = "Song Audio\\frozen.wav" } } } },
        }, @"C:\Songs\Song.sonator.json");
        Assert.Equal(@"C:\Songs\Song Audio\frozen.wav", unpacked.Patterns[0].Tracks[0].FrozenAudio);
    }

    [Fact]
    public void Unfreezing_PlaysThePluginAgain_AndCanBeUndone()
    {
        OnSta(() =>
        {
            using var viewModel = new MainViewModel();
            var track = viewModel.Tracks[0];
            track.PluginFilePath = @"C:\VST3\Synth.vst3";
            track.FrozenAudioPath = @"C:\frozen.wav";

            viewModel.UnfreezeTrack(track);
            Assert.False(viewModel.Tracks[0].IsFrozen);

            viewModel.UndoCommand.Execute(null);
            Assert.True(viewModel.Tracks[0].IsFrozen);
        });
    }
}
