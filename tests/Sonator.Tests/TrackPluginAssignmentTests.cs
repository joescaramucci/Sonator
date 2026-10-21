using System.Collections.ObjectModel;
using Sonator.ViewModels;
using Xunit;

namespace Sonator.Tests;

public sealed class TrackPluginAssignmentTests
{
    [Fact]
    public void PluginFilePath_SurvivesSaveAndLoad()
    {
        var track = TimelineAndTrackRulesTestsHelper.CreateTrack();
        track.PluginFilePath = @"C:\Program Files\Common Files\VST3\TAL-NoiseMaker.vst3";

        var restored = ProjectDocumentMapper.ApplyTrackDocument(ProjectDocumentMapper.BuildTrackDocument(track));

        Assert.Equal(track.PluginFilePath, restored.PluginFilePath);
    }

    [Fact]
    public void PluginFilePath_BlankIsStoredAsNoPlugin()
    {
        var track = TimelineAndTrackRulesTestsHelper.CreateTrack();

        track.PluginFilePath = "   ";

        Assert.Null(track.PluginFilePath);
    }

    [Fact]
    public void PluginFilePath_OlderProjectWithoutPluginLoadsAsNoPlugin()
    {
        var track = TimelineAndTrackRulesTestsHelper.CreateTrack();
        var document = ProjectDocumentMapper.BuildTrackDocument(track) with { PluginFilePath = null };

        Assert.Null(ProjectDocumentMapper.ApplyTrackDocument(document).PluginFilePath);
    }
}

internal static class TimelineAndTrackRulesTestsHelper
{
    public static PatternTrackViewModel CreateTrack()
    {
        var steps = new ObservableCollection<StepViewModel> { new(0) };
        return new PatternTrackViewModel("Track", "Test", 1, 60, steps);
    }
}
