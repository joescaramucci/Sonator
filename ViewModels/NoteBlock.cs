namespace Sonator.ViewModels;

// A block of a track's notes on the timeline: where it starts and how long it lasts, in sixteenth steps. A
// note belongs to the block it starts in. Blocks keep their bounds when they touch, so a block pasted after
// another stays a block of its own until the two are joined.
public readonly record struct NoteBlock(int StartStep, int LengthSteps)
{
    public int EndStep => StartStep + LengthSteps;

    public bool Holds(MidiNoteEventViewModel note) => note.StepIndex >= StartStep && note.StepIndex < EndStep;
}

public static class NoteBlocks
{
    // Where a note ends, in steps; a very short note counts as one step.
    public static double NoteEnd(MidiNoteEventViewModel note) =>
        note.StepIndex + Math.Max(1, note.StartOffset + note.DurationSteps + note.LengthFraction);

    // The blocks a track's notes show in: the track's own, and for the notes outside them a block for each run of
    // bars that has such notes, in whole bars. A run only joins a block it overlaps, not one it merely touches, so
    // blocks side by side stay apart. Blocks that overlap become one, and a block with no notes left goes. A track
    // whose notes have no blocks yet gets them this way.
    public static IReadOnlyList<NoteBlock> Of(IReadOnlyList<NoteBlock> blocks, IReadOnlyList<MidiNoteEventViewModel> notes, int stepsPerBar)
    {
        if (notes.Count == 0)
        {
            return Array.Empty<NoteBlock>();
        }

        stepsPerBar = Math.Max(1, stepsPerBar);
        var result = blocks.Where(block => block.LengthSteps > 0).ToList();
        var stored = result.ToList();
        NoteBlock? run = null;
        foreach (var note in notes.Where(note => !stored.Any(block => block.Holds(note))).OrderBy(note => note.StepIndex))
        {
            var start = Math.Max(0, note.StepIndex) / stepsPerBar * stepsPerBar;
            var end = Math.Max(start + stepsPerBar, (int)Math.Ceiling(NoteEnd(note) / stepsPerBar - 1e-6) * stepsPerBar);
            if (run is { } current && start <= current.EndStep)
            {
                run = new NoteBlock(current.StartStep, Math.Max(current.EndStep, end) - current.StartStep);
                continue;
            }

            if (run is { } finished)
            {
                result.Add(finished);
            }

            run = new NoteBlock(start, end - start);
        }

        if (run is { } last)
        {
            result.Add(last);
        }

        var merged = new List<NoteBlock>();
        foreach (var block in result.OrderBy(block => block.StartStep))
        {
            if (merged.Count > 0 && block.StartStep < merged[^1].EndStep)
            {
                var previous = merged[^1];
                merged[^1] = new NoteBlock(previous.StartStep, Math.Max(previous.EndStep, block.EndStep) - previous.StartStep);
            }
            else
            {
                merged.Add(block);
            }
        }

        return merged.Where(block => notes.Any(block.Holds)).ToList();
    }
}
