namespace Sonator.ViewModels;

// Note edits applied to a track's active steps. Inactive steps are never changed.
internal static class TrackEditOperations
{
    private const int MinNote = 0;
    private const int MaxNote = 127;
    private const int QuantizedVelocity = 96;

    public static void Transpose(PatternTrackViewModel track, int semitones)
    {
        // Read the base note before changing it. Steps without their own note fall back to the
        // original base note, so they move by the same amount as the rest of the track.
        var baseNote = track.NoteNumber;
        track.NoteNumber = ClampNote(baseNote + semitones);

        foreach (var step in track.Steps.Where(step => step.IsActive))
        {
            step.MidiNoteNumber = ClampNote((step.MidiNoteNumber ?? baseNote) + semitones);
        }

        if (track.MidiNoteEvents.Count > 0)
        {
            track.MidiNoteEvents = track.MidiNoteEvents
                .Select(note => note with { NoteNumber = ClampNote(note.NoteNumber + semitones) })
                .ToList();
        }
    }

    // Gives each active step a note, a one-step duration, and a fixed velocity.
    public static void QuantizeActiveSteps(PatternTrackViewModel track)
    {
        foreach (var step in track.Steps.Where(step => step.IsActive))
        {
            step.MidiNoteNumber ??= track.NoteNumber;
            step.DurationSteps = 1;
            step.Velocity = QuantizedVelocity;
        }
    }

    private static int ClampNote(int note)
    {
        return Math.Clamp(note, MinNote, MaxNote);
    }
}
