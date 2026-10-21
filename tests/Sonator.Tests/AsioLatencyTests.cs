using Sonator.Services;
using Xunit;

namespace Sonator.Tests;

public sealed class AsioLatencyTests
{
    [Fact]
    public void RoundTripMs_SumsInputAndOutputFrames()
    {
        // The SSL ASIO Driver 1 at 512 samples: input 584 + output 696 at 48 kHz.
        var latency = new AsioLatency(584, 696, 48000.0);

        Assert.Equal(26.67, latency.RoundTripMs, precision: 2);
    }

    [Fact]
    public void RoundTripMs_UnknownRate_IsNotANumber()
    {
        Assert.True(double.IsNaN(new AsioLatency(328, 408, 0.0).RoundTripMs));
    }
}
