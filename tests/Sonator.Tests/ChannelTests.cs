using System.IO;
using Sonator.Models;
using Sonator.Services;
using Sonator.Services.Effects;
using Sonator.ViewModels;
using Xunit;

namespace Sonator.Tests;

public sealed class ChannelTests
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

    // A song of two sections: the second pattern with one track fewer, its first track named apart.
    private static MainViewModel TwoSections()
    {
        var viewModel = new MainViewModel();
        var document = viewModel.BuildProjectDocument();
        var verse = document.Patterns[0] with { Name = "Verse", Slot = "01" };
        var chorusTracks = verse.Tracks.Take(verse.Tracks.Count - 1)
            .Select((track, index) => index == 0 ? track with { Name = "Other name", Volume = 0.2 } : track)
            .ToList();
        var chorus = verse with { Name = "Chorus", Slot = "02", Tracks = chorusTracks };
        viewModel.LoadDocument(document with { Patterns = new List<PatternDocument> { verse, chorus } });
        return viewModel;
    }

    [Fact]
    public void ATrackIsOneChannelInEveryPattern_TheFirstPatternsLeading()
    {
        OnSta(() =>
            {
                using var viewModel = TwoSections();
                var verse = viewModel.Patterns[0];
                var chorus = viewModel.Patterns[1];

                // The pattern that lacked a track has it now; the first pattern's settings lead.
                Assert.Equal(verse.Tracks.Count, chorus.Tracks.Count);
                Assert.Equal(verse.Tracks[0].Name, chorus.Tracks[0].Name);
                Assert.Equal(verse.Tracks[0].Volume, chorus.Tracks[0].Volume);
                Assert.Same(verse.Tracks[0], viewModel.ChannelOf(chorus.Tracks[0]));
                Assert.Equal(verse.Tracks[0].BusId, chorus.Tracks[0].BusId);

                // A setting changed in one pattern is the channel's.
                chorus.Tracks[1].Volume = 0.4;
                chorus.Tracks[1].IsMuted = true;
                chorus.Tracks[1].Name = "Bass";
                Assert.Equal(0.4, verse.Tracks[1].Volume);
                Assert.True(verse.Tracks[1].IsMuted);
                Assert.Equal("Bass", verse.Tracks[1].Name);

                // The effects are the very same.
                Assert.Same(verse.Tracks[2].Effects, chorus.Tracks[2].Effects);
                chorus.Tracks[2].Effects.Add(new TrackEffectViewModel(EffectCatalog.Get(EffectKind.Delay)));
                Assert.Contains(verse.Tracks[2].Effects, effect => effect.Definition.Kind == EffectKind.Delay);

                // The notes stay each pattern's own.
                chorus.Tracks[1].MidiNoteEvents = new[] { new MidiNoteEventViewModel(0, 1, 40, 100) };
                Assert.Empty(verse.Tracks[1].MidiNoteEvents);
            });
    }

    [Fact]
    public void ATrackAddedOrDeleted_IsAddedToOrDeletedFromEveryPattern_AndUndoKeepsTheChannels()
    {
        OnSta(() =>
            {
                using var viewModel = TwoSections();
                var count = viewModel.Patterns[0].Tracks.Count;

                viewModel.AddTrackCommand.Execute("midi");
                Assert.All(viewModel.Patterns, pattern => Assert.Equal(count + 1, pattern.Tracks.Count));
                Assert.Same(viewModel.Patterns[0].Tracks[^1], viewModel.ChannelOf(viewModel.Patterns[1].Tracks[^1]));

                viewModel.SelectedTrack = viewModel.Tracks[0];
                viewModel.DeleteTrackCommand.Execute(null);
                Assert.All(viewModel.Patterns, pattern => Assert.Equal(count, pattern.Tracks.Count));

                viewModel.UndoCommand.Execute(null);
                Assert.All(viewModel.Patterns, pattern => Assert.Equal(count + 1, pattern.Tracks.Count));
                Assert.Same(viewModel.Patterns[0].Tracks[0].Effects, viewModel.Patterns[1].Tracks[0].Effects);
                viewModel.Patterns[1].Tracks[0].Pan = -0.5;
                Assert.Equal(-0.5, viewModel.Patterns[0].Tracks[0].Pan);
            });
    }
}
