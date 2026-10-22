using Sonator.ViewModels;
using Xunit;

namespace Sonator.Tests;

public sealed class TransportClockTests
{
    [Fact]
    public void FormatPosition_StartOfSong_IsZero()
    {
        Assert.Equal(TransportClock.Zero, TransportClock.FormatPosition(0, 120));
        Assert.Equal("00:00:00", TransportClock.Zero);
    }

    [Fact]
    public void FormatPosition_ShowsWholeSeconds()
    {
        // At 60 BPM each sixteenth is a quarter of a second.
        Assert.Equal("00:00:00", TransportClock.FormatPosition(3, 60));
        Assert.Equal("00:00:01", TransportClock.FormatPosition(4, 60));
    }

    [Fact]
    public void FormatPosition_RollsOverIntoMinutesAndHours()
    {
        Assert.Equal("00:01:00", TransportClock.FormatPosition(240, 60));
        Assert.Equal("01:00:00", TransportClock.FormatPosition(240 * 60, 60));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-120)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void FormatPosition_InvalidTempo_UsesOneBpmFallback(double tempo)
    {
        // At 1 BPM one bar is 60 seconds, so step 4 is one minute in.
        Assert.Equal("00:01:00", TransportClock.FormatPosition(4, tempo));
    }
}
