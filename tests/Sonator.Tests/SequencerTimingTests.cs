using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows.Threading;
using Sonator.Services;
using Sonator.ViewModels;
using Xunit;
using Xunit.Abstractions;

namespace Sonator.Tests;

// Measures how evenly the playback loop spaces steps. Step timestamps are taken when
// StepChanged is dispatched to the UI thread, so the numbers include dispatcher latency.
public sealed class SequencerTimingTests
{
    private const double TempoBpm = 120;
    private const double ExpectedIntervalMs = 60000.0 / TempoBpm / 4.0; // sixteenth note
    private static readonly TimeSpan MeasureDuration = TimeSpan.FromSeconds(6);

    private readonly ITestOutputHelper _output;

    public SequencerTimingTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public void PlaybackLoop_StepIntervals_StayCloseToTempo()
    {
        List<double> intervalsMs = new();
        Exception? failure = null;

        // WPF's Dispatcher needs an STA thread.
        var thread = new Thread(() =>
        {
            try
            {
                intervalsMs = MeasureStepIntervals(MeasureDuration);
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
            throw failure;
        }

        // The first interval can include start-up work, so drop it.
        var samples = intervalsMs.Skip(1).ToList();
        Assert.True(samples.Count >= 10, $"Too few steps captured: {samples.Count}.");

        var mean = samples.Average();
        var stdDev = Math.Sqrt(samples.Average(v => Math.Pow(v - mean, 2)));
        var maxDeviation = samples.Max(v => Math.Abs(v - ExpectedIntervalMs));

        _output.WriteLine($"Steps: {samples.Count}");
        _output.WriteLine($"Expected interval: {ExpectedIntervalMs:F2} ms");
        _output.WriteLine($"Mean interval:     {mean:F2} ms");
        _output.WriteLine($"Std deviation:     {stdDev:F2} ms");
        _output.WriteLine($"Max deviation:     {maxDeviation:F2} ms");

        // The loop schedules each step from an absolute timestamp, so the average should
        // match the tempo closely even when single steps are late.
        Assert.InRange(mean, ExpectedIntervalMs * 0.98, ExpectedIntervalMs * 1.02);
    }

    private static List<double> MeasureStepIntervals(TimeSpan duration)
    {
        var engine = new SequencerEngine();
        var timestamps = new List<long>();
        try
        {
            engine.TempoBpm = TempoBpm;
            engine.LoadPattern(CreatePattern(), loopEnabled: true);
            engine.StepChanged += (_, _) => timestamps.Add(Stopwatch.GetTimestamp());

            engine.Start();
            RunDispatcherFor(duration, engine);
        }
        finally
        {
            engine.Dispose();
        }

        return timestamps
            .Zip(timestamps.Skip(1), (previous, next) => (next - previous) * 1000.0 / Stopwatch.Frequency)
            .ToList();
    }

    private static void RunDispatcherFor(TimeSpan duration, SequencerEngine engine)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer { Interval = duration };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            engine.Stop();
            frame.Continue = false;
        };
        timer.Start();
        Dispatcher.PushFrame(frame);
    }

    private static PatternViewModel CreatePattern()
    {
        var steps = new ObservableCollection<StepViewModel>();
        for (var i = 0; i < 16; i++)
        {
            steps.Add(new StepViewModel(i, isActive: i % 4 == 0));
        }

        var track = new PatternTrackViewModel("Track 1", "Test", 0, 60, steps);
        return new PatternViewModel("Timing", "Timing test", "A", new ObservableCollection<PatternTrackViewModel> { track });
    }
}
