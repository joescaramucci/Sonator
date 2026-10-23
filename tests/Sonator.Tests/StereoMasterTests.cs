using Sonator.ViewModels;
using Xunit;

namespace Sonator.Tests;

public sealed class StereoMasterTests
{
    private static void OnStaThread(Action test)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                test();
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
    public void LinkedFaders_MoveTogether_KeepingTheirDifference()
    {
        OnStaThread(() =>
        {
            using var viewModel = new MainViewModel();
            viewModel.IsMasterLinked = false;
            viewModel.MasterVolumeRight = 0.6;
            Assert.Equal(0.75, viewModel.MasterVolumeLeft, 6);

            viewModel.IsMasterLinked = true;
            viewModel.MasterVolumeLeft = 0.65;

            Assert.Equal(0.5, viewModel.MasterVolumeRight, 6);
            Assert.Equal(0.65, viewModel.MasterVolume, 6); // the higher side
            Assert.Equal("65 | 50", viewModel.MasterVolumeDisplay);
        });
    }

    [Fact]
    public void TheSingleMasterVolume_MovesBothFaders()
    {
        OnStaThread(() =>
        {
            using var viewModel = new MainViewModel();
            viewModel.IsMasterLinked = false;
            viewModel.MasterVolumeLeft = 0.5;

            viewModel.MasterVolume = 0.85;

            Assert.Equal(0.6, viewModel.MasterVolumeLeft, 6);
            Assert.Equal(0.85, viewModel.MasterVolumeRight, 6);
        });
    }

    [Theory]
    [InlineData(0.75, 0.75, 0)]
    [InlineData(0.75, 0.375, -0.5)] // the right side at half the left's
    [InlineData(0.25, 1.0, 0.75)] // the left side at a quarter of the right's
    [InlineData(0, 0, 0)]
    public void TheBalance_TurnsTheLowerSideDownByTheFadersRatio(double left, double right, double balance)
    {
        Assert.Equal(balance, MainViewModel.MasterBalanceOf(left, right), 6);
    }
}
