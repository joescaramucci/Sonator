using Xunit;

namespace Sonator.Tests;

public sealed class VuMeterTests
{
    [Fact]
    public void ZeroVu_IsMinus18Dbfs()
    {
        Assert.Equal(0, VuMeter.VuOf(Math.Pow(10, -18 / 20.0)), 6);
        Assert.Equal(-6, VuMeter.VuOf(Math.Pow(10, -24 / 20.0)), 6);
        Assert.Equal(-20, VuMeter.VuOf(0));
        Assert.Equal(4, VuMeter.VuOf(1)); // a full-scale peak pins the needle just past +3
    }
}
