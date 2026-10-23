using Xunit;

namespace Sonator.Tests;

public sealed class EffectDisplayTests
{
    [Fact]
    public void EqualizerCurve_PeaksAtTheMiddleBandsGain()
    {
        var band = EffectDisplay.Band.Peak(6, 1000, 1);
        Assert.Equal(6, band.GainDb(1000), 1);
        Assert.True(Math.Abs(band.GainDb(50)) < 0.3);
    }

    [Fact]
    public void EqualizerCurve_ShelvesReachTheirGainAtTheEnds()
    {
        Assert.Equal(-9, EffectDisplay.Band.LowShelf(-9, 200).GainDb(20), 0);
        Assert.Equal(4, EffectDisplay.Band.HighShelf(4, 4000).GainDb(18000), 0);
        Assert.Equal(0, EffectDisplay.Band.LowShelf(0, 200).GainDb(20));
    }

    [Fact]
    public void CompressorCurve_TurnsDownAboveTheThresholdByTheRatio()
    {
        Assert.Equal(0, EffectDisplay.CompressorGain(-30, -18, 4, 0));
        Assert.Equal(-9, EffectDisplay.CompressorGain(-6, -18, 4, 0), 6); // 12 dB over at 4:1 comes out 3 dB over
        Assert.InRange(EffectDisplay.CompressorGain(-18, -18, 4, 6), -1, -0.1); // the soft knee starts early
    }

    [Fact]
    public void ReverbDecay_GrowsWithTheSizeAndTurnsBack()
    {
        Assert.InRange(EffectDisplay.DecaySeconds(0), 0.5, 0.7);
        Assert.InRange(EffectDisplay.DecaySeconds(1), 9, 12);
        Assert.Equal(0.35, EffectDisplay.SizeForDecay(EffectDisplay.DecaySeconds(0.35)), 6);
    }
}
