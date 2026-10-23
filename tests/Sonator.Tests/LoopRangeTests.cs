using System.Collections.ObjectModel;
using System.Windows.Threading;
using Sonator.Services;
using Sonator.ViewModels;
using Xunit;

namespace Sonator.Tests;

public sealed class LoopRangeTests
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

    private static PatternViewModel Pattern(int steps)
    {
        var cells = new ObservableCollection<StepViewModel>(Enumerable.Range(0, steps).Select(i => new StepViewModel(i, false)));
        var track = new PatternTrackViewModel("Track 1", "Test", 0, 60, cells);
        return new PatternViewModel("Loop", "Loop test", "A", new ObservableCollection<PatternTrackViewModel> { track });
    }

    [Fact]
    public void ALoopOverPartOfThePattern_RepeatsOnlyThatPart()
    {
        OnStaThread(() =>
        {
            var engine = new SequencerEngine();
            var played = new List<int>();
            var looped = new List<int>();
            try
            {
                engine.TempoBpm = 600; // a step every 25 ms
                engine.LoadPattern(Pattern(32), loopEnabled: true);
                engine.SetLoopRange(4, 7);
                engine.IsLoopRangeActive = true;
                engine.SetPlaybackPosition(4);
                engine.StepChanged += (_, step) => played.Add(step);
                engine.Looped += (_, step) => looped.Add(step);
                engine.Start();

                var frame = new DispatcherFrame();
                var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
                timer.Tick += (_, _) =>
                {
                    timer.Stop();
                    engine.Stop();
                    frame.Continue = false;
                };
                timer.Start();
                Dispatcher.PushFrame(frame);
            }
            finally
            {
                engine.Dispose();
            }

            Assert.True(played.Count >= 12, $"only {played.Count} steps played");
            Assert.All(played, step => Assert.InRange(step, 4, 7));
            Assert.True(looped.Count >= 2);
            Assert.All(looped, step => Assert.Equal(4, step));
        });
    }

    [Fact]
    public void TheRangeChosenOnTheRuler_IsKeptWithTheProject()
    {
        OnStaThread(() =>
        {
            using var viewModel = new MainViewModel();
            viewModel.SetLoopRange(48, 16); // dragged from right to left: bars 2 and 3

            Assert.True(viewModel.HasLoopRange);
            Assert.Equal(16, viewModel.LoopStartStep);
            Assert.Equal(48, viewModel.LoopEndStep);
            Assert.Equal(33, viewModel.LoopRangeSteps);

            viewModel.ClearLoopRange();
            Assert.False(viewModel.HasLoopRange);
            Assert.Equal(0, viewModel.LoopRangeSteps);
        });
    }
}
