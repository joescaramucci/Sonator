using Sonator.Services;
using Xunit;
using Xunit.Abstractions;

namespace Sonator.Tests;

public sealed class TimeStretcherTests
{
    private const int SampleRate = 44100;
    private readonly ITestOutputHelper _output;

    public TimeStretcherTests(ITestOutputHelper output)
    {
        _output = output;
    }

    private static float[] Sine(double frequency, double seconds)
    {
        var frames = (int)(seconds * SampleRate);
        var samples = new float[frames];
        for (var frame = 0; frame < frames; frame++)
        {
            samples[frame] = (float)(0.5 * Math.Sin(2 * Math.PI * frequency * frame / SampleRate));
        }
        return samples;
    }

    // Zero crossings per second of the middle part of a clip, a simple estimate of its pitch.
    private static double EstimatedFrequency(float[] mono)
    {
        var crossings = 0;
        var from = mono.Length / 4;
        var to = mono.Length * 3 / 4;
        for (var index = from + 1; index < to; index++)
        {
            if (mono[index - 1] < 0 && mono[index] >= 0)
            {
                crossings++;
            }
        }
        return crossings / ((to - from) / (double)SampleRate);
    }

    [Theory]
    [InlineData(2.0)]
    [InlineData(0.5)]
    [InlineData(1.25)]
    public void Stretch_ChangesDurationByTheRatio(double ratio)
    {
        var input = Sine(440, 2.0);

        var output = TimeStretcher.Stretch(input, channels: 1, ratio);

        Assert.InRange(output.Length / (double)input.Length, ratio * 0.99, ratio * 1.01);
    }

    [Theory]
    [InlineData(2.0)]
    [InlineData(0.6)]
    public void Stretch_KeepsThePitch(double ratio)
    {
        var input = Sine(440, 2.0);

        var output = TimeStretcher.Stretch(input, channels: 1, ratio);

        var frequency = EstimatedFrequency(output);
        _output.WriteLine($"ratio={ratio} estimated pitch={frequency:F1} Hz");
        Assert.InRange(frequency, 440 * 0.95, 440 * 1.05);
    }

    [Fact]
    public void Stretch_ratioOne_ReturnsTheSameAudio()
    {
        var input = Sine(440, 0.5);

        Assert.Equal(input, TimeStretcher.Stretch(input, channels: 1, ratio: 1.0));
    }

    [Fact]
    public void Stretch_KeepsStereoChannelsApart()
    {
        var mono = Sine(440, 1.0);
        var stereo = new float[mono.Length * 2];
        for (var frame = 0; frame < mono.Length; frame++)
        {
            stereo[frame * 2] = mono[frame];
            stereo[frame * 2 + 1] = -mono[frame];
        }

        var output = TimeStretcher.Stretch(stereo, channels: 2, ratio: 1.5);

        for (var frame = 0; frame < output.Length / 2; frame += 997)
        {
            Assert.Equal(-output[frame * 2], output[frame * 2 + 1], precision: 4);
        }
    }
}
