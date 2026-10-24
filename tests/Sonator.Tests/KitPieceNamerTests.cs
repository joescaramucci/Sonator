using System.Collections.ObjectModel;
using System.Diagnostics;
using Sonator.ViewModels;
using Xunit;

namespace Sonator.Tests;

public sealed class KitPieceNamerTests
{
    [Fact]
    public void EachOutput_IsNamedAfterTheDrumHeardOnIt_ThroughTheBleed_AndOneWithEveryDrumIsTheRoom()
    {
        var track = new PatternTrackViewModel("Drums", "EZdrummer 3", 1, 36, new ObservableCollection<StepViewModel>());
        var snare = PatternTrackViewModel.CreateKitPiece(track, 1, "3/4");
        var hihat = PatternTrackViewModel.CreateKitPiece(track, 3, "7/8");
        var room = PatternTrackViewModel.CreateKitPiece(track, 6, "13/14");
        var mine = PatternTrackViewModel.CreateKitPiece(track, 2, "5/6");
        mine.Name = "My snare";
        foreach (var piece in new[] { snare, hihat, room, mine })
        {
            track.KitPieces.Add(piece);
        }

        var now = 0L;
        var namer = new KitPieceNamer(() => now);

        // A rock beat at 120 BPM, the meters read every twentieth of a second (ten reads a beat): the kick on 1, the
        // snare on 2, the hi-hat on the eighths. Each drum is heard on the read after it is struck, on its own
        // output and a little on the others (bleed); the room hears the kick and the snare about alike.
        bool kick = false, snareHit = false, hat = false;
        for (var read = 0; read < 600; read++)
        {
            now += Stopwatch.Frequency / 20;
            namer.Read(track, new[]
            {
                (snare, (snareHit ? 0.5 : 0) + (kick || hat ? 0.03 : 0.005)),
                (hihat, (hat ? 0.3 : 0) + (snareHit ? 0.12 : 0) + (kick ? 0.02 : 0) + 0.005), // its microphone hears the snare too
                (room, (kick ? 0.4 : 0) + (snareHit ? 0.35 : 0) + (hat ? 0.08 : 0) + 0.01),
                (mine, snareHit ? 0.5 : 0.01),
            });

            var step = read % 20;
            kick = step == 0;
            snareHit = step == 10;
            hat = step % 5 == 0;
            if (kick)
            {
                namer.Struck(track, 36, 100);
            }
            if (snareHit)
            {
                namer.Struck(track, 38, 100);
            }
            if (hat)
            {
                namer.Struck(track, 42, 90);
            }
        }

        Assert.Equal("Snare", snare.Name);
        Assert.Equal("Hi-hat", hihat.Name);
        Assert.Equal("Room", room.Name);
        Assert.Equal("My snare", mine.Name);
    }
}
