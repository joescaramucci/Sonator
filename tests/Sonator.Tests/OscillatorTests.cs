using System.Globalization;
using Sonator.Services;
using Xunit;

namespace Sonator.Tests;

public sealed class OscillatorTests
{
    [Theory]
    [InlineData(100)]
    [InlineData(1000)]
    [InlineData(10000)]
    [InlineData(20000)]
    public void EachTone_LoopsWithoutAClick(int frequency)
    {
        var samples = TestTone.Cycle(frequency);
        var frames = samples.Length / 2;
        var step = 2 * Math.PI * frequency / TestTone.SampleRate;

        // From the last frame back to the first is one ordinary step of the sine.
        Assert.Equal(Math.Sin(-step), samples[(frames - 1) * 2], 4);
        Assert.Equal(0, samples[0], 6);
        Assert.Equal(samples[0], samples[1]); // the same on both sides
    }

    [Fact]
    public void TheLineUpTone_ReadsZeroVu_OnAMeterCalibratedToIt()
    {
        var peak = TestTone.GainOf(TestTone.DefaultLevelDbfs);
        Assert.Equal(0, VuMeter.VuOf(peak), 6);
        Assert.Equal(-2, VuMeter.VuOf(peak, referenceDbfs: -16), 6); // CAL turned so 0 VU is -16 dBFS
    }

    [Fact]
    public void TheFrequencySwitches_ShowAndSetTheirOwnFrequency()
    {
        var converter = new SameValueConverter();
        Assert.Equal(true, converter.Convert(1000, typeof(bool), "1000", CultureInfo.InvariantCulture));
        Assert.Equal(false, converter.Convert(100, typeof(bool), "1000", CultureInfo.InvariantCulture));
        Assert.Equal(10000, converter.ConvertBack(true, typeof(int), "10000", CultureInfo.InvariantCulture));
    }
}
