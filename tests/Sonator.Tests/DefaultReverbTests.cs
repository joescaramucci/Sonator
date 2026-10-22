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
    public void OpeningFx_SwitchesTheDefaultReverbOn_TheFirstTimeOnly()
    {
        OnSta(() =>
        {
            using var viewModel = new MainViewModel();
            var track = viewModel.Tracks[0];
            var verb = Assert.Single(track.Effects);
            Assert.False(verb.IsEnabled);

            Assert.True(viewModel.SwitchOnDefaultReverb(track));
            Assert.True(verb.IsEnabled);

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
