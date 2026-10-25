using Sonator.Services.Sounds;
using Sonator.ViewModels;
using Xunit;

namespace Sonator.Tests;

public sealed class KitPieceSoundTests
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

    private static SoundEntry Sound(string name, SoundKind kind = SoundKind.Preset) =>
        new(name, SoundCategories.Drums, "", "Drum Synth", @"C:\VST3\Missing Drum Synth.vst3", @"C:\Presets\" + name, "", "", "", kind);

    [Fact]
    public void AnotherSoundForTheKick_PlaysTheKicksNotesOnANewTrack_AndMutesTheKitsKick()
    {
        OnSta(() =>
        {
            using var viewModel = new MainViewModel();
            var kit = viewModel.Tracks[0];
            kit.MidiNoteEvents = new[]
            {
                new MidiNoteEventViewModel(0, 1, 36, 110),
                new MidiNoteEventViewModel(4, 1, 38, 100),
                new MidiNoteEventViewModel(2, 1, 42, 80),
                new MidiNoteEventViewModel(8, 1, 36, 120, StartOffset: 0.25),
            };
            var kick = PatternTrackViewModel.CreateKitPiece(kit, 0, "Kick");
            kick.Name = "Kick";
            var snare = PatternTrackViewModel.CreateKitPiece(kit, 1, "Snare");
            snare.Name = "Snare";
            kit.KitPieces.Add(kick);
            kit.KitPieces.Add(snare);
            var count = viewModel.Tracks.Count;

            var track = viewModel.ReplaceKitPiece(kick, Sound("Kick 808"));

            Assert.NotNull(track);
            Assert.Equal(count + 1, viewModel.Tracks.Count);
            Assert.Same(track, viewModel.SelectedTrack);
            Assert.StartsWith("Kick", track!.Name);
            Assert.Equal(new[] { (0, 36), (8, 36) }, track.MidiNoteEvents.Select(note => (note.StepIndex, note.NoteNumber)));
            Assert.Equal(0.25, track.MidiNoteEvents[1].StartOffset);
            Assert.True(kick.IsMuted);
            Assert.False(snare.IsMuted);
            Assert.False(kit.IsMuted);
        });
    }

    [Fact]
    public void APieceThatIsNotOneDrum_OrAGroove_TakesNoSound()
    {
        OnSta(() =>
        {
            using var viewModel = new MainViewModel();
            var kit = viewModel.Tracks[0];
            var room = PatternTrackViewModel.CreateKitPiece(kit, 3, "Room");
            room.Name = "Room";
            var snare = PatternTrackViewModel.CreateKitPiece(kit, 1, "Snare");
            snare.Name = "Snare";
            var count = viewModel.Tracks.Count;

            Assert.Null(viewModel.ReplaceKitPiece(room, Sound("Kick 808")));
            Assert.Null(viewModel.ReplaceKitPiece(snare, Sound("Rock Groove", SoundKind.Groove)));
            Assert.Equal(count, viewModel.Tracks.Count);
            Assert.False(snare.IsMuted);
        });
    }
}
