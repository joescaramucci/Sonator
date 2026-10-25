using System.Diagnostics;

namespace Sonator.ViewModels;

// Names a drum plug-in's separate outputs after the drums heard on them, as the plug-in does not say. At each
// reading of the meters, each output's level is set against the kinds of drum the track struck just before, and
// a least-squares fit finds how much each kind adds to that output's level. Bleed (every microphone hears a little
// of the whole kit) and drums struck together are told apart this way: the snare's own output rises with the snare
// whatever else is struck. The outputs are then named together (see NameAll): each close microphone after its drum
// (Snare, Hi-hat...), the others Room. Only a piece still named by Sonator is renamed, never one the player has named.
internal sealed class KitPieceNamer
{
    internal static readonly string[] Kinds = ["Kick", "Snare", "Hi-hat", "Toms", "Crash", "Ride", "Percussion"];

    // How far back a drum struck counts for the level read now: the audio comes out a block or two after the
    // note goes in, and the meters are read every twentieth of a second.
    private static readonly long Window = Stopwatch.Frequency * 160 / 1000;

    // Readings only count while the track plays: within a second of a drum struck.
    private static readonly long Playing = Stopwatch.Frequency;

    // Readings before a first guess, how often a guess is made again, and how often a kind must have been struck
    // to be weighed.
    private const int ReadsBeforeNaming = 40;
    private const int ReadsBetweenGuesses = 20;
    private const int StrikesNeeded = 6;

    // How much a kind must add to the level to name the output, and the purity of a close microphone.
    private const double LevelNeeded = 0.01;
    private const double CloseMicPurity = 0.75;

    private readonly Func<long> _clock;
    private readonly object _sync = new();
    private readonly Dictionary<PatternTrackViewModel, List<(long Ticks, int Kind)>> _struck = new();
    private readonly Dictionary<PatternTrackViewModel, Fit> _fits = new();
    private readonly Dictionary<PatternTrackViewModel, string> _given = new();

    // The clock is Stopwatch ticks; tests give their own.
    public KitPieceNamer(Func<long>? clock = null)
    {
        _clock = clock ?? Stopwatch.GetTimestamp;
    }

    // The kind of drum a General MIDI note plays, as an index into Kinds, or -1 for none.
    internal static int KindOf(int note) => note switch
    {
        35 or 36 => 0,
        37 or 38 or 39 or 40 => 1,
        42 or 44 or 46 => 2,
        41 or 43 or 45 or 47 or 48 or 50 => 3,
        49 or 52 or 55 or 57 => 4,
        51 or 53 or 59 => 5,
        >= 54 and <= 81 => 6,
        _ => -1,
    };

    // The kind of drum a piece plays, by the name Sonator gave it (Snare, Snare 2, Hi-hat...), or -1 when it is not
    // one drum (Room, an output not yet named, a name of the player's).
    internal static int KindOfName(string name)
    {
        for (var kind = 0; kind < Kinds.Length; kind++)
        {
            if (name == Kinds[kind] || name.StartsWith(Kinds[kind] + " ", StringComparison.Ordinal))
            {
                return kind;
            }
        }

        return -1;
    }

    // The word to search the sounds with for a piece, by its name: "kick", "snare", "hat"...; null when the piece
    // is not one drum.
    internal static string? SearchWordOf(string name) => KindOfName(name) switch
    {
        0 => "kick",
        1 => "snare",
        2 => "hat",
        3 => "tom",
        4 => "crash",
        5 => "ride",
        6 => "perc",
        _ => null,
    };

    // A note the track's plug-in was sent. Any thread.
    public void Struck(PatternTrackViewModel track, int note, int velocity)
    {
        var kind = KindOf(note);
        if (velocity <= 0 || kind < 0)
        {
            return;
        }

        var now = _clock();
        lock (_sync)
        {
            if (!_struck.TryGetValue(track, out var struck))
            {
                _struck[track] = struck = new List<(long, int)>();
            }

            struck.RemoveAll(item => now - item.Ticks > Playing);
            struck.Add((now, kind));
        }
    }

    // One reading of the meters of a track's kit pieces, their peaks since the last. UI thread.
    public void Read(PatternTrackViewModel track, IReadOnlyList<(PatternTrackViewModel Piece, double Peak)> peaks)
    {
        var now = _clock();
        var present = new bool[Kinds.Length];
        lock (_sync)
        {
            if (!_struck.TryGetValue(track, out var struck) || !struck.Any(item => now - item.Ticks <= Playing))
            {
                return; // not playing
            }

            foreach (var (ticks, kind) in struck)
            {
                present[kind] |= now - ticks <= Window;
            }
        }

        var reads = 0;
        foreach (var (piece, peak) in peaks)
        {
            if (!_fits.TryGetValue(piece, out var fit))
            {
                _fits[piece] = fit = new Fit();
            }

            fit.Add(present, peak);
            reads = Math.Max(reads, fit.Reads);
        }

        if (reads >= ReadsBeforeNaming && reads % ReadsBetweenGuesses == 0)
        {
            NameAll(track, peaks.Select(item => item.Piece).ToList());
        }
    }

