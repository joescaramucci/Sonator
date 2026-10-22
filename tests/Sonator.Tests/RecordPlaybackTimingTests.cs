using System.Collections.ObjectModel;
using System.Diagnostics;
using Sonator.Services;
using Sonator.ViewModels;
using Xunit;
using Xunit.Abstractions;

namespace Sonator.Tests;

// Runs the real sequencer and checks the two halves of recording against the clock:
// the position recording reads while playback runs, and the time a recorded note is played back.
public sealed class RecordPlaybackTimingTests
{
    private const double TempoBpm = 120;
    private const double StepMs = 60000.0 / TempoBpm / 4.0; // a sixteenth note at 120 BPM: 125 ms

    private readonly ITestOutputHelper _output;

    public RecordPlaybackTimingTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public void LivePosition_FollowsTheClockWhilePlaying()
    {
        var samples = RunOnStaThread(() =>
        {
            using var engine = CreateEngine(Array.Empty<MidiNoteEventViewModel>(), out _);
            var start = Stopwatch.GetTimestamp();
            engine.Start();

            var readings = new List<(double ElapsedMs, double Position)>();
            foreach (var waitMs in new[] { 310, 400, 520, 260 })
            {
                Thread.Sleep(waitMs);
                readings.Add((Stopwatch.GetElapsedTime(start).TotalMilliseconds, engine.LivePosition));
            }

            engine.Stop();
            return readings;
        });

        foreach (var (elapsedMs, position) in samples)
        {
            var expected = elapsedMs / StepMs;
            _output.WriteLine($"after {elapsedMs:0.0} ms: position {position:0.000} steps, expected {expected:0.000}");
            Assert.InRange(position, expected - 0.2, expected + 0.2); // within 25 ms
        }
    }

    [Fact]
    public void RecordedNotes_ArePlayedAtTheirExactPositions()
    {
        // Positions between grid steps, as notes played without quantization are stored.
        var notes = new[]
        {
            new MidiNoteEventViewModel(2, 1, 60, 100, StartOffset: 0.4),   // 2.40 steps = 300.0 ms
            new MidiNoteEventViewModel(5, 0, 62, 100, StartOffset: 0.25, LengthFraction: 0.5), // 5.25 steps = 656.25 ms
            new MidiNoteEventViewModel(9, 2, 64, 100, StartOffset: 0.75),  // 9.75 steps = 1218.75 ms
        };

        var played = RunOnStaThread(() =>
        {
            using var engine = CreateEngine(notes, out var noteOns);
            var start = Stopwatch.GetTimestamp();
            engine.Start();
            Thread.Sleep(1600);
            engine.Stop();
            return noteOns.Select(item => (item.Note, Stopwatch.GetElapsedTime(start, item.Timestamp).TotalMilliseconds)).ToList();
        });

        Assert.Equal(3, played.Count);
        foreach (var note in notes)
        {
            var expectedMs = (note.StepIndex + note.StartOffset) * StepMs;
            var actualMs = played.Single(item => item.Note == note.NoteNumber).Item2;
            _output.WriteLine($"note {note.NoteNumber}: played at {actualMs:0.0} ms, expected {expectedMs:0.0} ms");
            Assert.InRange(actualMs, expectedMs - 15, expectedMs + 15);
        }
    }

    [Fact]
    public void LivePosition_DuringCountIn_CountsUpToTheStart()
    {
        var readings = RunOnStaThread(() =>
        {
            using var engine = CreateEngine(Array.Empty<MidiNoteEventViewModel>(), out _);
            var start = Stopwatch.GetTimestamp();
            _ = engine.StartAfterCountInAsync(1, null); // one bar: 16 steps, 2000 ms
            var result = new List<(double ElapsedMs, double Position)>();
            foreach (var waitMs in new[] { 1000, 1500 })
            {
                Thread.Sleep(waitMs);
                result.Add((Stopwatch.GetElapsedTime(start).TotalMilliseconds, engine.LivePosition));
            }

            engine.Stop();
            return result;
        });

        foreach (var (elapsedMs, position) in readings)
        {
            var expected = (elapsedMs - 2000) / StepMs; // negative before the first step, positive after
            _output.WriteLine($"after {elapsedMs:0.0} ms: position {position:0.000} steps, expected {expected:0.000}");
            Assert.InRange(position, expected - 0.3, expected + 0.3);
        }
    }

    [Fact]
    public void StopDuringCountIn_DoesNotStartPlayback()
    {
        var played = RunOnStaThread(() =>
        {
            using var engine = CreateEngine(new[] { new MidiNoteEventViewModel(0, 1, 60, 100) }, out var noteOns);
            _ = engine.StartAfterCountInAsync(1, null);
            Thread.Sleep(500);
            engine.Stop();
            Thread.Sleep(2500);
            return noteOns.Count;
        });

        Assert.Equal(0, played);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void CountInOnly_ClicksTheCountIn_ThenGoesQuiet(bool countInOnly, bool expectClicksAfter)
    {
        var (duringCountIn, afterCountIn) = RunOnStaThread(() =>
        {
            using var engine = CreateEngine(Array.Empty<MidiNoteEventViewModel>(), out _);
            engine.IsMetronomeOn = true;
            engine.IsClickCountInOnly = countInOnly;
            var clicks = 0;
            engine.ClickSound = _ => Interlocked.Increment(ref clicks);

            _ = engine.StartAfterCountInAsync(1, null); // one bar: four clicks over 2000 ms
            Thread.Sleep(1900);
            var counted = Volatile.Read(ref clicks);
            Thread.Sleep(2000);
            engine.Stop();
            return (counted, Volatile.Read(ref clicks) - counted);
        });

        Assert.Equal(4, duringCountIn);
        Assert.Equal(expectClicksAfter, afterCountIn > 0);
    }

    private static SequencerEngine CreateEngine(IReadOnlyList<MidiNoteEventViewModel> notes, out List<(int Note, long Timestamp)> noteOns)
    {
        var steps = new ObservableCollection<StepViewModel>(Enumerable.Range(0, 128).Select(index => new StepViewModel(index, false)));
        var track = new PatternTrackViewModel("Track 1", "Test", 1, 60, steps)
        {
            PluginFilePath = "test.vst3",
            MidiNoteEvents = notes,
        };
        var pattern = new PatternViewModel("Pattern 1", "Test", "01", new ObservableCollection<PatternTrackViewModel> { track });

        var received = new List<(int Note, long Timestamp)>();
        noteOns = received;
        var engine = new SequencerEngine { IsMetronomeOn = false };
        engine.PluginNoteRouter = (_, _, noteNumber, _, isOn) =>
        {
            if (isOn)
            {
                lock (received)
                {
                    received.Add((noteNumber, Stopwatch.GetTimestamp()));
                }
            }
            return true;
        };
        engine.UpdateTempo(TempoBpm);
        engine.LoadPattern(pattern);
        return engine;
    }

    // The engine captures the current dispatcher, which needs an STA thread.
    private static T RunOnStaThread<T>(Func<T> work)
    {
        T result = default!;
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                result = work();
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

        return result;
    }
}
