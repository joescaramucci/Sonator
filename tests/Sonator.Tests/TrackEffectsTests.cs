using Sonator.Services.Effects;
using Sonator.ViewModels;
using Xunit;

namespace Sonator.Tests;

public sealed class TrackEffectsTests
{
    private static PatternTrackViewModel NewTrack() => PatternFactory.CreatePattern("Pattern 1", "Test", "01").Tracks[0];

    private static double PeakAfter(NativeEffectChain chain, double level)
    {
        var samples = new float[9600 * 2];
        for (var i = 0; i < 9600; i++)
        {
            samples[i * 2] = samples[i * 2 + 1] = (float)(level * Math.Sin(2 * Math.PI * 440 * i / 48000.0));
        }

        chain.Process(samples, 9600, 2, 48000);
        return samples.Max(sample => Math.Abs(sample));
    }

    [Fact]
    public void NewEffect_StartsAtTheCatalogDefaults()
    {
        var effect = new TrackEffectViewModel(EffectCatalog.Get(EffectKind.Delay));

        Assert.True(effect.IsEnabled);
        Assert.Equal(new[] { 375.0, 35, 25, 0, 8000 }, effect.Parameters.Select(parameter => parameter.Value));
    }

    [Fact]
    public void LogarithmicKnob_SpreadsItsRangeByRatio()
    {
        var time = new TrackEffectViewModel(EffectCatalog.Get(EffectKind.Delay)).Parameters[0]; // 1 to 2000 ms

        time.Position = 0.5;

        Assert.Equal(Math.Sqrt(2000), time.Value, 3);
        Assert.Equal(0.5, time.Position, 6);
    }

    [Fact]
    public void Knob_StaysInItsRange()
    {
        var mix = new TrackEffectViewModel(EffectCatalog.Get(EffectKind.Verb)).Parameters[4];

        mix.Value = 150;

        Assert.Equal(100, mix.Value);
    }

    [Fact]
    public void Effects_AreSavedAndLoadedWithTheTrack()
    {
        var track = NewTrack();
        var compressor = new TrackEffectViewModel(EffectCatalog.Get(EffectKind.Compress));
        compressor.Parameters[0].Value = -30;
        compressor.IsEnabled = false;
        track.Effects.Add(new TrackEffectViewModel(EffectCatalog.Get(EffectKind.EQ)));
        track.Effects.Add(compressor);

        var restored = ProjectDocumentMapper.ApplyTrackDocument(ProjectDocumentMapper.BuildTrackDocument(track));

        Assert.Equal(new[] { "SonEQ", "SonCompress" }, restored.Effects.Select(effect => effect.Name));
        Assert.False(restored.Effects[1].IsEnabled);
        Assert.Equal(-30, restored.Effects[1].Parameters[0].Value);
    }

    [Fact]
    public void Host_FollowsTheTrack_AsEffectsAreAddedAndTurned()
    {
        using var host = new TrackEffectsHost();
        var track = NewTrack();
        var chain = host.ChainFor(track);
        Assert.NotNull(chain);
        Assert.Equal(0.9, PeakAfter(chain!, 0.9), 2);

        // Adding a limiter at -6 dB holds the peaks near 0.5.
        var limiter = new TrackEffectViewModel(EffectCatalog.Get(EffectKind.Limiter));
        limiter.Parameters[0].Value = -6;
        track.Effects.Add(limiter);
        Assert.InRange(PeakAfter(chain!, 0.9), 0.45, 0.502);

        // Turning its ceiling down is heard straight away, without rebuilding the chain.
        limiter.Parameters[0].Value = -12;
        Assert.InRange(PeakAfter(chain!, 0.9), 0.2, 0.252);

        // Switching it off lets the sound through again.
        limiter.IsEnabled = false;
        Assert.Equal(0.9, PeakAfter(chain!, 0.9), 2);
    }
}
