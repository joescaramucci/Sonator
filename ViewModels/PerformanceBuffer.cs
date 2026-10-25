using System.Diagnostics;

namespace Sonator.ViewModels;

// Everything played on the MIDI keyboard, kept whether recording or not, so a good part played while trying it out
// can still be put on the track afterwards (CAPTURE), as if recording had been on. A part played with the song is
// placed where it was played: the last time round, when the loop went round. A part played with the song stopped is
// the notes since the last pause, placed from the playhead at the song's tempo.
public sealed class PerformanceBuffer
{
    // A pause this long ends a part played with the song stopped.
    public const double PauseSeconds = 3.0;

    private const int MaxEvents = 8192;

    // A key pressed or released, or a controller moved: when (Stopwatch ticks), where in the song (null when it was
    // not playing), in which pass of it (each start and each turn of the loop is a pass).
    private readonly record struct Played(long Ticks, double? Step, int Pass, bool IsController, bool IsOn, int Number, double Value);

    private readonly object _sync = new();
    private readonly List<Played> _played = new();

    // Any thread.
    public void Note(long ticks, double? step, int pass, int noteNumber, int velocity, bool isOn) =>
        Add(new Played(ticks, step, pass, false, isOn, noteNumber, velocity));

    public void Controller(long ticks, double? step, int pass, int controller, double value) =>
        Add(new Played(ticks, step, pass, true, false, controller, value));

    private void Add(Played played)
    {
        lock (_sync)
        {
            if (_played.Count >= MaxEvents)
            {
                _played.RemoveRange(0, MaxEvents / 2);
            }

            _played.Add(played);
        }
    }

    public sealed record Part(IReadOnlyList<MidiNoteEventViewModel> Notes, IReadOnlyList<MidiControllerEventViewModel> Controllers, bool WithSong);

    // Takes the last part played out of the buffer, as notes and controllers in steps: null when nothing was played.
    // A part played with the song stopped starts at fromStep; tempo turns its seconds into steps; grid, when set,
    // puts each note's start on it, as recording does.
    public Part? Take(double fromStep, double tempo, double? grid)
    {
        List<Played> part;
        List<Played> after;
        lock (_sync)
        {
            var lastOn = _played.FindLastIndex(played => played is { IsController: false, IsOn: true });
            if (lastOn < 0)
            {
                return null;
            }

            var first = FirstOfPart(lastOn);
            var last = LastOfPart(lastOn);
            part = _played.GetRange(first, last + 1 - first);
            after = _played.GetRange(last + 1, _played.Count - last - 1);
            _played.RemoveRange(0, last + 1); // taken: not taken again
        }

        var stepsPerSecond = Math.Max(1, tempo) / 60.0 * 4;
        var startTicks = part[0].Ticks;
        var withSong = part[^1].Step is not null;
        double StepOf(Played played) => played.Step ?? fromStep + (played.Ticks - startTicks) / (double)Stopwatch.Frequency * stepsPerSecond;
        double Snap(double step) => grid is { } size && size > 0 ? Math.Round(step / size) * size : step;

        var notes = new List<MidiNoteEventViewModel>();
        var all = part.Concat(after).ToList();
        for (var i = 0; i < part.Count; i++)
        {
            var on = part[i];
            if (on.IsController || !on.IsOn)
            {
                continue;
            }

            // Held until its release; one step when it was not released.
            var release = all.Skip(i + 1).FirstOrDefault(played => !played.IsController && !played.IsOn && played.Number == on.Number);
            var length = release.Ticks > on.Ticks
                ? Math.Max(0.05, (release.Ticks - on.Ticks) / (double)Stopwatch.Frequency * stepsPerSecond)
                : 1.0;
            var start = Math.Max(0, Snap(StepOf(on)));
            var whole = (int)Math.Floor(start);
            var wholeLength = (int)Math.Floor(length);
            notes.Add(new MidiNoteEventViewModel(whole, wholeLength, on.Number, (int)Math.Clamp(on.Value, 1, 127), start - whole, length - wholeLength));
        }

        var controllers = part
            .Where(played => played.IsController)
            .Select(played => new MidiControllerEventViewModel(Math.Max(0, StepOf(played)), played.Number, played.Value))
            .ToList();
        return new Part(notes, controllers, withSong);
    }

    // Where the part of the event at index begins and ends: with the song, the start and end of that pass; with
    // the song stopped, the pauses before and after it.
    private int FirstOfPart(int index)
    {
        var first = index;
        while (first > 0 && SamePart(_played[first - 1], _played[first], _played[index]))
        {
            first--;
        }

        return first;
    }

    private int LastOfPart(int index)
    {
        var last = index;
        while (last < _played.Count - 1 && SamePart(_played[last], _played[last + 1], _played[index]))
        {
            last++;
        }

        return last;
    }

    private static bool SamePart(Played earlier, Played later, Played of) => of.Step is not null
        ? earlier.Step is not null && later.Step is not null && earlier.Pass == of.Pass && later.Pass == of.Pass
        : earlier.Step is null && later.Step is null && (later.Ticks - earlier.Ticks) / (double)Stopwatch.Frequency < PauseSeconds;
}
