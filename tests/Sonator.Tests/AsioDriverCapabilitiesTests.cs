using Sonator.Services;
using Xunit;

namespace Sonator.Tests;

public sealed class AsioDriverCapabilitiesTests
{
    [Fact]
    public void BufferSizeChoices_PowerOfTwoGranularity_ListsPowersInRange()
    {
        var sizes = AsioDriverCapabilities.BufferSizeChoices(min: 64, max: 1024, preferred: 512, granularity: -1);

        Assert.Equal(new[] { 64, 128, 256, 512, 1024 }, sizes);
    }

    [Fact]
    public void BufferSizeChoices_FixedGranularityZero_OffersOnlyTheMinimum()
    {
        var sizes = AsioDriverCapabilities.BufferSizeChoices(min: 256, max: 256, preferred: 256, granularity: 0);

        Assert.Equal(new[] { 256 }, sizes);
    }

    [Fact]
    public void BufferSizeChoices_StepGranularity_StepsFromTheMinimum()
    {
        var sizes = AsioDriverCapabilities.BufferSizeChoices(min: 96, max: 288, preferred: 192, granularity: 96);

        Assert.Equal(new[] { 96, 192, 288 }, sizes);
    }

    [Fact]
    public void BufferSizeChoices_PreferredSizeIsAlwaysOffered()
    {
        // 300 is not a power of two, but it is the driver's preferred size, so it must be selectable.
        var sizes = AsioDriverCapabilities.BufferSizeChoices(min: 64, max: 1024, preferred: 300, granularity: -1);

        Assert.Contains(300, sizes);
    }

    [Fact]
    public void BufferSizeChoices_InvalidRange_ReturnsNothing()
    {
        Assert.Empty(AsioDriverCapabilities.BufferSizeChoices(min: 0, max: 512, preferred: 256, granularity: -1));
        Assert.Empty(AsioDriverCapabilities.BufferSizeChoices(min: 512, max: 64, preferred: 256, granularity: -1));
    }
}
