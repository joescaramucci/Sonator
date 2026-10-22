using Sonator.ViewModels;
using Xunit;

namespace Sonator.Tests;

public sealed class InstrumentIconTests
{
    [Fact]
    public void EveryIcon_HasItsOwnKey_AndFitsTheGrid()
    {
        Assert.Equal(InstrumentIcons.All.Count, InstrumentIcons.All.Select(icon => icon.Key).Distinct().Count());

        foreach (var icon in InstrumentIcons.All)
        {
            var bounds = icon.Strokes.Bounds;
            if (icon.Fills is not null)
            {
                bounds.Union(icon.Fills.Bounds);
            }

            Assert.False(bounds.IsEmpty, icon.Key);
            Assert.InRange(bounds.Left, -0.5, 24.5);
            Assert.InRange(bounds.Top, -0.5, 24.5);
            Assert.InRange(bounds.Right, -0.5, 24.5);
            Assert.InRange(bounds.Bottom, -0.5, 24.5);
        }
    }

    [Fact]
    public void Track_ShowsTheIconOfItsKey_AndIgnoresUnknownKeys()
    {
        var track = PatternFactory.CreatePattern("Pattern 1", "Test", "01").Tracks[0];
        Assert.Null(track.InstrumentIcon);

        track.InstrumentIconKey = "violin";
        Assert.Equal("Violin", track.InstrumentIcon?.Name);

        track.InstrumentIconKey = "no-such-icon";
        Assert.Null(track.InstrumentIconKey);
        Assert.Null(track.InstrumentIcon);
    }

    [Fact]
    public void Icon_IsKeptInTheSavedTrack()
    {
        var track = PatternFactory.CreatePattern("Pattern 1", "Test", "01").Tracks[0];
        track.InstrumentIconKey = "drums";

        var document = ProjectDocumentMapper.BuildTrackDocument(track);
        var restored = ProjectDocumentMapper.ApplyTrackDocument(document);

        Assert.Equal("drums", document.InstrumentIcon);
        Assert.Equal("drums", restored.InstrumentIconKey);
    }
}
