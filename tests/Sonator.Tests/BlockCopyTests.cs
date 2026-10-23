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

    [Fact]
    public void PastedBlocks_StayApart_UntilJoined_AndUndoSplitsThemAgain()
    {
        OnStaThread(() =>
        {
            using var viewModel = new MainViewModel();
            var track = viewModel.SelectedPattern!.Tracks[0];
            track.MidiNoteEvents = FourBars();
            viewModel.SelectBlock(track);
            viewModel.CopySelectedNotes();
            viewModel.PasteNotes();

            Assert.Equal(new[] { new NoteBlock(0, 64), new NoteBlock(64, 64) }, viewModel.BlocksOf(track));
            Assert.Equal(2, viewModel.TimelineTracks[0].Blocks.Count(block => !block.IsRepeat));

            viewModel.SelectBlocks(viewModel.BlocksOf(track).Select(block => (track, block)));
            Assert.True(viewModel.CanJoinSelectedBlocks);
            viewModel.JoinSelectedBlocks();

            Assert.Equal(new[] { new NoteBlock(0, 128) }, viewModel.BlocksOf(track));
            Assert.Equal(32, track.MidiNoteEvents.Count);

            viewModel.UndoCommand.Execute(null);
            var restored = viewModel.SelectedPattern!.Tracks[0];
            Assert.Equal(2, viewModel.BlocksOf(restored).Count);
            viewModel.RedoCommand.Execute(null);
            Assert.Single(viewModel.BlocksOf(viewModel.SelectedPattern!.Tracks[0]));
        });
    }

    [Fact]
    public void MovingABlock_MovesOnlyItsNotes()
    {
        OnStaThread(() =>
        {
            using var viewModel = new MainViewModel();
            var track = viewModel.SelectedPattern!.Tracks[0];
            track.MidiNoteEvents = FourBars();
            viewModel.SelectBlock(track);
            viewModel.CopySelectedNotes();
            viewModel.PasteNotes();

            viewModel.MoveBlock(track, new NoteBlock(64, 64), 64);

            Assert.Equal(new[] { new NoteBlock(0, 64), new NoteBlock(128, 64) }, viewModel.BlocksOf(track));
            Assert.Equal(16, track.MidiNoteEvents.Count(note => note.StepIndex < 64));
            Assert.Equal(16, track.MidiNoteEvents.Count(note => note.StepIndex >= 128));
        });
    }

    [Fact]
    public void ABlock_KeepsItsLength_WithItsLastBarSilent()
    {
        var notes = new[] { new MidiNoteEventViewModel(0, 1, 36, 100) };

        Assert.Equal(new[] { new NoteBlock(0, 64) }, NoteBlocks.Of(new[] { new NoteBlock(0, 64) }, notes, 16));
    }

    [Fact]
    public void NotesWithoutBlocks_MakeABlockForEachRunOfBars()
    {
        var notes = new[]
        {
            new MidiNoteEventViewModel(0, 1, 36, 100),
            new MidiNoteEventViewModel(20, 1, 36, 100),
            new MidiNoteEventViewModel(52, 1, 36, 100),
        };

        Assert.Equal(new[] { new NoteBlock(0, 32), new NoteBlock(48, 16) }, NoteBlocks.Of(Array.Empty<NoteBlock>(), notes, 16));
    }

    [Fact]
    public void ANoteRecordedRightAfterABlock_MakesABlockOfItsOwn()
    {
        var notes = new[] { new MidiNoteEventViewModel(0, 1, 36, 100), new MidiNoteEventViewModel(64, 1, 36, 100) };

        Assert.Equal(new[] { new NoteBlock(0, 64), new NoteBlock(64, 16) }, NoteBlocks.Of(new[] { new NoteBlock(0, 64) }, notes, 16));
    }
}
