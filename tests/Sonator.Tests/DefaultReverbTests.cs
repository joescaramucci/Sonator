using Sonator.Services.Effects;
using Sonator.ViewModels;
using Xunit;

namespace Sonator.Tests;

public sealed class DefaultReverbTests
{
    private static void OnSta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                action();
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

    [Fact]
    public void SonVerbFromTheList_SwitchesTheDefaultReverbOn_InsteadOfAddingASecond()
    {
        OnSta(() =>
        {
            using var viewModel = new MainViewModel();
            var track = viewModel.Tracks[0];
            var verb = Assert.Single(track.Effects);

            Assert.Same(verb, viewModel.PutEffect(track, EffectCatalog.Get(EffectKind.Verb)));
            Assert.True(verb.IsEnabled);
            Assert.Single(track.Effects);

            // Once it is on, another SonVerb is a second one.
            viewModel.PutEffect(track, EffectCatalog.Get(EffectKind.Verb));
            Assert.Equal(2, track.Effects.Count);

            // Another effect goes after the reverb and leaves it off, now and when the rack is opened later.
            var other = viewModel.Tracks[1];
            var delay = viewModel.PutEffect(other, EffectCatalog.Get(EffectKind.Delay));
            Assert.Equal(new[] { EffectKind.Verb, EffectKind.Delay }, other.Effects.Select(effect => effect.Definition.Kind));
            Assert.False(other.Effects[0].IsEnabled);
            Assert.False(viewModel.SwitchOnDefaultReverb(other));
            Assert.False(other.Effects[0].IsEnabled);
            Assert.NotNull(delay);
        });
    }

    [Fact]
    public void OpeningFx_SwitchesTheDefaultReverbOn_TheFirstTimeOnly()
    {
        OnSta(() =>
        {
            using var viewModel = new MainViewModel();
            var track = viewModel.Tracks[0];
            var verb = Assert.Single(track.Effects);
            Assert.False(verb.IsEnabled);
            Assert.False(track.HasActiveEffects);
            var lit = new List<bool>();
            track.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(PatternTrackViewModel.HasActiveEffects))
                {
                    lit.Add(track.HasActiveEffects);
                }
            };

            Assert.True(viewModel.SwitchOnDefaultReverb(track));
            Assert.True(verb.IsEnabled);

            // The FX button lights while an effect is on, whether the rack is open or not.
            Assert.True(track.HasActiveEffects);
            Assert.Equal(new[] { true }, lit);

            // Switched off by the player, it stays off when the rack is opened again.
            verb.IsEnabled = false;
            Assert.False(viewModel.SwitchOnDefaultReverb(track));
            Assert.False(verb.IsEnabled);
        });
    }

    [Fact]
    public void OpeningFx_LeavesAReverbTheyChanged_AsItIs()
    {
        OnSta(() =>
        {
            using var viewModel = new MainViewModel();
            var track = viewModel.Tracks[1];
            track.Effects[0].Parameters[0].Value += 10; // the preset is now modified

            Assert.False(viewModel.SwitchOnDefaultReverb(track));
            Assert.False(track.Effects[0].IsEnabled);
        });
    }
}
