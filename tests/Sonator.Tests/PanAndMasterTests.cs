using Sonator.Services.Effects;
using Sonator.ViewModels;
using Xunit;

namespace Sonator.Tests;

public sealed class PanAndMasterTests
{
    private static PatternTrackViewModel NewTrack()
    {
        var track = PatternFactory.CreatePattern("Pattern 1", "Test", "01").Tracks[0];
        track.Effects.Clear();
        return track;
    }

    // The left and right peaks of a 0.9 sine on both sides after the chain, once a change of gain or pan
    // has finished gliding.
    private static (double Left, double Right) PeaksAfter(NativeEffectChain chain)
    {
        chain.Process(new float[960 * 2], 960, 2, 48000);
        var samples = new float[4800 * 2];
        for (var i = 0; i < 4800; i++)
        {
            samples[i * 2] = samples[i * 2 + 1] = (float)(0.9 * Math.Sin(2 * Math.PI * 440 * i / 48000.0));
        }

        chain.Process(samples, 4800, 2, 48000);
        return (samples.Where((_, i) => i % 2 == 0).Max(Math.Abs), samples.Where((_, i) => i % 2 == 1).Max(Math.Abs));
    }

    [Fact]
    public void Pan_TurnsDownTheOtherSide_AndLeavesTheCentreAsItIs()
    {
        using var host = new TrackEffectsHost();
        var track = NewTrack();
        var chain = host.ChainFor(track)!;

        var (left, right) = PeaksAfter(chain);
        Assert.Equal(0.9, left, 2);
        Assert.Equal(0.9, right, 2);

        track.Pan = 0.5;
        (left, right) = PeaksAfter(chain);
        Assert.Equal(0.45, left, 2);
        Assert.Equal(0.9, right, 2);

        track.Pan = -1;
        (left, right) = PeaksAfter(chain);
        Assert.Equal(0.9, left, 2);
        Assert.Equal(0.0, right, 3);
    }

    [Fact]
    public void GainChange_GlidesInsteadOfJumping()
    {
        using var host = new TrackEffectsHost();
        var track = NewTrack();
        var chain = host.ChainFor(track)!;
        var ones = Enumerable.Repeat(1f, 4800 * 2).ToArray();
        chain.Process(ones, 4800, 2, 48000);

        track.Volume = 0; // silent
        var samples = Enumerable.Repeat(1f, 4800 * 2).ToArray();
        chain.Process(samples, 4800, 2, 48000);

        // Over 10 ms (480 frames) the level falls step by step, and then stays at zero.
        Assert.InRange(samples[0], 0.99f, 1f);
        Assert.InRange(samples[240 * 2], 0.49f, 0.51f);
        Assert.Equal(0f, samples[480 * 2]);
        Assert.Equal(0f, samples[^1]);
    }

    [Fact]
    public void Pan_IsSavedWithTheTrack_AndShownAsAMixerDoes()
    {
        var track = NewTrack();
        track.Pan = -0.4;

        var restored = ProjectDocumentMapper.ApplyTrackDocument(ProjectDocumentMapper.BuildTrackDocument(track));

        Assert.Equal(-0.4, restored.Pan);
        Assert.Equal("L40", restored.PanDisplay);
        restored.Pan = 0.004;
        Assert.Equal("C", restored.PanDisplay);
        restored.Pan = 3;
        Assert.Equal("R100", restored.PanDisplay);
    }

    [Fact]
    public void MuteAndSolo_SilenceTheTracksThatMakeTheirOwnSound()
    {
        using var host = new TrackEffectsHost();
        var clip = NewTrack();
        var other = NewTrack();
        var chain = host.ChainFor(clip)!;
        Assert.Equal(0.9, PeaksAfter(chain).Left, 2);

        clip.IsMuted = true;
        Assert.Equal(0, PeaksAfter(chain).Left, 3);
        clip.IsMuted = false;

        other.IsSolo = true;
        host.IsSoloActive = true; // another track is soloed: this one is not heard
        Assert.Equal(0, PeaksAfter(chain).Left, 3);

        clip.IsSolo = true; // soloed too: heard again
        Assert.Equal(0.9, PeaksAfter(chain).Left, 2);
    }

    [Fact]
    public void MasterChain_KeepsUnityGain_WhateverTheMasterVolume()
    {
        using var host = new TrackEffectsHost();
        var master = NewTrack();
        master.Pan = 1; // the master has no pan of its own
        host.UseAsMaster(master);
        host.MasterVolume = 0.375;

        var (left, right) = PeaksAfter(host.ChainFor(master)!);

        Assert.Equal(0.9, left, 2);
        Assert.Equal(0.9, right, 2);
    }

    [Fact]
    public void MasterEffects_AreKeptByUndoAndRedo()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                using var viewModel = new MainViewModel();
                Assert.Empty(viewModel.MasterTrack.Effects);

                viewModel.AddEffect(viewModel.MasterTrack, EffectCatalog.Get(EffectKind.Limiter));
                Assert.True(viewModel.MasterTrack.HasActiveEffects);

                viewModel.UndoCommand.Execute(null);
                Assert.Empty(viewModel.MasterTrack.Effects);

                viewModel.RedoCommand.Execute(null);
                Assert.Equal("SonLimiter", Assert.Single(viewModel.MasterTrack.Effects).Name);
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null)
        {
            throw new Xunit.Sdk.XunitException(failure.ToString());
        }
    }
}
