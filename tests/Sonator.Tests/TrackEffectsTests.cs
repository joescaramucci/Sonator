using Sonator.Services.Effects;
using Sonator.ViewModels;
using Xunit;

namespace Sonator.Tests;

public sealed class TrackEffectsTests
{
    // A track with an empty rack (new tracks start with SonVerb).
    private static PatternTrackViewModel NewTrack()
    {
        var track = PatternFactory.CreatePattern("Pattern 1", "Test", "01").Tracks[0];
        track.Effects.Clear();
        return track;
    }

    [Fact]
    public void NewTrack_StartsWithSonVerbSwitchedOn()
    {
        var track = PatternFactory.CreatePattern("Pattern 1", "Test", "01").Tracks[0];

        var effect = Assert.Single(track.Effects);
        Assert.Equal("SonVerb", effect.Name);
        Assert.True(effect.IsEnabled);
        Assert.Equal("Small Room", effect.PresetName);
        Assert.False(effect.IsPresetModified);
        Assert.Equal(new[] { 35.0, 50, 80, 5, 18 }, effect.Values);
    }

    [Fact]
    public void TrackLoadedFromAProject_KeepsItsOwnRack()
    {
        var track = NewTrack();

        var restored = ProjectDocumentMapper.ApplyTrackDocument(ProjectDocumentMapper.BuildTrackDocument(track));

        Assert.Empty(restored.Effects);
    }

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

public sealed class EffectLevelTests
{
    [Fact]
    public void LevelMeters_ShowTheSignalBeforeAndAfterTheEffect()
    {
        using var host = new TrackEffectsHost();
        var track = PatternFactory.CreatePattern("Pattern 1", "Test", "01").Tracks[0];
        track.Effects.Clear();
        var limiter = new TrackEffectViewModel(EffectCatalog.Get(EffectKind.Limiter));
        limiter.Parameters[0].Value = -12;
        track.Effects.Add(limiter);
        var chain = host.ChainFor(track)!;

        var samples = new float[4800 * 2];
        for (var i = 0; i < 4800; i++)
        {
            samples[i * 2] = samples[i * 2 + 1] = (float)(0.9 * Math.Sin(2 * Math.PI * 440 * i / 48000.0));
        }
        chain.Process(samples, 4800, 2, 48000);

        var (input, output) = chain.TakeLevels(0);
        Assert.Equal(0.9, input, 2);
        Assert.InRange(output, 0.2, 0.252);
        Assert.Equal((0.0, 0.0), chain.TakeLevels(0)); // read once, then measured afresh
    }

    [Fact]
    public void Meter_Scale_RunsFromMinus48ToZeroDecibels()
    {
        Assert.Equal(1, TrackEffectViewModel.ToMeter(1.0), 6);
        Assert.Equal(0.5, TrackEffectViewModel.ToMeter(Math.Pow(10, -24 / 20.0)), 6);
        Assert.Equal(0, TrackEffectViewModel.ToMeter(0.0001));
        Assert.True(new TrackEffectViewModel(EffectCatalog.Get(EffectKind.Compress)).HasLevelMeters);
        Assert.False(new TrackEffectViewModel(EffectCatalog.Get(EffectKind.Verb)).HasLevelMeters);
    }
}
