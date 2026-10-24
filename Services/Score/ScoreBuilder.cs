namespace Sonator.Services.Score;

// Turns a track's notes into a sheet: notes set on the sixteenth grid, those starting together as chords, rests
// in the gaps, each cut at the bar lines and into values a single symbol can show, tied where a sound goes on; a
// clef from the track's range and a key from the notes it uses.
public static class ScoreBuilder
{
    // The values a symbol can show, longest first: whole, dotted half, half, dotted quarter, quarter, dotted
    // eighth, eighth, sixteenth.
    private static readonly int[] Values = [16, 12, 8, 6, 4, 3, 2, 1];

    public static ScoreSheet Build(
        IReadOnlyList<(double Start, double Length, int Note)> notes,
        int numerator,
        int denominator,
        int totalSteps)
    {
        var stepsPerBeat = Math.Max(1, 16 / denominator);
        var stepsPerBar = Math.Max(1, numerator * stepsPerBeat);

        var placed = notes
            .Select(note => (Start: Math.Max(0, (int)Math.Round(note.Start)), Length: Math.Max(1, (int)Math.Round(note.Length)), note.Note))
            .ToList();
        var end = Math.Max(totalSteps, placed.Count == 0 ? 0 : placed.Max(note => note.Start + note.Length));
        var barCount = Math.Max(1, (int)Math.Ceiling(end / (double)stepsPerBar));

        var clef = ClefFor(placed.Select(note => note.Note).ToList());
        var key = KeyFor(placed.Select(note => (note.Note, note.Length)).ToList());

        // Chords: the notes starting together, sounding until the longest ends or the next chord starts; rests
        // in the gaps, to the end of the last bar.
        var onsets = placed.GroupBy(note => note.Start).OrderBy(group => group.Key).ToList();
        var segments = new List<(int Start, int End, IReadOnlyList<int> Notes)>();
        var position = 0;
        for (var i = 0; i < onsets.Count; i++)
        {
            var start = onsets[i].Key;
            var next = i + 1 < onsets.Count ? onsets[i + 1].Key : int.MaxValue;
            var soundEnd = start + Math.Min(onsets[i].Max(note => note.Length), next - start);
            if (start > position)
            {
                segments.Add((position, start, Array.Empty<int>()));
            }

            segments.Add((start, soundEnd, onsets[i].Select(note => note.Note).Distinct().OrderBy(note => note).ToList()));
            position = soundEnd;
        }

        var songEnd = barCount * stepsPerBar;
        if (position < songEnd)
        {
            segments.Add((position, songEnd, Array.Empty<int>()));
        }

        // Each bar's share of them, a sound going over a bar line tied across it; then cut into values and spelled.
        var compound = denominator == 8 && numerator % 3 == 0 && numerator > 3;
        var bars = new List<ScoreBar>();
        for (var bar = 0; bar < barCount; bar++)
        {
            var barStart = bar * stepsPerBar;
            var barEnd = barStart + stepsPerBar;
            var pieces = segments
                .Where(segment => segment.Start < barEnd && segment.End > barStart)
                .Select(segment =>
                {
                    var isNote = segment.Notes.Count > 0;
                    var from = Math.Max(segment.Start, barStart);
                    var to = Math.Min(segment.End, barEnd);
                    return (Start: from - barStart, Length: to - from, segment.Notes,
                        ContinuesTie: isNote && segment.Start < barStart, TieToNext: isNote && segment.End > barEnd);
                })
                .ToList();
            bars.Add(new ScoreBar(bar + 1, Spell(Split(pieces, stepsPerBar, compound), key)));
        }

        return new ScoreSheet(clef, key, numerator, denominator, stepsPerBar, stepsPerBeat, bars);
    }

    // Cuts each piece into values a symbol can show, from where it starts: a value only starts where a note of
    // its length could, so a long note off the beat is written as shorter tied notes. A bar of rest alone is a
    // whole-bar rest.
    private static List<(int Start, int Duration, IReadOnlyList<int> Notes, bool ContinuesTie, bool TieToNext, bool WholeBar)> Split(
        List<(int Start, int Length, IReadOnlyList<int> Notes, bool ContinuesTie, bool TieToNext)> pieces, int stepsPerBar, bool compound)
    {
        var result = new List<(int, int, IReadOnlyList<int>, bool, bool, bool)>();
        if (pieces.Count == 1 && pieces[0].Notes.Count == 0)
        {
            result.Add((0, stepsPerBar, pieces[0].Notes, false, false, true));
            return result;
        }

        foreach (var piece in pieces.Where(piece => piece.Length > 0))
        {
            var position = piece.Start;
            var remaining = piece.Length;
            var first = true;
            while (remaining > 0)
            {
                var isNote = piece.Notes.Count > 0;
                var value = Values.First(candidate => candidate <= remaining
                    && Fits(candidate, position, stepsPerBar, compound)
                    && (!IsDotted(candidate) || (isNote && candidate == remaining)));
                remaining -= value;
                result.Add((position, value, piece.Notes,
                    isNote && (piece.ContinuesTie || !first),
                    isNote && (remaining > 0 || piece.TieToNext),
                    false));
                position += value;
                first = false;
            }
        }

        return result;
    }

