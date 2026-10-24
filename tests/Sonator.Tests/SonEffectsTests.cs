using Sonator.Services.Effects;
using Xunit;

namespace Sonator.Tests;

// Runs real audio through the native effects, so these check the DSP itself.
public sealed class SonEffectsTests
{
    private const double Rate = 48000;

    private static NativeEffectChain Chain(params EffectKind[] kinds)
    {
        var chain = NativeEffectChain.TryCreate();
        Assert.NotNull(chain);
        chain!.SetLayout(kinds);
        return chain;
    }

    private static void Set(NativeEffectChain chain, int slot, EffectKind kind, string name, double value)
    {
        var definition = EffectCatalog.Get(kind);
        var index = definition.Parameters.ToList().FindIndex(parameter => parameter.Name == name);
        chain.SetParameter(slot, definition.Parameters[index], index, value);
    }

    // Stereo interleaved: a sine at the given frequency and peak level.
    private static float[] Sine(double frequency, double level, int frames)
    {
        var samples = new float[frames * 2];
        for (var i = 0; i < frames; i++)
        {
            var value = (float)(level * Math.Sin(2 * Math.PI * frequency * i / Rate));
            samples[i * 2] = value;
            samples[i * 2 + 1] = value;
        }

        return samples;
    }

    private static double Peak(float[] samples, int fromFrame, int toFrame)
    {
        var peak = 0.0;
        for (var i = fromFrame * 2; i < toFrame * 2; i++)
        {
            peak = Math.Max(peak, Math.Abs(samples[i]));
        }

        return peak;
    }

    [Fact]
    public void Catalog_ListsTheSonEffects_WithDefaultsInRange()
    {
        Assert.Equal(new[] { "SonVerb", "SonDelay", "SonCompress", "SonEQ", "SonLimiter", "SonArmo", "SonCoro" }, EffectCatalog.All.Select(effect => effect.Name));
        Assert.All(EffectCatalog.All.SelectMany(effect => effect.Parameters), parameter =>
            Assert.InRange(parameter.Default, parameter.Minimum, parameter.Maximum));
    }

    [Fact]
    public void EmptyChain_PassesAudioThrough_AndAppliesTheGain()
    {
        using var chain = Chain();
        chain.SetGain(0.5);
        var samples = Sine(440, 0.8, 4800);

        chain.Process(samples, 4800, 2, Rate);

        Assert.Equal(0.4, Peak(samples, 0, 4800), 3);
        Assert.Equal(0.4, chain.TakePeak(), 3);
        Assert.Equal(0, chain.TakePeak());
    }

    [Fact]
    public void Delay_RepeatsAnImpulseAfterItsTime()
    {
        using var chain = Chain(EffectKind.Delay);
        Set(chain, 0, EffectKind.Delay, "Time", 100);
        Set(chain, 0, EffectKind.Delay, "Feedback", 0);
        Set(chain, 0, EffectKind.Delay, "Mix", 100);
        var samples = new float[9600 * 2];
        samples[0] = samples[1] = 1f;

        chain.Process(samples, 9600, 2, Rate);

        // The echo arrives 100 ms (4800 frames) later, and nothing in between.
        Assert.True(Peak(samples, 4799, 4802) > 0.5, "no echo at 100 ms");
        Assert.True(Peak(samples, 100, 4700) < 0.001, "sound between the impulse and its echo");
    }

    [Fact]
    public void Compressor_TurnsDownALoudSignal_AndReportsIt()
    {
        using var chain = Chain(EffectKind.Compress);
        Set(chain, 0, EffectKind.Compress, "Threshold", -20);
        Set(chain, 0, EffectKind.Compress, "Ratio", 4);
        Set(chain, 0, EffectKind.Compress, "Attack", 1);
        Set(chain, 0, EffectKind.Compress, "Knee", 0);
        var samples = Sine(200, 1.0, 24000);

        chain.Process(samples, 24000, 2, Rate);

        // 20 dB over the threshold at 4:1 leaves 5 dB over it: about -15 dBFS, a peak near 0.18.
        Assert.InRange(Peak(samples, 12000, 24000), 0.12, 0.26);
        Assert.InRange(chain.Meter(0), 13, 17);
    }

    [Fact]
    public void Reverb_LeavesATailAfterTheSoundStops()
    {
        using var chain = Chain(EffectKind.Verb);
        Set(chain, 0, EffectKind.Verb, "Mix", 100);
        var samples = new float[48000 * 2];
        for (var i = 0; i < 480; i++)
        {
            samples[i * 2] = samples[i * 2 + 1] = (float)Math.Sin(i * 0.3);
        }

        chain.Process(samples, 48000, 2, Rate);

        Assert.True(Peak(samples, 9600, 14400) > 0.001, "no reverb tail 200 ms later");
    }

    [Fact]
    public void Equalizer_LowShelf_BoostsTheBass()
    {
        using var chain = Chain(EffectKind.EQ);
        Set(chain, 0, EffectKind.EQ, "Low gain", 12);
        Set(chain, 0, EffectKind.EQ, "Low freq", 300);
        var samples = Sine(50, 0.1, 48000);

        chain.Process(samples, 48000, 2, Rate);

        // +12 dB is about four times the level.
        Assert.InRange(Peak(samples, 24000, 48000) / 0.1, 3.4, 4.4);
    }

    [Fact]
    public void Limiter_KeepsPeaksUnderTheCeiling()
    {
        using var chain = Chain(EffectKind.Limiter);
        Set(chain, 0, EffectKind.Limiter, "Ceiling", -6);
        var samples = Sine(440, 2.0, 9600);

        chain.Process(samples, 9600, 2, Rate);

        Assert.True(Peak(samples, 0, 9600) <= 0.502, "peaks over the ceiling");
    }

    [Fact]
    public void DisabledEffect_IsBypassed()
    {
        using var chain = Chain(EffectKind.Limiter);
        Set(chain, 0, EffectKind.Limiter, "Ceiling", -12);
        chain.SetEnabled(0, false);
        var samples = Sine(440, 0.9, 4800);

        chain.Process(samples, 4800, 2, Rate);

        Assert.Equal(0.9, Peak(samples, 0, 4800), 2);
    }
}

public sealed class SonNormalizeTests
{
    [Fact]
    public void Normalize_BringsThePeakToTheTarget_KeepingTheShape()
    {
        var clip = new Sonator.Services.AudioClip(new[] { 0.1f, -0.25f, 0.05f, 0f }, 1, 48000);

        var normalized = SonNormalize.Apply(clip, -6);

        Assert.Equal(-12.04, SonNormalize.PeakDb(clip), 2);
        Assert.Equal(-6, SonNormalize.PeakDb(normalized), 3);
        Assert.Equal(0.1 / 0.25, normalized.Samples[0] / -normalized.Samples[1], 4);
        Assert.Equal(0.1f, clip.Samples[0]); // the original is left alone
    }

    [Fact]
    public void SilentClip_IsLeftAlone()
    {
        var clip = new Sonator.Services.AudioClip(new float[8], 2, 48000);

        Assert.True(double.IsNegativeInfinity(SonNormalize.PeakDb(clip)));
        Assert.All(SonNormalize.Apply(clip, -1).Samples, sample => Assert.Equal(0f, sample));
    }
}
