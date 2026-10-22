using System.IO;
using Sonator.Services;
using Sonator.ViewModels;
using Xunit;

namespace Sonator.Tests;

public sealed class PluginStateTests
{
    [Fact]
    public void ThePluginsSound_IsSavedWithTheProject_AndComesBack()
    {
        var track = PatternFactory.CreatePattern("Pattern 1", "Test", "01").Tracks[0];
        track.PluginFilePath = @"C:\VST3\DX7 V.vst3";
        track.PluginState = new byte[] { 1, 2, 3, 250 };
        track.PluginEditorState = new byte[] { 9, 8 };
        var document = new Models.ProjectDocument(
            "1.2", 120, 1, "4/4", null, null, 0, 15,
            new List<Models.PatternDocument>
            {
                new("Pattern 1", "Test", "01", new List<Models.TrackDocument> { ProjectDocumentMapper.BuildTrackDocument(track) }),
            },
            new List<Models.ArrangeRowDocument>(),
            new List<string[]>());

        var path = Path.Combine(Path.GetTempPath(), $"sonator-state-{Guid.NewGuid():N}.sonator.json");
        try
        {
            var service = new ProjectFileService();
            service.Save(path, document);
            var loaded = ProjectDocumentMapper.ApplyTrackDocument(service.Load(path).Patterns[0].Tracks[0]);

            Assert.Equal(track.PluginState, loaded.PluginState);
            Assert.Equal(track.PluginEditorState, loaded.PluginEditorState);
            Assert.Equal(track.PluginFilePath, loaded.PluginFilePath);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
