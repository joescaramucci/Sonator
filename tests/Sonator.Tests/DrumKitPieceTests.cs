using Sonator.ViewModels;
using Xunit;

namespace Sonator.Tests;

public sealed class DrumKitPieceTests
{
    private static PatternTrackViewModel DrumTrack()
    {
        var track = PatternFactory.CreatePattern("Pattern 1", "Test", "01").Tracks[0];
        track.PluginFilePath = @"C:\VST3\Toontrack\EZdrummer 3.vst3";
        return track;
    }

    [Theory]
    [InlineData("EZdrummer 3 3/4", "Out 3/4")]
    [InlineData("Kick", "Out Kick")]
    [InlineData("EZdrummer 3", "Out 2")]
    public void APiece_IsNamedAfterItsOutput_WithoutThePluginsName(string output, string name)
    {
        var piece = PatternTrackViewModel.CreateKitPiece(DrumTrack(), 1, output);

        Assert.Equal(name, piece.Name);
        Assert.Equal(1, piece.KitOutput);
        Assert.True(piece.IsKitPiece);
    }

    [Fact]
    public void TheTrack_HasAKit_AsSoonAsItsPluginHasOutputs_AndHearsIt_OnceAPiecePlays()
    {
        var track = DrumTrack();
        var raised = new List<string?>();
        track.PropertyChanged += (_, e) => raised.Add(e.PropertyName);
        Assert.False(track.HasKitPieces);

        var piece = PatternTrackViewModel.CreateKitPiece(track, 1, "Kick");
        track.KitPieces.Add(piece);
        Assert.True(track.HasKitPieces);
        Assert.False(track.HasHeardKitPieces);
        Assert.Contains(nameof(PatternTrackViewModel.HasKitPieces), raised);

        piece.IsHeard = true;

        Assert.True(track.HasHeardKitPieces);
        Assert.Contains(nameof(PatternTrackViewModel.HasHeardKitPieces), raised);
    }

    [Fact]
    public void ThePieces_AreSavedWithTheirTrack()
    {
        var track = DrumTrack();
        var kick = PatternTrackViewModel.CreateKitPiece(track, 1, "Kick");
        kick.Name = "Cassa";
        kick.Volume = 0.5;
        kick.IsHeard = true;
        track.KitPieces.Add(kick);
        track.KitPieces.Add(PatternTrackViewModel.CreateKitPiece(track, 2, "Snare"));

        var restored = ProjectDocumentMapper.ApplyTrackDocument(ProjectDocumentMapper.BuildTrackDocument(track));

        Assert.Equal(2, restored.KitPieces.Count);
        var restoredKick = restored.KitPieces[0];
        Assert.Equal("Cassa", restoredKick.Name);
        Assert.Equal(0.5, restoredKick.Volume);
        Assert.True(restoredKick.IsHeard);
        Assert.Equal(1, restoredKick.KitOutput);
        Assert.Same(restored, restoredKick.KitParent);
        Assert.False(restored.KitPieces[1].IsHeard);
        Assert.True(restored.HasHeardKitPieces);
    }

    [Fact]
    public void ASoloedPiece_SilencesTheRestOfTheKit_AndTheOtherTracks()
    {
        var pattern = PatternFactory.CreatePattern("Pattern 1", "Test", "01");
        var track = pattern.Tracks[0];
        var other = pattern.Tracks[1];
        var snare = PatternTrackViewModel.CreateKitPiece(track, 1, "Snare");
        var hihat = PatternTrackViewModel.CreateKitPiece(track, 3, "Hi-hat");
        track.KitPieces.Add(snare);
        track.KitPieces.Add(hihat);
        var host = new TrackEffectsHost();

        snare.IsSolo = true;
        host.IsSoloActive = pattern.Tracks.Any(item => item.IsSoloed);

        Assert.True(track.IsSoloed);
        Assert.False(host.IsSilenced(snare));
        Assert.True(host.IsSilenced(hihat));
        Assert.True(host.IsSilenced(track)); // the kick, the track's own output
        Assert.True(host.IsSilenced(other));

        track.IsSolo = true;
        Assert.False(host.IsSilenced(track));
        Assert.True(host.IsSilenced(hihat));
    }

    [Fact]
    public void TheMainOutputsPiece_MutesAndSolosTheTracksOwnOutput_OnceHeard()
    {
        var pattern = PatternFactory.CreatePattern("Pattern 1", "Test", "01");
        var track = pattern.Tracks[0];
        var kick = PatternTrackViewModel.CreateKitPiece(track, 0, "EZdrummer 3 1/2");
        var snare = PatternTrackViewModel.CreateKitPiece(track, 1, "Snare");
        track.KitPieces.Add(kick);
        track.KitPieces.Add(snare);
        var host = new TrackEffectsHost();
        Assert.True(kick.IsKitMainOutput);
        Assert.Same(kick, track.KitMainOutput);
        Assert.False(snare.IsKitMainOutput);

        // Not heard apart yet, it is the whole kit: its mute does nothing.
        kick.IsMuted = true;
        Assert.False(host.IsSilenced(track));

        kick.IsHeard = true;
        Assert.True(host.IsSilenced(track));
        Assert.False(host.IsSilenced(snare));

        kick.IsMuted = false;
        kick.IsSolo = true;
        host.IsSoloActive = pattern.Tracks.Any(item => item.IsSoloed);
        Assert.False(host.IsSilenced(track));
        Assert.True(host.IsSilenced(snare));

        kick.IsSolo = false;
        snare.IsSolo = true;
        Assert.True(host.IsSilenced(track));
        Assert.False(host.IsSilenced(snare));
    }
}
