namespace Sonator.Services.Effects;

// SonArmo's music, as the native harmonizer works it out (SonEffects.cpp, Harmonizer): the names of its keys and
// intervals, and the harmony notes for a note sung, for showing them.
public static class Harmony
{
    public static IReadOnlyList<string> KeyNames { get; } = ["None", "C", "C#", "D", "Eb", "E", "F", "F#", "G", "Ab", "A", "Bb", "B"];

    // From -7 (an octave below) through 0 (no voice) to +7 (an octave above), in steps of the scale.
    public static IReadOnlyList<string> IntervalNames { get; } =
    [
        "Octave down", "7th down", "6th down", "5th down", "4th down", "3rd down", "2nd down", "Off",
        "2nd up", "3rd up", "4th up", "5th up", "6th up", "7th up", "Octave up",
    ];

    private static readonly int[] Major = [0, 2, 4, 5, 7, 9, 11];
    private static readonly int[] Minor = [0, 2, 3, 5, 7, 8, 10];
    private static readonly string[] NoteNames = ["C", "C#", "D", "Eb", "E", "F", "F#", "G", "Ab", "A", "Bb", "B"];

    // The semitones a voice moves the note sung: with no key (0), steps of the major scale from any note; in a key
    // (1 = C ... 12 = B), steps along its scale from the scale note nearest the voice, so a third is major or minor
    // as the scale has it there.
    public static int Shift(int sungNote, int steps, int key, bool minor)
    {
        if (steps == 0)
        {
            return 0;
        }

        if (key <= 0)
        {
            int[] semitones = [0, 2, 4, 5, 7, 9, 11, 12];
            var size = semitones[Math.Min(Math.Abs(steps), 7)];
            return steps < 0 ? -size : size;
        }

        var scale = minor ? Minor : Major;
        var tonic = key - 1;
        var best = sungNote;
        var bestDegree = 0;
        var bestDistance = int.MaxValue;
        for (var note = sungNote - 2; note <= sungNote + 2; note++)
        {
            var degree = Array.IndexOf(scale, ((note - tonic) % 12 + 12) % 12);
            if (degree >= 0 && Math.Abs(note - sungNote) < bestDistance)
            {
                bestDistance = Math.Abs(note - sungNote);
                best = note;
                bestDegree = degree;
            }
        }

        var index = bestDegree + steps;
        var octaves = (int)Math.Floor(index / 7.0);
        var target = best - scale[bestDegree] + scale[index - octaves * 7] + 12 * octaves;
        return target - best;
    }

    public static string NoteName(int note) => $"{NoteNames[((note % 12) + 12) % 12]}{note / 12 - 1}";
}
