using Sonator.Services.Effects;
using Xunit;

namespace Sonator.Tests;

public sealed class ChoirTests
{
    private const double Rate = 48000;

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

    private static float[] Choir(float[] samples, params (string Name, double Value)[] settings)
    {
        using var chain = NativeEffectChain.TryCreate()!;
        chain.SetLayout([EffectKind.Choir]);
        var definition = EffectCatalog.Get(EffectKind.Choir);
        foreach (var (name, value) in settings)
        {
            var index = definition.Parameters.ToList().FindIndex(parameter => parameter.Name == name);
            chain.SetParameter(0, definition.Parameters[index], index, value);
        }

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

    // How strong a frequency is on one side, over the second half of a stereo buffer (the Goertzel filter).
    private static double Strength(float[] samples, double frequency, int side = 0)
    {
        var frames = samples.Length / 2;
        var coefficient = 2 * Math.Cos(2 * Math.PI * frequency / Rate);
        double s1 = 0, s2 = 0;
        for (var i = frames / 2; i < frames; i++)
        {
            var s0 = samples[i * 2 + side] + coefficient * s1 - s2;
            s2 = s1;
            s1 = s0;
        }

        return Math.Sqrt(s1 * s1 + s2 * s2 - coefficient * s1 * s2);
    }

    [Fact]
    public void AtMixNothing_TheVoiceIsAsItWas()
    {
        var voice = Sing(220, 1.0);
        var output = Choir((float[])voice.Clone(), ("Mix", 0));

        Assert.Equal(voice, output);
    }

    [Fact]
    public void TheChoir_SingsTheVoicesNote_SpreadAcrossTheSides()
    {
        var output = Choir(Sing(220, 1.5), ("Mix", 100), ("Singers", 8), ("Detune", 15), ("Width", 100));

        var note = Strength(output, 220);
        Assert.True(note > 4 * Strength(output, 247), "the note sung, not another");
        var difference = 0.0;
        for (var i = output.Length / 4; i < output.Length / 2; i++)
        {
            difference += Math.Abs(output[i * 2] - output[i * 2 + 1]);
        }

        Assert.True(difference > 1, "left and right are different singers");
    }

    [Fact]
    public void TheLowSection_SingsAnOctaveBelow()
    {
        var without = Choir(Sing(220, 1.5), ("Mix", 100), ("Low section", 0));
        var with = Choir(Sing(220, 1.5), ("Mix", 100), ("Low section", 100));

        Assert.True(Strength(with, 110) > 5 * Strength(without, 110));
    }

    [Fact]
    public void ForALowVoice_TheLowSection_StaysWherePeopleSing()
    {
        // G2 (98 Hz): an octave below (49 Hz) is under any bass; the low section doubles the choir instead.
        var output = Choir(Sing(98, 2.0), ("Mix", 100), ("Low section", 100));

        Assert.True(Strength(output, 98) > 8 * Strength(output, 49), $"98 Hz {Strength(output, 98):0.0}, 49 Hz {Strength(output, 49):0.0}");
    }

    [Fact]
    public void AWomensChoir_FromAMansVoice_SingsAnOctaveUp()
    {
        var output = Choir(Sing(110, 2.0), ("Mix", 100), ("Voices", 1), ("Detune", 0), ("Wavering", 0));

        Assert.True(Strength(output, 220) > 3 * Strength(output, 110), $"220 Hz {Strength(output, 220):0.0}, 110 Hz {Strength(output, 110):0.0}");
    }

    [Fact]
    public void AMensChoir_FromAWomansVoice_SingsAnOctaveDown()
    {
        // The timbre kept, the sound stays where the voice had it (440 and 880 Hz) while the note goes down to 220 Hz,
        // which the choir as sung does not have at all.
        var men = Choir(Sing(440, 2.0), ("Mix", 100), ("Voices", 2), ("Detune", 0), ("Wavering", 0));
        var asSung = Choir(Sing(440, 2.0), ("Mix", 100), ("Voices", 0), ("Detune", 0), ("Wavering", 0));

        Assert.True(Strength(men, 220) > 10 * Strength(asSung, 220), $"men {Strength(men, 220):0.0}, as sung {Strength(asSung, 220):0.0}");
        Assert.True(Strength(men, 220) > 5 * Strength(men, 110), "one octave down, not two");
    }
}
