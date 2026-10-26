using Sonator.Services;
using Xunit;

namespace Sonator.Tests;

public sealed class StemSeparatorTests
{
    private const int Segment = 400;

    // A model that gives each stem a fixed share of the mix: drums 40%, bass 30%, other 20%, vocals 10%.
    private static readonly float[] Shares = { 0.4f, 0.3f, 0.2f, 0.1f };

    private static float[] FakeModel(float[] segment)
    {
        var output = new float[Shares.Length * segment.Length];
        for (var stem = 0; stem < Shares.Length; stem++)
        {
            for (var i = 0; i < segment.Length; i++)
            {
                output[stem * segment.Length + i] = segment[i] * Shares[stem];
            }
        }

        return output;
    }

    private static AudioClip Noise(int frames, int seed = 7)
    {
        var random = new Random(seed);
        return new AudioClip(Enumerable.Range(0, frames * 2).Select(_ => (float)(random.NextDouble() * 2 - 1)).ToArray(), 2, StemSeparator.SampleRate);
    }

    [Theory]
    [InlineData(150)]   // shorter than a segment
    [InlineData(400)]   // one segment exactly
    [InlineData(1234)]  // segments overlapping, the last one padded
    public void Segments_OverlapAndCrossFade_IntoStemsAsLongAsTheMix_EachSampleInItsPlace(int frames)
    {
        var mix = Noise(frames);

        var stems = StemSeparator.Separate(mix, FakeModel, null, CancellationToken.None, Segment);

        Assert.Equal(4, stems.Count);
        for (var stem = 0; stem < stems.Count; stem++)
        {
            Assert.Equal(frames, stems[stem].FrameCount);
            Assert.Equal(2, stems[stem].Channels);
            for (var i = 0; i < mix.Samples.Length; i++)
            {
                Assert.Equal(mix.Samples[i] * Shares[stem], stems[stem].Samples[i], 4); // left and right kept apart
            }
        }
    }

    [Fact]
    public void Progress_ReachesTheEnd_AndStopStops()
    {
        var reports = new List<double>();
        StemSeparator.Separate(Noise(1000), FakeModel, new SyncProgress(reports), CancellationToken.None, Segment);
        Assert.Equal(1.0, reports[^1], 6);
        Assert.True(reports.Count >= 3);

        using var stop = new CancellationTokenSource();
        stop.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => StemSeparator.Separate(Noise(1000), FakeModel, null, stop.Token, Segment));
    }

    [Fact]
    public void ASongIsTakenAsTheModelHearsIt_StereoAt44100()
    {
        var mono = new AudioClip(Enumerable.Range(0, 22050).Select(i => (float)Math.Sin(i * 0.05)).ToArray(), 1, 22050);

        var taken = StemSeparator.ToModelFormat(mono);

        Assert.Equal(2, taken.Channels);
        Assert.Equal(44100, taken.SampleRate);
        Assert.InRange(taken.FrameCount, 44100 - 200, 44100 + 200); // a second, resampled
        Assert.Equal(taken.Samples[20000], taken.Samples[20001], 5); // the mono doubled
    }

    private sealed class SyncProgress : IProgress<double>
    {
        private readonly List<double> _reports;

        public SyncProgress(List<double> reports) => _reports = reports;

        public void Report(double value) => _reports.Add(value);
    }
}
