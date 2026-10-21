using System.Collections.ObjectModel;
using Sonator.ViewModels;
using Xunit;

namespace Sonator.Tests;

public sealed class TrackAudioClipTests
{
    [Fact]
    public void AudioClipPath_SurvivesSaveAndLoad()
    {
        var steps = new ObservableCollection<StepViewModel> { new(0) };
        var track = new PatternTrackViewModel("Track", "Test", 1, 60, steps) { AudioClipPath = @"C:\clips\a.wav" };

        var restored = ProjectDocumentMapper.ApplyTrackDocument(ProjectDocumentMapper.BuildTrackDocument(track));

        Assert.Equal(@"C:\clips\a.wav", restored.AudioClipPath);
    }

    [Fact]
    public void IsEmptyTrack_OnlyForTracksWithoutNotesStepsOrClip()
    {
        var steps = new ObservableCollection<StepViewModel> { new(0) };
        var track = new PatternTrackViewModel("Track", "Test", 1, 60, steps);
        Assert.True(MainViewModel.IsEmptyTrack(track));

        track.AudioClipPath = @"C:\clips\a.wav";
        Assert.False(MainViewModel.IsEmptyTrack(track));
    }
}
