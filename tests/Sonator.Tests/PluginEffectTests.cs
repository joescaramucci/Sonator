using Sonator.Services.Effects;
using Sonator.Services.Vst3;
using Sonator.ViewModels;
using Xunit;

namespace Sonator.Tests;

public sealed class PluginEffectTests
{
    private static PatternTrackViewModel NewTrack()
    {
        var track = PatternFactory.CreatePattern("Pattern 1", "Test", "01").Tracks[0];
        track.Effects.Clear();
        return track;
    }

    [Fact]
    public void PluginEffect_IsSavedWithItsStateAndInstance()
    {
        var track = NewTrack();
        var effect = TrackEffectViewModel.ForPlugin(@"C:\VST3\Rev.vst3", "Rev", isEnabled: false, state: new byte[] { 1, 2, 3 });
        track.Effects.Add(effect);

        var restored = Assert.Single(ProjectDocumentMapper.ApplyTrackDocument(ProjectDocumentMapper.BuildTrackDocument(track)).Effects);

        Assert.True(restored.IsPlugin);
        Assert.False(restored.HasKnobs);
        Assert.Equal("Rev", restored.Name);
        Assert.Equal(@"C:\VST3\Rev.vst3", restored.PluginPath);
        Assert.Equal(new byte[] { 1, 2, 3 }, restored.PluginState);
        Assert.False(restored.IsEnabled);
        Assert.Equal(effect.InstanceId, restored.InstanceId); // undo goes on with the same loaded plug-in
    }

    [Fact]
    public void PastedTrack_GetsPluginInstancesOfItsOwn()
    {
        var source = NewTrack();
        source.Effects.Add(TrackEffectViewModel.ForPlugin(@"C:\VST3\Rev.vst3", "Rev"));
        var target = NewTrack();

        ProjectDocumentMapper.ApplyTrackDocumentToTrack(target, ProjectDocumentMapper.BuildTrackDocument(source));

        Assert.Equal(@"C:\VST3\Rev.vst3", Assert.Single(target.Effects).PluginPath);
        Assert.NotEqual(source.Effects[0].InstanceId, target.Effects[0].InstanceId);
    }

    [Fact]
    public void MissingPlugin_PassesTheSoundThrough_AndTheEffectsAfterItStillWork()
    {
        using var host = new TrackEffectsHost(new Vst3PluginHost());
        var track = NewTrack();
        var chain = host.ChainFor(track)!;
        var missing = TrackEffectViewModel.ForPlugin(@"C:\Nowhere\Missing.vst3", "Missing");
        var limiter = new TrackEffectViewModel(EffectCatalog.Get(EffectKind.Limiter));
        limiter.Parameters[0].Value = -12;
        track.Effects.Add(missing);
        track.Effects.Add(limiter);

        Assert.True(missing.IsPluginMissing);
        var samples = new float[9600 * 2];
        for (var i = 0; i < 9600; i++)
        {
            samples[i * 2] = samples[i * 2 + 1] = (float)(0.9 * Math.Sin(2 * Math.PI * 440 * i / 48000.0));
        }
        chain.Process(samples, 9600, 2, 48000);

        // The limiter, second in the rack, still holds the peaks near -12 dB.
        Assert.InRange(samples.Max(Math.Abs), 0.2, 0.252);
    }
}
