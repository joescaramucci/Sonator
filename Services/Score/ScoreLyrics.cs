namespace Sonator.Services.Score;

// Sets a song's words under its notes, a syllable to each note struck (not to a note tied on from another, nor to a
// rest). Words are split at spaces and line ends, and into syllables where they are written with a hyphen
// ("a-mo-re"); a hyphen is then drawn between them. An underscore on its own leaves a note without a syllable, for a
// syllable held over several notes.
public static class ScoreLyrics
{
    public const string Skip = "_";

    // The syllables of the words, in order: each with whether a hyphen joins it to the next; null for a note left
    // without one.
    public static IReadOnlyList<(string? Text, bool HyphenAfter)> Syllables(string? words)
    {
        var result = new List<(string?, bool)>();
        foreach (var word in (words ?? string.Empty).Split((char[])[' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            if (word == Skip)
            {
                result.Add((null, false));
                continue;
            }

            var parts = word.Split('-', StringSplitOptions.RemoveEmptyEntries);
            for (var i = 0; i < parts.Length; i++)
            {
                result.Add((parts[i], i < parts.Length - 1));
            }
        }

        return result;
    }

    // The sheet with the words set under its notes. Words left over when the notes run out are not shown.
    public static ScoreSheet Apply(ScoreSheet sheet, string? words)
    {
        var syllables = Syllables(words);
        var next = 0;
        var bars = sheet.Bars.Select(bar => bar with
        {
            Events = bar.Events.Select(item =>
            {
                if (item.IsRest || item.ContinuesTie || next >= syllables.Count)
                {
                    return item;
                }

                var (text, hyphen) = syllables[next++];
                return item with { Syllable = text, HyphenAfter = hyphen };
            }).ToList(),
        }).ToList();
        return sheet with { Bars = bars };
    }
}
