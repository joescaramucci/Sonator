using System.IO;
using Sonator.Services;
using Sonator.Services.Effects;
using Sonator.ViewModels;
using Xunit;

namespace Sonator.Tests;

public sealed class MasteringTests
{
    private const int Rate = 48000;

    private static float[] Sine(double frequency, double level, double seconds)
    {
        var frames = (int)(seconds * Rate);
        var samples = new float[frames * 2];
        for (var i = 0; i < frames; i++)
        {
            var value = (float)(level * Math.Sin(2 * Math.PI * frequency * i / Rate));
            samples[i * 2] = value;
            samples[i * 2 + 1] = value;
        }

        return samples;
    }

    [Fact]
    public void ASineAtMinus20OnBothSides_MeasuresMinus20Lufs()
    {
        // EBU Tech 3341: a 1 kHz sine at -23 dBFS on both sides is -23 LUFS.
        var measure = Loudness.Of(Sine(997, Math.Pow(10, -20 / 20.0), 5), 2, Rate);

        Assert.InRange(measure.IntegratedLufs, -20.3, -19.7);
        Assert.InRange(measure.TruePeakDb, -20.2, -19.8);
    }

    [Fact]
    public void Silence_IsSilent()
    {
        Assert.True(double.IsNegativeInfinity(Loudness.Of(new float[Rate * 2], 2, Rate).IntegratedLufs));
    }

    private static float[] Master(float[] samples, params (string Name, double Value)[] settings)
    {
        using var chain = NativeEffectChain.TryCreate()!;
        chain.SetLayout([EffectKind.Mastering]);
        var definition = EffectCatalog.Get(EffectKind.Mastering);
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

    [Fact]
    public void Driven_TheSongGetsLouder_ButNeverOverTheCeiling()
    {
        // Bursts of a loud tone, with quiet between them, driven 12 dB into a ceiling of -1 dB.
        var input = Sine(220, 0.5, 4);
        for (var i = 0; i < input.Length / 2; i++)
        {
            if (i / (Rate / 4) % 2 == 1)
            {
                input[i * 2] *= 0.1f;
                input[i * 2 + 1] *= 0.1f;
            }
        }

        var before = Loudness.Of(input, 2, Rate).IntegratedLufs;
        var output = Master((float[])input.Clone(), ("Drive", 12), ("Ceiling", -1), ("Glue", 40));

        var ceiling = Math.Pow(10, -1 / 20.0);
        Assert.All(output, sample => Assert.True(Math.Abs(sample) <= ceiling + 1e-4));
        Assert.True(Loudness.Of(output, 2, Rate).IntegratedLufs > before + 3, "louder");
    }

    [Fact]
    public void AnExport_CanBeAnMp3()
    {
        var path = Path.Combine(Path.GetTempPath(), $"sonmast-{Guid.NewGuid():N}.mp3");
        try
        {
            MainViewModel.WriteMp3(path, Sine(440, 0.3, 2), Rate);

            var clip = AudioClipLoader.Load(path);
            Assert.InRange(clip.Duration.TotalSeconds, 1.9, 2.2);
            Assert.InRange(Loudness.Of(clip.Samples, clip.Channels, clip.SampleRate).IntegratedLufs, -14.5, -10);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void TheReport_AdvisesOnPeaksAndLoudness()
    {
        Assert.Contains("CEILING", MainViewModel.LoudnessReport(new Loudness.Measure(-10, -0.2)));
        Assert.Contains("DRIVE", MainViewModel.LoudnessReport(new Loudness.Measure(-22, -6)));
        Assert.Equal("-14.0 LUFS, true peak -1.0 dBTP", MainViewModel.LoudnessReport(new Loudness.Measure(-14, -1)));
    }
}
