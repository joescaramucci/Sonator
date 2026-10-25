using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Windows.Threading;
using Sonator.Services;
using Sonator.ViewModels;
using Xunit;

namespace Sonator.Tests;

public sealed class PluginTransportTests
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
    public void ThePlugIns_AreToldWhereTheSongStartsAndJumps_ItsTempo_AndWhenItStops()
    {
        OnStaThread(() =>
        {
            var engine = new SequencerEngine();
            var told = new ConcurrentQueue<PluginTransport>();
            try
            {
                engine.TransportChanged = told.Enqueue;
                engine.StepsPerBar = 12; // 3/4
                engine.StepsPerBeat = 4;
                engine.LoadPattern(Pattern(32), loopEnabled: true);
                engine.SetLoopRange(4, 7);
                engine.IsLoopRangeActive = true;

                // Stopped: a new tempo and a new place are told at once.
                engine.UpdateTempo(600); // a step every 25 ms
                engine.SetPlaybackPosition(4);
                Assert.Equal(2, told.Count);
                var placed = told.Last();
                Assert.Equal(new PluginTransport(600, 3, 4, 1.0, false, true), placed);

                told.Clear();
                engine.Start();
                var frame = new DispatcherFrame();
                var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
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

            var all = told.ToList();

            // The start, at step 4 (a quarter note in), then only the loop going back, and the stop.
            Assert.Equal(new PluginTransport(600, 3, 4, 1.0, true, true), all[0]);
            Assert.True(all.Count >= 3, $"only {all.Count} told");
            Assert.All(all.Skip(1).SkipLast(1), transport => Assert.Equal(new PluginTransport(600, 3, 4, 1.0, true, true), transport));
            Assert.False(all[^1].Playing);
            Assert.False(all[^1].Jump);
        });
    }
}
