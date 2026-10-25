using Sonator.ViewModels;
using Xunit;

namespace Sonator.Tests;

public sealed class KitLanesTests
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
    public void KitOnTheTimeline_ShowsEachPieceThatPlayed_UnderItsTrack_WithItsDrumsHits()
    {
        OnSta(() =>
        {
            using var viewModel = new MainViewModel();
            var track = viewModel.SelectedPattern!.Tracks[0];
            track.MidiNoteEvents = [new(0, 1, 36, 100), new(4, 1, 38, 100), new(8, 1, 42, 90), new(12, 1, 38, 100)];
            var snare = PatternTrackViewModel.CreateKitPiece(track, 1, "3/4");
            snare.Name = "Snare";
            snare.IsHeard = true;
            var room = PatternTrackViewModel.CreateKitPiece(track, 6, "13/14");
            room.Name = "Room";
            room.IsHeard = true;
            var silent = PatternTrackViewModel.CreateKitPiece(track, 4, "9/10");
            track.KitPieces.Add(snare);
            track.KitPieces.Add(room);
            track.KitPieces.Add(silent);

            viewModel.ToggleKitOnTimeline(track);

            var lanes = viewModel.TimelineTracks.ToList();
            var at = lanes.FindIndex(lane => ReferenceEquals(lane.SourceTrack, track));
            Assert.Same(snare, lanes[at + 1].SourceTrack);
            Assert.Same(room, lanes[at + 2].SourceTrack);
            Assert.DoesNotContain(lanes, lane => ReferenceEquals(lane.SourceTrack, silent));
            // The pattern plays again along the timeline, and so do its hits.
            Assert.Equal(new[] { 4.0, 12.0, 20.0, 28.0 }, lanes[at + 1].KitHits.Take(4));
            Assert.True(lanes[at + 2].KitHitsAreAll);
            Assert.Equal(2 * lanes[at + 1].KitHits.Count, lanes[at + 2].KitHits.Count);

            // A piece's lane selects its track.
            viewModel.SelectedTrack = snare;
            Assert.Same(track, viewModel.SelectedTrack);

            viewModel.ToggleKitOnTimeline(track);
            Assert.DoesNotContain(viewModel.TimelineTracks, lane => lane.IsKitPiece);
        });
    }
}
