namespace Sonator.Services.Score;

// A word heard in a sung take, in seconds from its start.
public sealed record SungWord(double Start, double End, string Text);

// Writes the words heard in a take as lyrics for its notes, in the form Cromator reads (syllables split by hyphens,
// _ for a note without one): each word's syllables go on the notes that start while it is sung, one each; a word
// with fewer notes than syllables puts what is left on its last note, a word with more holds its last syllable
// over the rest; a note no word reaches is left without one.
public static class LyricsAligner
{
    public static string Lyrics(IReadOnlyList<SungNote> notes, IReadOnlyList<SungWord> words)
    {
        var syllableOf = new string?[notes.Count];
        var hyphenAfter = new bool[notes.Count];
        var used = new bool[notes.Count];

        foreach (var word in words)
        {
            var text = Clean(word.Text);
            if (text.Length == 0)
            {
                continue;
            }

            // The notes starting within the word, a little before its start allowed for; else the nearest free one.
            var span = Enumerable.Range(0, notes.Count)
                .Where(index => !used[index] && notes[index].Start >= word.Start - 0.12 && notes[index].Start < word.End)
                .ToList();
            if (span.Count == 0)
            {
                var nearest = Enumerable.Range(0, notes.Count)
                    .Where(index => !used[index])
                    .OrderBy(index => Math.Abs(notes[index].Start - word.Start))
                    .Cast<int?>()
                    .FirstOrDefault();
                if (nearest is null)
                {
                    continue;
                }

                span.Add(nearest.Value);
            }

            var syllables = Syllables.Of(text);
            for (var i = 0; i < span.Count && i < syllables.Count; i++)
            {
                var last = i == span.Count - 1;
                syllableOf[span[i]] = last ? string.Concat(syllables.Skip(i)) : syllables[i];
                hyphenAfter[span[i]] = !last && i < syllables.Count - 1;
                used[span[i]] = true;
            }

            foreach (var held in span.Skip(syllables.Count))
            {
                used[held] = true; // the last syllable held over it
            }
        }

        // The words written out: a note without a syllable is _, but none is written after the last syllable.
        var lastSung = Array.FindLastIndex(syllableOf, syllable => syllable is not null);
        var lyrics = new System.Text.StringBuilder();
        for (var i = 0; i <= lastSung; i++)
        {
            var piece = syllableOf[i] ?? ScoreLyrics.Skip;
            lyrics.Append(piece);
            lyrics.Append(hyphenAfter[i] && syllableOf[i] is not null ? "-" : " ");
        }

        return lyrics.ToString().Trim();
    }

    // A word as sung, without the punctuation the recogniser adds.
    private static string Clean(string text) =>
        new(text.Trim().Where(character => char.IsLetter(character) || character == '\'').ToArray());
}