    // Names the kit's outputs together. Each plays the drum it hears most (its best kind); it is that drum's close
    // microphone when it hears little else (its share of what it hears, its purity, is high), or when no output hears
    // that drum more purely: a hi-hat microphone hears a good deal of the snare and the kick, the overheads more.
    // Any other output is the room.
    private void NameAll(PatternTrackViewModel track, IReadOnlyList<PatternTrackViewModel> pieces)
    {
        var heard = pieces
            .Select(piece => (Piece: piece, Parts: _fits.TryGetValue(piece, out var fit) ? fit.Parts() : null))
            .Where(item => item.Parts is not null)
            .ToList();
        double Purity(double[] parts, int kind)
        {
            var total = parts.Sum(part => Math.Max(0, part));
            return total <= 0 ? 0 : Math.Max(0, parts[kind]) / total;
        }

        foreach (var (piece, parts) in heard)
        {
            var best = Enumerable.Range(0, Kinds.Length).MaxBy(kind => parts![kind]);
            if (parts![best] < LevelNeeded)
            {
                continue;
            }

            var purity = Purity(parts, best);
            var purest = heard.All(other => ReferenceEquals(other.Piece, piece) || Purity(other.Parts!, best) <= purity);
            Rename(track, piece, purity >= CloseMicPurity || purest ? Kinds[best] : "Room");
        }
    }

    // A least-squares fit of one output's level on the kinds of drum struck: level = base + sum of each kind's part.
    private sealed class Fit
    {
        private const int Size = 8; // the base, then the kinds
        private readonly double[,] _sums = new double[Size, Size];
        private readonly double[] _levelSums = new double[Size];
        private readonly int[] _strikes = new int[Size];

        public int Reads { get; private set; }

        public void Add(bool[] present, double level)
        {
            Span<double> x = stackalloc double[Size];
            x[0] = 1;
            for (var kind = 0; kind < Kinds.Length; kind++)
            {
                x[kind + 1] = present[kind] ? 1 : 0;
                _strikes[kind + 1] += present[kind] ? 1 : 0;
            }

            for (var i = 0; i < Size; i++)
            {
                _levelSums[i] += x[i] * level;
                for (var j = 0; j < Size; j++)
                {
                    _sums[i, j] += x[i] * x[j];
                }
            }

            Reads++;
        }


        // How much each kind of drum adds to the output's level, for the kinds struck often enough to tell (0 for the
        // others), or null when it cannot be solved.
        public double[]? Parts()
        {
            if (Solve() is not { } solved)
            {
                return null;
            }

            return Enumerable.Range(0, Kinds.Length)
                .Select(kind => _strikes[kind + 1] >= StrikesNeeded ? solved[kind + 1] : 0)
                .ToArray();
        }

        // Each part, from the sums with a little ridge so kinds never struck, or always struck together, stay
        // small instead of breaking the sum; null if it cannot be solved.
        private double[]? Solve()
        {
            var a = new double[Size, Size + 1];
            for (var i = 0; i < Size; i++)
            {
                for (var j = 0; j < Size; j++)
                {
                    a[i, j] = _sums[i, j] + (i == j && i > 0 ? 1.0 : 0.0);
                }

                a[i, Size] = _levelSums[i];
            }

            for (var column = 0; column < Size; column++)
            {
                var pivot = column;
                for (var row = column + 1; row < Size; row++)
                {
                    if (Math.Abs(a[row, column]) > Math.Abs(a[pivot, column]))
                    {
                        pivot = row;
                    }
                }

                if (Math.Abs(a[pivot, column]) < 1e-12)
                {
                    return null;
                }

                for (var j = 0; j <= Size; j++)
                {
                    (a[column, j], a[pivot, j]) = (a[pivot, j], a[column, j]);
                }

                for (var row = 0; row < Size; row++)
                {
                    if (row == column)
                    {
                        continue;
                    }

                    var factor = a[row, column] / a[column, column];
                    for (var j = column; j <= Size; j++)
                    {
                        a[row, j] -= factor * a[column, j];
                    }
                }
            }

            var parts = new double[Size];
            for (var i = 0; i < Size; i++)
            {
                parts[i] = a[i, Size] / a[i, i];
            }

            return parts;
        }
    }

    // Gives the piece the name, when Sonator named it so far, apart from its fellow pieces: a second snare output
    // is Snare 2.
    private void Rename(PatternTrackViewModel track, PatternTrackViewModel piece, string name)
    {
        var given = _given.GetValueOrDefault(piece);
        var namedBySonator = piece.Name.StartsWith("Out ", StringComparison.Ordinal) || piece.Name == given;
        if (!namedBySonator || (given is not null && (given == name || given.StartsWith(name + " ", StringComparison.Ordinal))))
        {
            return;
        }

        var taken = track.KitPieces.Where(other => !ReferenceEquals(other, piece)).Select(other => other.Name).ToHashSet();
        var unique = name;
        for (var number = 2; taken.Contains(unique); number++)
        {
            unique = $"{name} {number}";
        }

        piece.Name = unique;
        _given[piece] = unique;
    }
}
