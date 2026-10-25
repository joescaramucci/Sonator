namespace Sonator.ViewModels;

// A choice in a quantize list: a grid size in sixteenth-note steps, a strength or a swing amount.
public sealed record QuantizeOption(string Label, double Value);

// A choice in the humanize list: how far a note's start may move, in steps, and its velocity.
public sealed record HumanizeOption(string Label, double TimingSteps, int Velocity);

// Moves notes toward a grid, as a DAW's quantize does. Positions and grid sizes are in sixteenth-note steps.
public static class NoteQuantizer
{
    // Straight values, dotted (D, one and a half times as long) and triplets (T, two thirds as long).
    public static IReadOnlyList<QuantizeOption> Grids { get; } = new[]
    {
        new QuantizeOption("1/1", 16),
        new QuantizeOption("1/2", 8),
        new QuantizeOption("1/2 dotted", 12),
        new QuantizeOption("1/2 triplet", 16.0 / 3.0),
        new QuantizeOption("1/4", 4),
        new QuantizeOption("1/4 dotted", 6),
        new QuantizeOption("1/4 triplet", 8.0 / 3.0),
        new QuantizeOption("1/8", 2),
        new QuantizeOption("1/8 dotted", 3),
        new QuantizeOption("1/8 triplet", 4.0 / 3.0),
        new QuantizeOption("1/16", 1),
        new QuantizeOption("1/16 dotted", 1.5),
        new QuantizeOption("1/16 triplet", 2.0 / 3.0),
        new QuantizeOption("1/32", 0.5),
        new QuantizeOption("1/32 triplet", 1.0 / 3.0),
        new QuantizeOption("1/64", 0.25),
    };

    // How far a note moves toward its grid point: 100% lands on it.
    public static IReadOnlyList<QuantizeOption> Strengths { get; } = new[]
    {
        new QuantizeOption("100%", 1.0),
        new QuantizeOption("75%", 0.75),
        new QuantizeOption("50%", 0.5),
        new QuantizeOption("25%", 0.25),
    };

    // How much every second grid point is delayed: 100% puts it on the triplet position, a shuffle.
    public static IReadOnlyList<QuantizeOption> Swings { get; } = new[]
    {
        new QuantizeOption("No swing", 0),
        new QuantizeOption("Swing 25%", 0.25),
        new QuantizeOption("Swing 50%", 0.5),
        new QuantizeOption("Swing 75%", 0.75),
        new QuantizeOption("Swing 100%", 1.0),
    };

    // Quantizes note starts, and their ends when quantizeEnds is set; otherwise each note keeps its length.
    public static IReadOnlyList<MidiNoteEventViewModel> Quantize(
        IEnumerable<MidiNoteEventViewModel> notes,
        double grid,
        double strength,
        double swing,
        bool quantizeEnds)
    {
        if (grid <= 0)
        {
            return notes.ToList();
        }

        return notes.Select(note =>
        {
            var start = note.StepIndex + note.StartOffset;
            var length = Math.Max(0.05, note.DurationSteps + note.LengthFraction);
            var newStart = Math.Max(0, Move(start, Target(start, grid, swing), strength));

            var newEnd = newStart + length;
            if (quantizeEnds)
            {
                var end = start + length;
                var target = Target(end, grid, swing);
                if (target <= newStart)
                {
                    target = newStart + grid;
                }

                newEnd = Math.Max(newStart + 0.05, Move(end, target, strength));
            }

            return ToNote(note, newStart, newEnd - newStart);
        }).ToList();
    }

    // How loosely Humanize plays the notes: how far a start may move (in steps) and a velocity change.
    public static IReadOnlyList<HumanizeOption> Humanizings { get; } = new[]
    {
        new HumanizeOption("Subtle", 0.06, 6),
        new HumanizeOption("Medium", 0.12, 12),
        new HumanizeOption("Loose", 0.22, 20),
    };

    // Swings the notes on the grid's off-beats (every second grid point, within a quarter of the grid) later by the
    // swing chosen (100% to the triplet position), leaving the others where they are, as a groove's swing does.
    public static IReadOnlyList<MidiNoteEventViewModel> Swing(IEnumerable<MidiNoteEventViewModel> notes, double grid, double swing)
    {
        if (grid <= 0 || swing <= 0)
        {
            return notes.ToList();
        }

        return notes.Select(note =>
        {
            var start = note.StepIndex + note.StartOffset;
            var index = Math.Round(start / grid);
            if (Math.Abs(index % 2) != 1 || Math.Abs(start - index * grid) > grid / 4)
            {
                return note;
            }

            var length = Math.Max(0.05, note.DurationSteps + note.LengthFraction);
            return ToNote(note, start + swing * grid / 3.0, length);
        }).ToList();
    }

    // Plays the notes as a hand would: each start a little early or late, each velocity a little softer or harder,
    // by up to the amounts chosen; a note's end moves with its start. The same seed gives the same result.
    public static IReadOnlyList<MidiNoteEventViewModel> Humanize(IEnumerable<MidiNoteEventViewModel> notes, HumanizeOption amount, int seed)
    {
        var random = new Random(seed);
        return notes.Select(note =>
        {
            // Two draws added make small changes likelier than large ones.
            double Spread() => (random.NextDouble() + random.NextDouble() - 1.0);
            var start = Math.Max(0, note.StepIndex + note.StartOffset + Spread() * amount.TimingSteps);
            var length = Math.Max(0.05, note.DurationSteps + note.LengthFraction);
            var velocity = (int)Math.Clamp(Math.Round(note.Velocity + Spread() * amount.Velocity), 1, 127);
            return ToNote(note with { Velocity = velocity }, start, length);
        }).ToList();
    }

    private static double Target(double position, double grid, double swing)
    {
        var index = Math.Round(position / grid);
        var target = index * grid;
        if (Math.Abs(index % 2) == 1)
        {
            target += swing * grid / 3.0;
        }

        return target;
    }

    private static double Move(double position, double target, double strength)
    {
        return position + Math.Clamp(strength, 0, 1) * (target - position);
    }

    private static MidiNoteEventViewModel ToNote(MidiNoteEventViewModel note, double start, double length)
    {
        var wholeStart = Math.Floor(start);
        var wholeLength = Math.Floor(length);
        return note with
        {
            StepIndex = (int)wholeStart,
            StartOffset = start - wholeStart,
            DurationSteps = (int)wholeLength,
            LengthFraction = length - wholeLength,
        };
    }
}
