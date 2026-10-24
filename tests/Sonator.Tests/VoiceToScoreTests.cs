using Sonator.Services.Score;
using Xunit;

namespace Sonator.Tests;

public sealed class VoiceToScoreTests
{
    [Theory]
    [InlineData("amore", "a-mo-re")]
    [InlineData("bello", "bel-lo")]
    [InlineData("canto", "can-to")]
    [InlineData("pasta", "pa-sta")]
    [InlineData("capra", "ca-pra")]
    [InlineData("campanaro", "cam-pa-na-ro")]
    [InlineData("poeta", "po-e-ta")]
    [InlineData("piede", "pie-de")]
    [InlineData("Martino", "Mar-ti-no")]
    [InlineData("tu", "tu")]
    [InlineData("sogno", "so-gno")]
    [InlineData("finestra", "fi-ne-stra")]
    public void ItalianWords_SplitIntoTheirSyllables(string word, string expected)
    {
        Assert.Equal(expected, string.Join("-", Syllables.Of(word)));
    }

    [Fact]
    public void Words_GoOnTheNotesSungDuringThem()
    {
        var notes = new[]
        {
            new SungNote(0.0, 0.4, 62), new SungNote(0.5, 0.9, 64), new SungNote(1.0, 1.4, 66), new SungNote(1.5, 1.9, 62),
            new SungNote(2.0, 2.9, 66), new SungNote(3.0, 3.4, 67),
        };
        var words = new[]
        {
            new SungWord(0.0, 0.45, "Fra"), new SungWord(0.5, 1.95, "Martino,"), new SungWord(2.0, 3.2, "dormi"),
        };

        Assert.Equal("Fra Mar-ti-no dor-mi", LyricsAligner.Lyrics(notes, words));
    }

    [Fact]
    public void AWordHeldOverNotes_LeavesThemWithoutSyllables()
    {
        var notes = new[] { new SungNote(0, 0.3, 60), new SungNote(0.3, 0.6, 62), new SungNote(0.6, 0.9, 64), new SungNote(1, 1.3, 65) };
        var words = new[] { new SungWord(0, 0.95, "oh"), new SungWord(1, 1.3, "sì") };

        Assert.Equal("oh _ _ sì", LyricsAligner.Lyrics(notes, words));
    }

    // A sung line, made up: D4, E4, then D4 again after a short breath, a quarter-tone sharp, with vibrato.
    private static float[] Voice(int sampleRate)
    {
        (double From, double To, int Note)[] notes = [(0.10, 0.60, 62), (0.60, 1.10, 64), (1.20, 1.70, 62)];
        var samples = new float[(int)(1.9 * sampleRate)];
        var phase = 0.0;
        for (var i = 0; i < samples.Length; i++)
        {
            var time = i / (double)sampleRate;
            var current = notes.FirstOrDefault(note => time >= note.From && time < note.To);
            if (current.Note == 0)
            {
                continue;
            }

            var midi = current.Note + 0.25 + 0.15 * Math.Sin(2 * Math.PI * 5.5 * time);
            var hz = 440 * Math.Pow(2, (midi - 69) / 12);
            phase += 2 * Math.PI * hz / sampleRate;
            // A voice-like tone: a few harmonics.
            samples[i] = (float)(0.3 * Math.Sin(phase) + 0.15 * Math.Sin(2 * phase) + 0.08 * Math.Sin(3 * phase));
        }

        return samples;
    }

    [Fact]
    public void ASungLine_BecomesItsNotes_InTune()
    {
        var notes = VoicePitch.Notes(Voice(44100), 44100);

        Assert.Equal(new[] { 62, 64, 62 }, notes.Select(note => note.Note));
        Assert.InRange(notes[0].Start, 0.05, 0.2);
        Assert.InRange(notes[1].Start, 0.55, 0.7);
        Assert.InRange(notes[2].Start, 1.15, 1.3);
    }

    [Fact]
    public void AWideVibrato_StaysOneNote()
    {
        const int Rate = 44100;
        var samples = new float[(int)(1.2 * Rate)];
        var phase = 0.0;
        for (var i = (int)(0.1 * Rate); i < (int)(1.1 * Rate); i++)
        {
            var midi = 62.2 + 0.45 * Math.Sin(2 * Math.PI * 5.5 * i / Rate);
            phase += 2 * Math.PI * 440 * Math.Pow(2, (midi - 69) / 12) / Rate;
            samples[i] = (float)(0.3 * Math.Sin(phase) + 0.12 * Math.Sin(2 * phase));
        }

        var notes = VoicePitch.Notes(samples, Rate);

        Assert.Single(notes);
        Assert.Equal(62, notes[0].Note);
    }
}
