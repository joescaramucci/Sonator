namespace Sonator.Services.Score;

// What Cromator writes on the staff: a track's notes as bars of notes and rests, in a clef and a key.

public enum Clef
{
    Treble,
    Bass,
}

// A note as written: its place on the staff (the diatonic step, C4 = 28, each step a line or a space), its
// alteration (-1 flat, +1 sharp) and the accidental printed before it, if any.
public sealed record ScorePitch(int MidiNote, int Diatonic, int Alteration, Accidental Accidental);

public enum Accidental
{
    None,
    Sharp,
    Flat,
    Natural,
}

// One note, chord or rest of a bar. Start and Duration are in sixteenths from the start of the bar; Duration is
// one a single symbol can show (1, 2, 3, 4, 6, 8, 12 or 16). A note tied to the next is the same sound going on.
public sealed record ScoreEvent(int Start, int Duration, IReadOnlyList<ScorePitch> Pitches, bool TieToNext, bool ContinuesTie)
{
    public bool IsRest => Pitches.Count == 0;

    // The syllable sung on it, set by ScoreLyrics, and whether a hyphen joins it to the next syllable.
    public string? Syllable { get; init; }

    public bool HyphenAfter { get; init; }

    // A rest filling the whole bar, drawn as a whole rest in its middle whatever the time signature.
    public bool IsWholeBarRest { get; init; }
}

public sealed record ScoreBar(int Number, IReadOnlyList<ScoreEvent> Events);

public sealed record ScoreSheet(
    Clef Clef,
    int KeyFifths,
    int Numerator,
    int Denominator,
    int StepsPerBar,
    int StepsPerBeat,
    IReadOnlyList<ScoreBar> Bars);
