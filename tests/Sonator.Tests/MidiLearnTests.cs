using Sonator.Services;
using Sonator.ViewModels;
using Xunit;

namespace Sonator.Tests;

public sealed class MidiLearnTests
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
    public void AKnobOfTheKeyboard_IsAssignedToTheControlLastMoved_AndMovesIt()
    {
        OnSta(() =>
        {
            using var viewModel = new MainViewModel();
            var track = viewModel.Tracks[1];
            viewModel.RunAutomation(); // as its timer does: the tracks are watched for the controls moved

            // Nothing moved yet: nothing to learn.
            viewModel.IsMidiLearning = true;
            viewModel.OnMappedController(new MidiControllerMessage(0, 21, 0.2));
            Assert.Empty(viewModel.MidiMappings);

            track.Volume = 0.5;
            viewModel.OnMappedController(new MidiControllerMessage(0, 21, 0.2));
            var mapping = Assert.Single(viewModel.MidiMappings);
            Assert.False(viewModel.IsMidiLearning);
            Assert.Equal(AutomationTarget.Volume, mapping.Target);
            Assert.Equal($"CC 21 (channel 1) → {track.Name} volume", mapping.Describe());

            viewModel.OnMappedController(new MidiControllerMessage(0, 21, 0.9));
            Assert.Equal(0.9, track.Volume, 6);

            // Saved with the project.
            var saved = Assert.Single(viewModel.BuildProjectDocument().MidiMappings!);
            Assert.Equal(21, saved.Controller);
            Assert.Equal(track.BusId, saved.Track);

            viewModel.ForgetMidiMapping(mapping);
            Assert.Empty(viewModel.MidiMappings);
        });
    }

    [Fact]
    public void AnEffectsKnob_CanBeAssigned()
    {
        OnSta(() =>
        {
            using var viewModel = new MainViewModel();
            var track = viewModel.Tracks[0];
            var verb = track.Effects[0];
            viewModel.RunAutomation();
            verb.Parameters[4].Position = 0.3; // Mix, turned by hand

            viewModel.IsMidiLearning = true;
            viewModel.OnMappedController(new MidiControllerMessage(2, 74, 0.5));
            Assert.Equal(AutomationTarget.Parameter, Assert.Single(viewModel.MidiMappings).Target);

            viewModel.OnMappedController(new MidiControllerMessage(2, 74, 0.8));
            Assert.Equal(0.8, verb.Parameters[4].Position, 3);
        });
    }
}
