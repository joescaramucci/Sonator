using Sonator.ViewModels;
using Xunit;

namespace Sonator.Tests;

public sealed class BusTests
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
    public void Sends_AreSavedWithTheTrack()
    {
        var track = PatternFactory.CreatePattern("Pattern 1", "Test", "01").Tracks[0];
        var bus = Guid.NewGuid();
        track.SetSend(bus, 0.5);

        var restored = ProjectDocumentMapper.ApplyTrackDocument(ProjectDocumentMapper.BuildTrackDocument(track));

        Assert.Equal(0.5, restored.SendLevel(bus));
        Assert.Equal(track.BusId, restored.BusId);

        // A send turned all the way down is gone.
        restored.SetSend(bus, 0);
        Assert.Empty(restored.Sends);
    }

    [Fact]
    public void SendControl_ShowsDecibelsFromUnity()
    {
        var track = PatternFactory.CreatePattern("Pattern 1", "Test", "01").Tracks[0];
        var bus = PatternFactory.CreatePattern("Pattern 1", "Test", "01").Tracks[1];
        var send = new SendViewModel(track, bus);

        Assert.Equal("off", send.LevelDisplay);
        send.Level = 0.75;
        Assert.Equal(0.0.ToString("0.0"), send.LevelDisplay);
        send.Level = 0.375;
        Assert.StartsWith("-6", send.LevelDisplay);
    }

    [Fact]
    public void NewBus_HasAWetReverb_AndRemovingItTakesTheSendsAway()
    {
        OnSta(() =>
        {
            using var viewModel = new MainViewModel();
            var bus = viewModel.AddBus()!;
            Assert.Equal("Bus 1", bus.Name);
            var verb = Assert.Single(bus.Effects);
            Assert.True(verb.IsEnabled);
            Assert.Equal(100, verb.Parameters[4].Value);
            Assert.Equal("Bus 2", viewModel.AddBus()!.Name);

            var track = viewModel.Tracks[0];
            track.SetSend(bus.BusId, 0.6);
            viewModel.RemoveBus(bus);
            Assert.Equal(0, track.SendLevel(bus.BusId));
            Assert.Single(viewModel.Buses);

            // Undo brings the bus back, with the track's send to it.
            viewModel.UndoCommand.Execute(null);
            Assert.Equal(new[] { "Bus 1", "Bus 2" }, viewModel.Buses.Select(item => item.Name));
            Assert.Equal(0.6, viewModel.Tracks[0].SendLevel(viewModel.Buses[0].BusId));
        });
    }
}
