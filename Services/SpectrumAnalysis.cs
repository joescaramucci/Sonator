namespace Sonator.Services;

/// <summary>
/// The spectrum of the last moments of sound, as the master's LED analyser shows it: a Hann-windowed FFT of
/// Size samples, read into Bands bands spaced evenly by pitch from 31.5 Hz to 16 kHz. Each band's level is its
/// loudest frequency in dBFS, so a full-scale sine reads 0 in its band.
/// </summary>
public sealed class SpectrumAnalysis
{
    public const int Size = 4096;
    public const int Bands = 20;
    public const double LowestHz = 31.5;
    public const double HighestHz = 16000;
    public const double FloorDb = -90;

    private readonly double[] _window = new double[Size];
    private readonly double[] _real = new double[Size];
    private readonly double[] _imaginary = new double[Size];

    public SpectrumAnalysis()
    {
        for (var i = 0; i < Size; i++)
        {
            _window[i] = 0.5 - 0.5 * Math.Cos(2 * Math.PI * i / (Size - 1));
        }
    }

    // The middle of each band, in Hz.
    public static double CentreOf(int band) => LowestHz * Math.Pow(HighestHz / LowestHz, band / (double)(Bands - 1));

    // Fills levels (Bands long) with each band's level in dBFS, from the last Size samples.
    public void Analyse(float[] samples, double sampleRate, double[] levels)
    {
        for (var i = 0; i < Size; i++)
        {
            _real[i] = samples[i] * _window[i];
            _imaginary[i] = 0;
        }

        Transform(_real, _imaginary);

        // A Hann window halves a sine's height, and the transform spreads it over both halves: 4/N brings a
        // full-scale sine back to 1.
        var binHz = sampleRate / Size;
        var halfBand = Math.Pow(HighestHz / LowestHz, 0.5 / (Bands - 1));
        for (var band = 0; band < Bands; band++)
        {
            var centre = CentreOf(band);
            var from = Math.Max(1, (int)Math.Floor(centre / halfBand / binHz));
            var to = Math.Min(Size / 2 - 1, Math.Max(from, (int)Math.Ceiling(centre * halfBand / binHz)));
            var loudest = 0.0;
            for (var bin = from; bin <= to; bin++)
            {
                loudest = Math.Max(loudest, Math.Sqrt(_real[bin] * _real[bin] + _imaginary[bin] * _imaginary[bin]) * 4 / Size);
            }

            levels[band] = loudest <= 0 ? FloorDb : Math.Max(FloorDb, 20 * Math.Log10(loudest));
        }
    }

    // An in-place radix-2 FFT.
    private static void Transform(double[] real, double[] imaginary)
    {
        var n = real.Length;
        for (int i = 1, j = 0; i < n; i++)
        {
            var bit = n >> 1;
            for (; (j & bit) != 0; bit >>= 1)
            {
                j ^= bit;
            }

            j ^= bit;
            if (i < j)
            {
                (real[i], real[j]) = (real[j], real[i]);
                (imaginary[i], imaginary[j]) = (imaginary[j], imaginary[i]);
            }
        }

        for (var length = 2; length <= n; length <<= 1)
        {
            var angle = -2 * Math.PI / length;
            var stepReal = Math.Cos(angle);
            var stepImaginary = Math.Sin(angle);
            for (var start = 0; start < n; start += length)
            {
                var wReal = 1.0;
                var wImaginary = 0.0;
                for (var k = 0; k < length / 2; k++)
                {
                    var a = start + k;
                    var b = a + length / 2;
                    var tReal = real[b] * wReal - imaginary[b] * wImaginary;
                    var tImaginary = real[b] * wImaginary + imaginary[b] * wReal;
                    real[b] = real[a] - tReal;
                    imaginary[b] = imaginary[a] - tImaginary;
                    real[a] += tReal;
                    imaginary[a] += tImaginary;
                    var nextReal = wReal * stepReal - wImaginary * stepImaginary;
                    wImaginary = wReal * stepImaginary + wImaginary * stepReal;
                    wReal = nextReal;
                }
            }
        }
    }
}
