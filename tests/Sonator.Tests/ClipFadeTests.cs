using Sonator.ViewModels;
using Xunit;

namespace Sonator.Tests;

public sealed class ClipFadeTests
{
    [Fact]
    public void AClipsFades_AreSavedWithItsTrack_AndNeverNegative()
    {
        var track = PatternFactory.CreatePattern("Pattern 1", "Test", "01").Tracks[0];
        track.AudioFadeInSeconds = 0.25;
        track.AudioFadeOutSeconds = 1.5;

        var restored = ProjectDocumentMapper.ApplyTrackDocument(ProjectDocumentMapper.BuildTrackDocument(track));

        Assert.Equal(0.25, restored.AudioFadeInSeconds);
        Assert.Equal(1.5, restored.AudioFadeOutSeconds);

        restored.AudioFadeInSeconds = -3;
        restored.AudioFadeOutSeconds = double.NaN;
        Assert.Equal(0, restored.AudioFadeInSeconds);
        Assert.Equal(0, restored.AudioFadeOutSeconds);
    }
}
