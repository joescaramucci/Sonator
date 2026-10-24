using Sonator.Services.Effects;
using Xunit;

namespace Sonator.Tests;

public sealed class HarmonizerTests
{
    private const double Rate = 48000;

    [Theory]
    [InlineData(57, 2, 1, false, 3)]   // A in C major: a third above is C, a minor third
    [InlineData(60, 2, 1, false, 4)]   // C in C major: E, a major third
    [InlineData(64, 4, 1, false, 7)]   // E in C major: a fifth above is B
    [InlineData(65, 4, 1, false, 7)]   // F in C major: the fifth above is C
    [InlineData(60, 2, 10, true, 4)]   // C in A minor: E
    [InlineData(60, -2, 1, false, -3)] // C in C major: a third below is A
    [InlineData(61, 2, 0, false, 4)]   // no key: always four semitones
    [InlineData(60, 7, 1, false, 12)]  // the octave
    public void TheHarmony_FollowsTheScale(int sung, int steps, int key, bool minor, int shift)
    {
        Assert.Equal(shift, Harmony.Shift(sung, steps, key, minor));
    }

    // How strong a frequency is in the second half of a stereo buffer's left side (the Goertzel filter).
    private static double Strength(float[] samples, double frequency)
    {
        var frames = samples.Length / 2;
        var coefficient = 2 * Math.Cos(2 * Math.PI * frequency / Rate);
        double s1 = 0, s2 = 0;
        for (var i = frames / 2; i < frames; i++)
        {
            var s0 = samples[i * 2] + coefficient * s1 - s2;
            s2 = s1;
            s1 = s0;
        }

        return Math.Sqrt(s1 * s1 + s2 * s2 - coefficient * s1 * s2);
    }

    private static float[] Sing(double frequency, double seconds)
    {
        var frames = (int)(seconds * Rate);
        var samples = new float[frames * 2];
        for (var i = 0; i < frames; i++)
        {
            var phase = 2 * Math.PI * frequency * i / Rate;
            var value = (float)(0.3 * Math.Sin(phase) + 0.12 * Math.Sin(2 * phase));
            samples[i * 2] = value;
            samples[i * 2 + 1] = value;
        }

        return samples;
    }

    private static float[] Harmonize(double sungHz, double key, double minor, double voice1)
    {
        using var chain = NativeEffectChain.TryCreate()!;
        chain.SetLayout([EffectKind.Harmonizer]);
        var definition = EffectCatalog.Get(EffectKind.Harmonizer);
        void Set(string name, double value)
        {
            var index = definition.Parameters.ToList().FindIndex(parameter => parameter.Name == name);
            chain.SetParameter(0, definition.Parameters[index], index, value);
        }

        Set("Key", key);
        Set("Minor", minor);
        Set("Voice 1", voice1);
        Set("Voice 2", 0);
        Set("Level 1", 100);
        Set("Dry", 0);
        Set("Width", 0);

        var samples = Sing(sungHz, 1.5);
        const int Block = 512;
        var block = new float[Block * 2];
        for (var start = 0; start < samples.Length / 2; start += Block)
        {
            var frames = Math.Min(Block, samples.Length / 2 - start);
            Array.Copy(samples, start * 2, block, 0, frames * 2);
            chain.Process(block, frames, 2, Rate);
            Array.Copy(block, 0, samples, start * 2, frames * 2);
        }

        return samples;
    }

    [Fact]
    public void InCMajor_AnASungGetsTheCAbove()
    {
        var output = Harmonize(220.0, key: 1, minor: 0, voice1: 2);

        var c = Strength(output, 261.63);
        Assert.True(c > 10 * Strength(output, 277.18), "C, not C#");
        Assert.True(c > 10 * Strength(output, 220.0), "the harmony, not the voice");
    }

    [Fact]
    public void WithNoKey_TheIntervalIsFixed()
    {
        var output = Harmonize(220.0, key: 0, minor: 0, voice1: 2);

        var cSharp = Strength(output, 277.18);
        var c = Strength(output, 261.63);
        Assert.True(cSharp > 10 * c, $"C#, four semitones up: C# {cSharp:0.0}, C {c:0.0}, A {Strength(output, 220):0.0}, E {Strength(output, 329.6):0.0}");
    }
}
