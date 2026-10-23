using Sonator.ViewModels;
using Xunit;

namespace Sonator.Tests;

public sealed class ExportLengthTests
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
    public void TheSongLasts_ToItsLastNote_RoundedUpToTheBar()
    {
        OnSta(() =>
        {
            using var viewModel = new MainViewModel();
            foreach (var track in viewModel.Tracks)
            {
                foreach (var step in track.Steps)
                {
                    step.IsActive = false;
                }
            }

            Assert.Equal(16, viewModel.ExportLengthSteps()); // an empty song is a bar

            // A note from bar 2 into bar 3 makes the song three bars long.
            viewModel.ReplaceTrackNotes(viewModel.Tracks[2], new[] { new MidiNoteEventViewModel(20, 14, 60, 100) });
            Assert.Equal(48, viewModel.ExportLengthSteps());

            // A muted track does not count.
            viewModel.Tracks[2].IsMuted = true;
            Assert.Equal(16, viewModel.ExportLengthSteps());
        });
    }
}
