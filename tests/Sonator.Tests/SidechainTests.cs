using Sonator.Services.Effects;
using Sonator.ViewModels;
using Xunit;

namespace Sonator.Tests;

public sealed class SidechainTests
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
    public void TheCompressor_TakesASidechain_AndKeepsItsTrackWhenSaved()
    {
        var pattern = PatternFactory.CreatePattern("Pattern 1", "Test", "01");
        var bass = pattern.Tracks[1];
        var kick = pattern.Tracks[0];
        var compressor = new TrackEffectViewModel(EffectCatalog.Get(EffectKind.Compress));
        var changes = new List<int>();
        compressor.Changed += (_, index) => changes.Add(index);
        Assert.True(compressor.CanSidechain);
        Assert.False(new TrackEffectViewModel(EffectCatalog.Get(EffectKind.Verb)).CanSidechain);

        compressor.SidechainChoice = kick.BusId;
        Assert.Equal(kick.BusId, compressor.SidechainSource);
        Assert.Equal(new[] { TrackEffectViewModel.SidechainChange }, changes);
        bass.Effects.Add(compressor);

        var restored = ProjectDocumentMapper.ApplyTrackDocument(ProjectDocumentMapper.BuildTrackDocument(bass));
        Assert.Equal(kick.BusId, restored.Effects.Single(effect => effect.Definition.Kind == EffectKind.Compress).SidechainSource);

        compressor.SidechainChoice = Guid.Empty;
        Assert.Null(compressor.SidechainSource);
    }

    [Fact]
    public void TheRack_OffersTheOtherTracks_AndTheKitsPiecesThatPlayed()
    {
        OnSta(() =>
        {
            using var viewModel = new MainViewModel();
            var drums = viewModel.Tracks[0];
            var bass = viewModel.Tracks[1];
            var kick = PatternTrackViewModel.CreateKitPiece(drums, 0, "Kick");
            kick.Name = "Kick";
            kick.IsHeard = true;
            var silent = PatternTrackViewModel.CreateKitPiece(drums, 5, "Out 11/12");
            drums.KitPieces.Add(kick);
            drums.KitPieces.Add(silent);

            var choices = viewModel.SidechainChoices(bass);

            Assert.Equal("none", choices[0].Name);
            Assert.Equal(Guid.Empty, choices[0].Id);
            Assert.DoesNotContain(choices, choice => choice.Id == bass.BusId);
            Assert.Contains(choices, choice => choice.Id == kick.BusId && choice.Name == $"{drums.Name} · Kick");
            Assert.DoesNotContain(choices, choice => choice.Id == silent.BusId);
        });
    }
}
