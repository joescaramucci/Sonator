using Sonator.ViewModels;
using Xunit;

namespace Sonator.Tests;

public sealed class TransportClockTests
{
    [Fact]
    public void FormatPosition_StartOfSong_IsZero()
    {
        Assert.Equal("00:00.00", TransportClock.FormatPosition(0, 120));
    }

    [Fact]
    public void FormatPosition_AtSixtyBpm_EachSixteenthIsAQuarterSecond()
    {
        Assert.Equal("00:00.50", TransportClock.FormatPosition(2, 60));
        Assert.Equal("00:01.00", TransportClock.FormatPosition(4, 60));
    }

    [Fact]
    public void FormatPosition_PastOneMinute_RollsOver()
    {
        Assert.Equal("01:00.00", TransportClock.FormatPosition(240, 60));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-120)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void FormatPosition_InvalidTempo_UsesOneBpmFallback(double tempo)
    {
        // At 1 BPM one bar is 60 seconds, so step 4 is one minute in.
        Assert.Equal("01:00.00", TransportClock.FormatPosition(4, tempo));
    }
}
