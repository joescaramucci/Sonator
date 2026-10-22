namespace Sonator.Models;

// A time signature such as 3/4 or 7/8. Sonator's steps are sixteenth notes, so a beat of the
// denominator's note value lasts 16 / Denominator steps and a bar Numerator beats.
public readonly record struct Meter(int Numerator, int Denominator)
{
    public static readonly Meter Common = new(4, 4);

    public static IReadOnlyList<int> Numerators { get; } = Enumerable.Range(1, 16).ToArray();

    public static IReadOnlyList<int> Denominators { get; } = new[] { 2, 4, 8, 16 };

    // The signatures offered as one-click choices.
    public static IReadOnlyList<Meter> Presets { get; } = new Meter[]
    {
        new(2, 4), new(3, 4), new(4, 4), new(5, 4), new(6, 4), new(7, 4),
        new(3, 8), new(5, 8), new(6, 8), new(7, 8), new(9, 8), new(12, 8),
        new(2, 2), new(3, 2),
    };

    public int StepsPerBeat => 16 / Denominator;

    public int StepsPerBar => Numerator * StepsPerBeat;

    public bool IsValid => Numerators.Contains(Numerator) && Denominators.Contains(Denominator);

    public override string ToString() => $"{Numerator}/{Denominator}";

    public static bool TryParse(string? text, out Meter meter)
    {
        meter = Common;
        var parts = text?.Split('/', StringSplitOptions.TrimEntries);
        if (parts is not { Length: 2 }
            || !int.TryParse(parts[0], out var numerator)
            || !int.TryParse(parts[1], out var denominator))
        {
            return false;
        }

        var parsed = new Meter(numerator, denominator);
        if (!parsed.IsValid)
        {
            return false;
        }

        meter = parsed;
        return true;
    }

    public static Meter ParseOrCommon(string? text) => TryParse(text, out var meter) ? meter : Common;
}