    // A dotted value is only written for a whole note-length (never cut and tied), and never for a rest.
    private static bool IsDotted(int value) => value is 12 or 6 or 3;

    // True when a value may start at a place in the bar and end within it: on a multiple of itself, a dotted
    // value on a multiple of its plain value (of itself in a compound time such as 6/8, where it is the beat).
    private static bool Fits(int value, int position, int stepsPerBar, bool compound)
    {
        if (position + value > stepsPerBar)
        {
            return false;
        }

        var align = value switch
        {
            12 or 6 or 3 => compound ? value : value / 3 * 2,
            _ => value,
        };
        return position % align == 0;
    }

    // Treble clef for a track that mostly plays from middle C up, bass below.
    internal static Clef ClefFor(IReadOnlyList<int> notes)
    {
        if (notes.Count == 0)
        {
            return Clef.Treble;
        }

        var sorted = notes.OrderBy(note => note).ToList();
        return sorted[sorted.Count / 2] >= 57 ? Clef.Treble : Clef.Bass;
    }

    // The key, as sharps (+) or flats (-) in its signature: the major key whose scale holds most of the music,
    // weighted by how long each note lasts, the one with fewer accidentals when they hold as much.
    internal static int KeyFor(IReadOnlyList<(int Note, int Length)> notes)
    {
        if (notes.Count == 0)
        {
            return 0;
        }

        var weight = new double[12];
        foreach (var (note, length) in notes)
        {
            weight[((note % 12) + 12) % 12] += length;
        }

        int[] major = [0, 2, 4, 5, 7, 9, 11];
        var best = 0;
        var bestScore = double.MinValue;
        for (var fifths = -6; fifths <= 6; fifths++)
        {
            var tonic = ((fifths * 7) % 12 + 12) % 12;
            var score = major.Sum(degree => weight[(tonic + degree) % 12]) - Math.Abs(fifths) * 1e-3;
            if (score > bestScore)
            {
                bestScore = score;
                best = fifths;
            }
        }

        return best;
    }

    // The letter (0 = C ... 6 = B) and alteration of each pitch class, spelled with sharps or with flats.
    private static readonly (int Letter, int Alteration)[] SharpNames =
        [(0, 0), (0, 1), (1, 0), (1, 1), (2, 0), (3, 0), (3, 1), (4, 0), (4, 1), (5, 0), (5, 1), (6, 0)];

    private static readonly (int Letter, int Alteration)[] FlatNames =
        [(0, 0), (1, -1), (1, 0), (2, -1), (2, 0), (3, 0), (4, -1), (4, 0), (5, -1), (5, 0), (6, -1), (6, 0)];

    // The alteration the key gives each letter: sharps in the order F C G D A E B, flats the other way.
    internal static int[] KeyAlterations(int fifths)
    {
        var alterations = new int[7];
        int[] sharpOrder = [3, 0, 4, 1, 5, 2, 6];
        for (var i = 0; i < Math.Min(7, Math.Abs(fifths)); i++)
        {
            alterations[fifths > 0 ? sharpOrder[i] : sharpOrder[6 - i]] = fifths > 0 ? 1 : -1;
        }

        return alterations;
    }

    internal static (int Diatonic, int Alteration) SpellNote(int midiNote, int fifths)
    {
        var pitchClass = ((midiNote % 12) + 12) % 12;
        var octave = midiNote / 12 - 1;
        var (letter, alteration) = fifths >= 0 ? SharpNames[pitchClass] : FlatNames[pitchClass];
        return (octave * 7 + letter, alteration);
    }

    // The bar's events with their notes spelled, and an accidental where the note differs from what the key, or
    // an earlier accidental on the same line or space in the bar, makes it. A tied note keeps the accidental of the
    // note it goes on from, and shows none.
    private static List<ScoreEvent> Spell(
        List<(int Start, int Duration, IReadOnlyList<int> Notes, bool ContinuesTie, bool TieToNext, bool WholeBar)> events, int fifths)
    {
        var key = KeyAlterations(fifths);
        var inBar = new Dictionary<int, int>(); // diatonic step -> alteration in force
        var result = new List<ScoreEvent>();
        foreach (var item in events)
        {
            var pitches = new List<ScorePitch>();
            foreach (var note in item.Notes)
            {
                var (diatonic, alteration) = SpellNote(note, fifths);
                var expected = inBar.TryGetValue(diatonic, out var current) ? current : key[((diatonic % 7) + 7) % 7];
                var accidental = Accidental.None;
                if (!item.ContinuesTie && alteration != expected)
                {
                    accidental = alteration switch { 1 => Accidental.Sharp, -1 => Accidental.Flat, _ => Accidental.Natural };
                }

                inBar[diatonic] = alteration;
                pitches.Add(new ScorePitch(note, diatonic, alteration, accidental));
            }

            result.Add(new ScoreEvent(item.Start, item.Duration, pitches, item.TieToNext, item.ContinuesTie) { IsWholeBarRest = item.WholeBar });
        }

        return result;
    }
}
