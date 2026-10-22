using Sonator.ViewModels;
using Xunit;

namespace Sonator.Tests;

public sealed class DeleteBlockTests
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
    public void Undo_KeepsThePatternSelectedInTheArrangement()
    {
        OnSta(() =>
        {
            using var viewModel = new MainViewModel();
            var bars = viewModel.SelectedArrangePatternLength;
            Assert.True(bars > 0);

            viewModel.ReplaceTrackNotes(viewModel.Tracks[0], new[] { new MidiNoteEventViewModel(0, 4, 60, 100) });
            viewModel.UndoCommand.Execute(null);

            // The arrangement is rebuilt by undo, and so is opening a project: its pattern stays selected.
            Assert.Equal(bars, viewModel.SelectedArrangePatternLength);
            Assert.NotEqual(string.Empty, viewModel.SelectedArrangePatternStart);
        });
    }

    [Fact]
    public void NewProject_SelectsItsPatternInTheArrangement()
    {
        OnSta(() =>
        {
            using var viewModel = new MainViewModel();
            var bars = viewModel.SelectedArrangePatternLength;

            viewModel.NewProjectCommand.Execute(null);

            Assert.Equal(bars, viewModel.SelectedArrangePatternLength);
        });
    }

    [Fact]
    public void Delete_RemovesTheClickedBlock_AndUndoBringsItBack()
    {
        OnSta(() =>
        {
            using var viewModel = new MainViewModel();
            var track = viewModel.Tracks[0];
            viewModel.ReplaceTrackNotes(track, new[]
            {
                new MidiNoteEventViewModel(0, 4, 60, 100),
                new MidiNoteEventViewModel(4, 4, 64, 100),
            });

            viewModel.SelectBlock(track);
            Assert.True(viewModel.DeleteSelectedBlock());

            Assert.Empty(track.MidiNoteEvents);
            Assert.DoesNotContain(viewModel.TimelineTracks, lane => lane.IsBlockSelected);

            viewModel.UndoCommand.Execute(null);
            var restored = viewModel.Tracks[0];
            Assert.Equal(2, restored.MidiNoteEvents.Count);
        });
    }

    [Fact]
    public void Delete_DoesNothing_WithoutAClickedBlock()
    {
        OnSta(() =>
        {
            using var viewModel = new MainViewModel();
            var track = viewModel.Tracks[0];
            viewModel.ReplaceTrackNotes(track, new[] { new MidiNoteEventViewModel(0, 4, 60, 100) });
            viewModel.SelectedTrack = track; // a selected track is not a clicked block

            Assert.False(viewModel.DeleteSelectedBlock());
            Assert.Single(track.MidiNoteEvents);
        });
    }
}
