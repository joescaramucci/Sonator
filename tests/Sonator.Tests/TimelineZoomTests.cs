using System.Globalization;
using Sonator.ViewModels;
using Xunit;

namespace Sonator.Tests;

public sealed class TimelineZoomTests
{
    [Fact]
    public void ZoomingOut_GoesDownToNarrowBars_AndStopsThere()
    {
        var width = 180.0;
        for (var i = 0; i < 30; i++)
        {
            width = MainViewModel.ZoomedBarWidth(width, zoomIn: false);
        }

        Assert.Equal(MainViewModel.MinTimelineBarWidth, width);
        Assert.True(MainViewModel.MinTimelineBarWidth <= 16);
        Assert.True(MainViewModel.ZoomedBarWidth(width, zoomIn: true) > width);
    }

    [Theory]
    [InlineData(5, 180.0, "BAR 5")]
    [InlineData(5, 30.0, "5")]
    [InlineData(5, 16.0, "5")]
    [InlineData(6, 16.0, "")]
    public void TheRuler_NamesTheBars_AsRoomAllows(int bar, double width, string label)
    {
        Assert.Equal(label, new RulerBarLabelConverter().Convert([bar, width], typeof(string), null!, CultureInfo.InvariantCulture));
    }
}
