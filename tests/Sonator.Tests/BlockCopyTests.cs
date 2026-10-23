using Sonator.ViewModels;
using Xunit;

namespace Sonator.Tests;

public sealed class BlockCopyTests
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

    // A four-bar drum block: a kick on every beat, the last hit on the last beat of bar 4.
    private static List<MidiNoteEventViewModel> FourBars() =>
        Enumerable.Range(0, 16).Select(beat => new MidiNoteEventViewModel(beat * 4, 1, 36, 100)).ToList();

    [Fact]
    public void Pasting_PutsTheCopyRightAfterTheBlock_InWholeBars()
    {
        OnStaThread(() =>
        {
            using var viewModel = new MainViewModel();
            var track = viewModel.SelectedPattern!.Tracks[0];
            track.MidiNoteEvents = FourBars();
            viewModel.SelectBlock(track);

            viewModel.CopySelectedNotes();
            viewModel.PasteNotes();
            viewModel.PasteNotes();

            Assert.Equal(48, track.MidiNoteEvents.Count);
            Assert.Equal(Enumerable.Range(0, 48).Select(beat => beat * 4), track.MidiNoteEvents.Select(note => note.StepIndex).OrderBy(step => step));
        });
    }

    [Fact]
    public void RepeatingABlock_CopiesTheOriginal_AndTheSongGrowsToPlayThem()
    {
        OnStaThread(() =>
        {
            using var viewModel = new MainViewModel();
            var pattern = viewModel.SelectedPattern!;
            var track = pattern.Tracks[0];
            track.MidiNoteEvents = FourBars();
            viewModel.SelectBlock(track);

            viewModel.RepeatBlock(track, first: true);
            viewModel.RepeatBlock(track, first: false);
            viewModel.RepeatBlock(track, first: false);

            Assert.Equal(64, track.MidiNoteEvents.Count); // four copies of four bars, not a growing block
            Assert.Equal(16 * 4 * 4 - 4, track.MidiNoteEvents.Max(note => note.StepIndex));
            Assert.Equal(16, TimelineFormatting.PatternBarCount(pattern));
            Assert.Equal(16, viewModel.SelectedArrangePatternLength);
        });
    }

    [Fact]
    public void APaste_IsOneUndoStep()
    {
        OnStaThread(() =>
        {
            using var viewModel = new MainViewModel();
            var track = viewModel.SelectedPattern!.Tracks[0];
            track.MidiNoteEvents = FourBars();
            viewModel.SelectBlock(track);
            viewModel.CopySelectedNotes();
            viewModel.PasteNotes();

            viewModel.UndoCommand.Execute(null);

            Assert.Equal(16, viewModel.SelectedPattern!.Tracks[0].MidiNoteEvents.Count);
        });
    }
}
