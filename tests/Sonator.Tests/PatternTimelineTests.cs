using Sonator.Models;
using Sonator.ViewModels;
using Xunit;

namespace Sonator.Tests;

public sealed class PatternTimelineTests
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

    private static List<StepDocument> Bars(int bars) =>
        Enumerable.Range(0, bars * 16).Select(index => new StepDocument(index, false, null, 100, 1)).ToList();

    // Intro (2 bars), Verse (4 bars), Intro again: an arrangement of three sections.
    private static MainViewModel Song()
    {
        var viewModel = new MainViewModel();
        var document = viewModel.BuildProjectDocument();
        PatternDocument Section(string name, string slot, int bars) => document.Patterns[0] with
        {
            Name = name,
            Slot = slot,
            Tracks = document.Patterns[0].Tracks.Select(track => track with { Steps = Bars(bars) }).ToList(),
        };

        var grid = new[] { "01", "01", "02", "02", "02", "02", "01", "01" }
            .Select(slot => new[] { slot, string.Empty, string.Empty, string.Empty }).ToList();
        viewModel.LoadDocument(document with
        {
            Patterns = new List<PatternDocument> { Section("Intro", "01", 2), Section("Verse", "02", 4) },
            SongGrid = grid,
            ArrangeMode = true,
        });
        return viewModel;
    }

    [Fact]
    public void TheTimelineShowsThePatternSelected_NotTheWholeSong()
    {
        OnSta(() =>
        {
            using var viewModel = Song();
            Assert.True(viewModel.IsArrangeMode);

            viewModel.SelectedPattern = viewModel.Patterns[0];
            Assert.Equal(2, viewModel.TimelineBarNumbers.Count);

            viewModel.SelectedPattern = viewModel.Patterns[1];
            Assert.Equal(4, viewModel.TimelineBarNumbers.Count);
            Assert.Equal(32, viewModel.ShownSectionStart);
            Assert.Equal(32 + 5, viewModel.SongStepOf(5));
            Assert.Equal(5, viewModel.PatternStepOf(32 + 5));
            Assert.Equal(3, viewModel.PatternStepOf(96 + 3)); // the intro played again
        });
    }

    [Fact]
    public void Copy_PlacesThePatternAgain_AClone()
    {
        OnSta(() =>
        {
            using var viewModel = Song();
            var patterns = viewModel.Patterns.Count;
            viewModel.SelectedPattern = viewModel.Patterns[1];
            viewModel.SelectedArrangeCell = viewModel.SongArrangeRows[2].Cells[0];

            viewModel.CopyArrangePatternCommand.Execute(null);

            Assert.Equal(patterns, viewModel.Patterns.Count); // no new pattern
            var sections = viewModel.ArrangeSections().Select(section => section.Pattern.Name).ToList();
            Assert.Equal(new[] { "Intro", "Verse", "Verse", "Intro" }, sections);
        });
    }
}
