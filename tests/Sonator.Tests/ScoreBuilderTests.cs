using Sonator.Services.Score;
using Xunit;

namespace Sonator.Tests;

public sealed class ScoreBuilderTests
{
    private static ScoreSheet Build(params (double Start, double Length, int Note)[] notes) =>
        ScoreBuilder.Build(notes, 4, 4, 16);

    private static string Rhythm(ScoreBar bar) =>
        string.Join(" ", bar.Events.Select(item => (item.IsRest ? "r" : "n") + item.Duration + (item.TieToNext ? "~" : "")));

    [Fact]
    public void FourQuarters_AreFourQuarterNotes()
    {
        var sheet = Build((0, 4, 60), (4, 4, 62), (8, 4, 64), (12, 4, 65));

        Assert.Single(sheet.Bars);
        Assert.Equal("n4 n4 n4 n4", Rhythm(sheet.Bars[0]));
        Assert.Equal(Clef.Treble, sheet.Clef);
        Assert.Equal(0, sheet.KeyFifths);
    }

    [Fact]
    public void Gaps_AreRests_AndAnEmptyBarIsAWholeBarRest()
    {
        var sheet = ScoreBuilder.Build([(4, 2, 60)], 4, 4, 32);

        Assert.Equal("r4 n2 r2 r8", Rhythm(sheet.Bars[0]));
        Assert.True(sheet.Bars[1].Events.Single().IsWholeBarRest);
    }

    [Fact]
    public void ANoteOverTheBarLine_IsTiedAcrossIt()
    {
        var sheet = ScoreBuilder.Build([(12, 8, 67)], 4, 4, 32);

        Assert.Equal("r8 r4 n4~", Rhythm(sheet.Bars[0]));
        Assert.Equal("n4 r4 r8", Rhythm(sheet.Bars[1]));
        Assert.True(sheet.Bars[1].Events[0].ContinuesTie);
    }

    [Fact]
    public void AHalfNoteOffTheMiddle_IsTwoTiedQuarters()
    {
        var sheet = Build((4, 8, 60));

        Assert.Equal("r4 n4~ n4 r4", Rhythm(sheet.Bars[0]));
    }

    [Fact]
    public void InSixEight_ADottedQuarterSitsOnEachBeat()
    {
        var sheet = ScoreBuilder.Build([(0, 6, 60), (6, 6, 62)], 6, 8, 12);

        Assert.Equal("n6 n6", Rhythm(sheet.Bars[0]));
    }

    [Fact]
    public void NotesTogether_AreAChord()
    {
        var sheet = Build((0, 16, 60), (0, 16, 64), (0, 16, 67));

        Assert.Equal(new[] { 60, 64, 67 }, sheet.Bars[0].Events[0].Pitches.Select(pitch => pitch.MidiNote));
        Assert.Equal(16, sheet.Bars[0].Events[0].Duration);
    }

    [Fact]
    public void LowNotes_GetTheBassClef()
    {
        Assert.Equal(Clef.Bass, Build((0, 4, 40), (4, 4, 43)).Clef);
    }

    [Fact]
    public void TheKey_IsFoundFromTheNotes_AndAccidentalsShowOncePerBar()
    {
        // D major: D E F# G A B C#; then a C natural and C# again.
        var sheet = ScoreBuilder.Build([(0, 2, 62), (2, 2, 64), (4, 2, 66), (6, 2, 67), (8, 2, 69), (10, 2, 71), (12, 2, 73), (14, 2, 74),
            (16, 2, 72), (18, 2, 72), (20, 4, 73)], 4, 4, 32);

        Assert.Equal(2, sheet.KeyFifths);
        Assert.All(sheet.Bars[0].Events, item => Assert.Equal(Accidental.None, item.Pitches[0].Accidental));
        Assert.Equal(new[] { Accidental.Natural, Accidental.None, Accidental.Sharp },
            sheet.Bars[1].Events.Take(3).Select(item => item.Pitches[0].Accidental));
    }

    [Fact]
    public void FlatKeys_AreSpelledWithFlats()
    {
        // F major: B flat.
        var sheet = Build((0, 4, 65), (4, 4, 67), (8, 4, 69), (12, 4, 70));

        Assert.Equal(-1, sheet.KeyFifths);
        var bFlat = sheet.Bars[0].Events[3].Pitches[0];
        Assert.Equal(-1, bFlat.Alteration);
        Assert.Equal(Accidental.None, bFlat.Accidental);
    }

    [Fact]
    public void Words_AreSetASyllableToEachNoteStruck()
    {
        var sheet = ScoreLyrics.Apply(ScoreBuilder.Build([(0, 4, 60), (4, 8, 62), (12, 8, 64), (24, 4, 65)], 4, 4, 32), "Ciao a-mo _ re");

        var all = sheet.Bars.SelectMany(bar => bar.Events).ToList();
        Assert.All(all.Where(item => item.ContinuesTie), item => Assert.Null(item.Syllable)); // a note tied on takes none
        var sung = all.Where(item => !item.IsRest && !item.ContinuesTie).ToList();
        Assert.Equal("Ciao", sung[0].Syllable);
        Assert.Equal("a", sung[1].Syllable);
        Assert.True(sung[1].HyphenAfter);
        Assert.Equal("mo", sung[2].Syllable);
        Assert.Null(sung[3].Syllable); // the underscore leaves it out
    }

    [Fact]
    public void TheWords_AreSavedWithTheTrack()
    {
        var track = Sonator.ViewModels.PatternFactory.CreatePattern("Pattern 1", "Test", "01").Tracks[0];
        track.Lyrics = "Fra Mar-ti-no";

        var restored = Sonator.ViewModels.ProjectDocumentMapper.ApplyTrackDocument(Sonator.ViewModels.ProjectDocumentMapper.BuildTrackDocument(track));

        Assert.Equal("Fra Mar-ti-no", restored.Lyrics);
    }
}
