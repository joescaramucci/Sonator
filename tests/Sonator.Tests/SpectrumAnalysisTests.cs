using Sonator.Services;
using Xunit;

namespace Sonator.Tests;

public sealed class SpectrumAnalysisTests
{
    private static double[] LevelsOf(double frequency, double amplitude)
    {
        var samples = new float[SpectrumAnalysis.Size];
        for (var i = 0; i < samples.Length; i++)
        {
            samples[i] = (float)(amplitude * Math.Sin(2 * Math.PI * frequency * i / 48000));
        }

        var levels = new double[SpectrumAnalysis.Bands];
        new SpectrumAnalysis().Analyse(samples, 48000, levels);
        return levels;
    }

    [Fact]
    public void ASine_LightsItsOwnBand_AtItsLevel()
    {
        var band = 12;
        var levels = LevelsOf(SpectrumAnalysis.CentreOf(band), Math.Pow(10, -20 / 20.0));

        Assert.InRange(levels[band], -21.5, -19.5); // a sine at -20 dBFS reads about -20 in its band
        Assert.True(levels[3] < -60); // far below it, nothing
        Assert.Equal(band, Array.IndexOf(levels, levels.Max()));
    }

    [Fact]
    public void Silence_ReadsTheFloor()
    {
        Assert.All(LevelsOf(1000, 0), level => Assert.Equal(SpectrumAnalysis.FloorDb, level));
    }
}
