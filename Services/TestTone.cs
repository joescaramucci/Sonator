namespace Sonator.Services;

/// <summary>
/// The line-up tone of the master's oscillator, as on a studio desk: a steady sine at 100 Hz, 1 kHz, 10 kHz or
/// 20 kHz. The master's VU meters always see it at the reference level, -18 dBFS, to set their 0 against
/// (it reads 0 VU on a meter calibrated the usual way); its LEVEL only sets how loud it is heard. It is one
/// second of sound played round and round: each frequency fits a whole number of times in it, so it loops
/// without a click.
/// </summary>
public static class TestTone
{
    public const int SampleRate = 48000;

    public static IReadOnlyList<int> Frequencies { get; } = new[] { 100, 1000, 10000, 20000 };

    // The level the meters see the tone at, whatever it is heard at.
    public const double ReferenceLevelDbfs = -18;

    public const double DefaultLevelDbfs = -18;
    public const double MinLevelDbfs = -30;
    public const double MaxLevelDbfs = 0;

    // One second of the sine at full scale, the same on both sides, interleaved.
    public static float[] Cycle(int frequency)
    {
        var samples = new float[SampleRate * 2];
        for (var i = 0; i < SampleRate; i++)
        {
            var value = (float)Math.Sin(2 * Math.PI * frequency * i / SampleRate);
            samples[i * 2] = value;
            samples[i * 2 + 1] = value;
        }

        return samples;
    }

    // The gain that brings the full-scale sine to its peak at this level.
    public static double GainOf(double dbfs) => Math.Pow(10, dbfs / 20);

    public static string Label(int frequency) => frequency >= 1000 ? $"{frequency / 1000}k" : frequency.ToString();
}
