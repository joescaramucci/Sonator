using Sonator.Services.Effects;
using Xunit;

namespace Sonator.Tests;

public sealed class TunerTests
{
    private const double Rate = 48000;

    private static float[] Sing(double frequency, double seconds)
    {
        var frames = (int)(seconds * Rate);
        var samples = new float[frames * 2];
        for (var i = 0; i < frames; i++)
        {
            var phase = 2 * Math.PI * frequency * i / Rate;
            samples[i * 2] = samples[i * 2 + 1] = (float)(0.3 * Math.Sin(phase) + 0.12 * Math.Sin(2 * phase));
        }

        return samples;
    }

    private static float[] Tune(float[] samples, params (string Name, double Value)[] settings)
    {
        using var chain = NativeEffectChain.TryCreate()!;
        chain.SetLayout([EffectKind.Tune]);
        var definition = EffectCatalog.Get(EffectKind.Tune);
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

    // How strong a frequency is in the left side's second half (the Goertzel filter).
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

    [Fact]
    public void ANoteSungFlat_IsMovedToTheNoteItMeant()
    {
        // 214 Hz is A3 (220 Hz) sung half a quarter-tone flat.
        var output = Tune(Sing(214, 2.0), ("Speed", 0), ("Humanize", 0));

        Assert.True(Strength(output, 220) > 4 * Strength(output, 214), $"220 Hz {Strength(output, 220):0.0}, 214 Hz {Strength(output, 214):0.0}");
    }

    [Fact]
    public void InAKey_TheNoteGoesToTheNearestOfItsScale()
    {
        // 236 Hz is A#3 a little sharp: not in C major, whose nearest note to it is B3 (246.9 Hz).
        var output = Tune(Sing(236, 2.0), ("Key", 1), ("Minor", 0), ("Speed", 0), ("Humanize", 0));

        Assert.True(Strength(output, 246.94) > 4 * Strength(output, 236), $"B3 {Strength(output, 246.94):0.0}, sung {Strength(output, 236):0.0}");
        Assert.True(Strength(output, 246.94) > 4 * Strength(output, 233.08));
    }

    [Fact]
    public void AtMixNothing_TheVoiceComesOutAsItWas_AsLateAsTheTunedOne()
    {
        var voice = Sing(220, 0.5);
        var output = Tune((float[])voice.Clone(), ("Mix", 0));
        var latency = (int)(Rate * 0.032);

        for (var i = 0; i < voice.Length / 2 - latency; i += 97)
        {
            Assert.Equal(voice[i * 2], output[(i + latency) * 2], 4);
        }
    }

    [Fact]
    public void WithNothingToCorrect_AVoiceWithVibrato_ComesOutAsItWent_EvenHalfMixed()
    {
        // A vowel on A3, 20 cents flat, with a vibrato of 25 cents. Its grains laid the period heard apart slipped
        // against the voice, and with the dry voice half and half it combed like a flanger.
        var frames = (int)(Rate * 2);
        var voice = new float[frames * 2];
        double phase = 0;
        for (var i = 0; i < frames; i++)
        {
            var frequency = 220 * Math.Pow(2, (-0.20 + 0.25 * Math.Sin(2 * Math.PI * 5.5 * i / Rate)) / 12);
            phase += 2 * Math.PI * frequency / Rate;
            double value = 0;
            for (var harmonic = 1; harmonic < 12; harmonic++)
            {
                value += (Math.Exp(-Math.Pow((harmonic * frequency - 800) / 300, 2)) + 0.05) * Math.Sin(harmonic * phase);
            }

            voice[i * 2] = voice[i * 2 + 1] = (float)(0.15 * value);
        }

        var output = Tune((float[])voice.Clone(), ("Amount", 0), ("Mix", 50));

        // In 50 ms windows, each at its best delay near the latency: the sound itself, however late.
        var window = (int)(Rate / 20);
        var ratios = new List<double>();
        for (var start = frames / 4; start + window < frames; start += window)
        {
            var best = double.MaxValue;
            double signal = 0;
            for (var delay = 1400; delay < 1700; delay++)
            {
                double error = 0, power = 0;
                for (var i = start; i < start + window; i += 2)
                {
                    var difference = output[i * 2] - voice[(i - delay) * 2];
                    error += difference * difference;
                    power += voice[(i - delay) * 2] * voice[(i - delay) * 2];
                }

                if (error < best)
                {
                    best = error;
                    signal = power;
                }
            }

            ratios.Add(10 * Math.Log10(signal / Math.Max(1e-12, best)));
        }

        var median = ratios.OrderBy(ratio => ratio).ElementAt(ratios.Count / 2);
        Assert.True(median > 18, $"{median:0.0} dB of signal over the difference");
    }
}
