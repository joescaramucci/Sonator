namespace Sonator.Services.Score;

// Splits a word into its syllables, by the Italian rules, which do well enough for a song in English too: a single
// consonant goes with the vowel after it (a-mo-re); a double one splits (bel-lo); a cluster that can begin a word
// (pr, bl, ch, gn, sc, s before another consonant...) goes with the vowel after it (ca-pra, pa-sta); any other
// splits after its first consonant (can-to, par-te). Vowels together stay together (pie-de, buo-no), but two of
// a, e, o side by side are two syllables (po-e-ta).
public static class Syllables
{
    private const string Vowels = "aeiouàèéìíòóùúy";
    private const string Strong = "aeoàèéòó";

    // The clusters of two consonants that can start a syllable.
    private static readonly HashSet<string> Starts = new(StringComparer.Ordinal)
    {
        "bl", "br", "cl", "cr", "dr", "fl", "fr", "gl", "gr", "pl", "pr", "tr", "vr",
        "ch", "gh", "gn", "sc", "sh", "th", "ph", "wh", "qu",
    };

    public static IReadOnlyList<string> Of(string word)
    {
        if (word.Length <= 2)
        {
            return [word];
        }

        var lower = word.ToLowerInvariant();
        bool IsVowel(int i) => i >= 0 && i < lower.Length && Vowels.Contains(lower[i]);
        var cuts = new List<int>();

        // The vowels' places; between two vowel groups, the consonants decide where the cut falls.
        var i = 0;
        while (i < lower.Length && !IsVowel(i))
        {
            i++;
        }

        while (i < lower.Length)
        {
            // The vowel group, cut between two strong vowels.
            var groupStart = i;
            while (IsVowel(i + 1))
            {
                if (Strong.Contains(lower[i]) && Strong.Contains(lower[i + 1]))
                {
                    cuts.Add(i + 1);
                }

                i++;
            }

            var consonantsStart = i + 1;
            var next = consonantsStart;
            while (next < lower.Length && !IsVowel(next))
            {
                next++;
            }

            if (next >= lower.Length)
            {
                break; // the word ends in consonants: they stay with the last syllable
            }

            var count = next - consonantsStart;
            var cut = count switch
            {
                0 => consonantsStart,
                1 => consonantsStart,
                _ => ClusterCut(lower, consonantsStart, next),
            };
            if (cut > groupStart)
            {
                cuts.Add(cut);
            }

            i = next;
        }

        var parts = new List<string>();
        var from = 0;
        foreach (var cut in cuts.Distinct().OrderBy(cut => cut))
        {
            if (cut > from && cut < word.Length)
            {
                parts.Add(word[from..cut]);
                from = cut;
            }
        }

        parts.Add(word[from..]);
        return parts;
    }

    // Where consonants between two vowels split: before an s that leads others (pa-sta) or before a cluster that
    // can begin a word (ca-pra, ra-gno); otherwise after the first consonant (can-to, bel-lo).
    private static int ClusterCut(string word, int start, int end)
    {
        var cluster = word[start..end];
        if (cluster[0] == 's' && cluster.Length >= 2 && cluster[1] != 's')
        {
            return start;
        }

        if (cluster.Length == 2 && Starts.Contains(cluster))
        {
            return start;
        }

        if (cluster.Length == 3 && (cluster == "sch" || cluster == "str" || cluster == "spr" || cluster == "scr" || cluster == "spl"))
        {
            return start;
        }

        if (cluster.Length >= 3 && Starts.Contains(cluster[^2..]))
        {
            return end - 2;
        }

        return start + 1;
    }
}
