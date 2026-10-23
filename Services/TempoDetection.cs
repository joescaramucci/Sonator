namespace Sonator.Services;

// What tempo detection found in a clip: its tempo, the time of its first beat, and how sure it is (0 to 1).
public sealed record DetectedTempo(double Bpm, double FirstBeatSeconds, double Confidence);

/// <summary>
/// Finds the tempo of a piece of music, as a DAW's BPM detection does, so the song's tempo, metronome and grid
/// can follow an imported clip. The sound's onsets are taken as the rise of its spectrum from one moment to the
/// next (spectral flux); the tempo is the beat period at which they repeat best (their autocorrelation, from 60
/// to 200 BPM, leaning toward the usual 80 to 160), refined between frames; the first beat is where a comb of
/// beats at that period lines up best with them.
/// </summary>
public static class TempoDetection
{
    private const int FrameSize = 1024;
    private const int Hop = 256;
    private const double MinBpm = 60;
    private const double MaxBpm = 200;
    private const double MaxSeconds = 90; // the first minute and a half tells the tempo

    public static DetectedTempo? Detect(AudioClip clip)
    {
        var sampleRate = clip.SampleRate;
        var frames = Math.Min(clip.FrameCount, (int)(MaxSeconds * sampleRate));
        if (frames < sampleRate * 4)
        {
            return null; // too short to tell
        }

        var (onsets, lowOnsets) = OnsetStrength(clip, frames);
        var frameRate = sampleRate / (double)Hop;

        // The autocorrelation of the onsets over the lags of the tempos looked at.
        var minLag = (int)Math.Floor(frameRate * 60 / MaxBpm);
        var maxLag = (int)Math.Ceiling(frameRate * 60 / MinBpm);
        if (maxLag + 2 >= onsets.Length)
        {
            return null;
        }

        var correlation = new double[maxLag + 2];
        for (var lag = minLag - 1; lag <= maxLag + 1; lag++)
        {
            var sum = 0.0;
            for (var i = lag; i < onsets.Length; i++)
            {
                sum += onsets[i] * onsets[i - lag];
            }

            correlation[lag] = sum / (onsets.Length - lag);
        }

        // Each lag scored with its multiples, so the beat beats its own halves and doubles, and weighted toward
        // the tempos music is mostly played at.
        var bestLag = -1;
        var bestScore = double.MinValue;
        var scores = new double[maxLag + 1];
        for (var lag = minLag; lag <= maxLag; lag++)
        {
            var score = correlation[lag];
            if (2 * lag + 1 < correlation.Length)
            {
                score += 0.5 * correlation[2 * lag];
            }
            else if (2 * lag < onsets.Length)
            {
                score += 0.5 * LagCorrelation(onsets, 2 * lag);
            }

            var bpm = 60 * frameRate / lag;
            var octavesFrom120 = Math.Log2(bpm / 120);
            score *= Math.Exp(-0.5 * octavesFrom120 * octavesFrom120 / (0.9 * 0.9));
            scores[lag] = score;
            if (score > bestScore)
            {
                bestScore = score;
                bestLag = lag;
            }
        }

        if (bestLag < 0 || bestScore <= 0)
        {
            return null;
        }

        // Between frames: the peak of a parabola through the best lag and its neighbours.
        var lagExact = (double)bestLag;
        if (bestLag > minLag && bestLag < maxLag)
        {
            var (a, b, c) = (scores[bestLag - 1], scores[bestLag], scores[bestLag + 1]);
            var denominator = a - 2 * b + c;
            if (denominator < 0)
            {
                lagExact += 0.5 * (a - c) / denominator;
            }
        }

        var bpmFound = 60 * frameRate / lagExact;
        bpmFound = RefineBpm(onsets, frameRate, bpmFound);
        var periodFrames = 60 * frameRate / bpmFound;
        var firstBeat = (FirstBeat(onsets, lowOnsets, periodFrames) + FrameLatency) / frameRate;

        var mean = scores.Skip(minLag).Take(maxLag - minLag + 1).Average();
        var confidence = Math.Clamp((bestScore - mean) / (bestScore + 1e-12), 0, 1);
        return new DetectedTempo(Math.Round(bpmFound, 2), firstBeat, confidence);
    }

    // Tries tempos close to the one found, in hundredths of a BPM, for the one whose beats line up with the
    // onsets all through the clip: a small error in the tempo adds up over a minute.
    private static double RefineBpm(double[] onsets, double frameRate, double bpm)
    {
        var best = bpm;
        var bestScore = double.MinValue;
        for (var candidate = bpm - 1.5; candidate <= bpm + 1.5; candidate += 0.01)
        {
            var period = 60 * frameRate / candidate;
            var score = CombScore(onsets, period, BestPhase(onsets, period));
            if (score > bestScore)
            {
                bestScore = score;
                best = candidate;
            }
        }

        return best;
    }

    // A frame's onset belongs to the middle of its window.
    private const double FrameLatency = FrameSize / 2.0 / Hop;

    // The frame of the first beat: the offset within one period where a comb of beats sums the most onset;
    // between it and the half beat after, the one with more low end, where the kick drum is, as the off-beats
    // often hold more of the hi-hats' high end.
    private static double FirstBeat(double[] onsets, double[] lowOnsets, double period)
    {
        var phase = BestPhase(onsets, period);
        var offBeat = (phase + period / 2) % period;
        return CombScore(lowOnsets, period, offBeat) > CombScore(lowOnsets, period, phase) ? offBeat : phase;
    }

    private static double BestPhase(double[] onsets, double period)
    {
        var bestPhase = 0.0;
        var bestScore = double.MinValue;
        for (var phase = 0.0; phase < period; phase += 0.5)
        {
            var score = CombScore(onsets, period, phase);
            if (score > bestScore)
            {
                bestScore = score;
                bestPhase = phase;
            }
        }

        return bestPhase;
    }

    private static double CombScore(double[] onsets, double period, double phase)
    {
        var sum = 0.0;
        for (var t = phase; t < onsets.Length - 1; t += period)
        {
            var i = (int)t;
            var fraction = t - i;
            sum += onsets[i] * (1 - fraction) + onsets[i + 1] * fraction;
        }

        return sum;
    }

    private static double LagCorrelation(double[] onsets, int lag)
    {
        var sum = 0.0;
        for (var i = lag; i < onsets.Length; i++)
        {
            sum += onsets[i] * onsets[i - lag];
        }

        return sum / (onsets.Length - lag);
    }

    // The onset strength of each hop: the sum of the rises of the log spectrum's bins, minus its local mean,
    // so steady sound does not count; and the same for the low end alone, below about 200 Hz.
    internal static (double[] All, double[] Low) OnsetStrength(AudioClip clip, int frames)
    {
        var lowBins = Math.Max(2, (int)(200.0 * FrameSize / clip.SampleRate));
        var count = Math.Max(0, (frames - FrameSize) / Hop);
        var window = new double[FrameSize];
        for (var i = 0; i < FrameSize; i++)
        {
            window[i] = 0.5 - 0.5 * Math.Cos(2 * Math.PI * i / (FrameSize - 1));
        }

        var real = new double[FrameSize];
        var imaginary = new double[FrameSize];
        var previous = new double[FrameSize / 2];
        var flux = new double[count];
        var lowFlux = new double[count];
        var channels = clip.Channels;
        for (var frame = 0; frame < count; frame++)
        {
            var start = frame * Hop;
            for (var i = 0; i < FrameSize; i++)
            {
                var sample = 0.0;
                var index = (start + i) * channels;
                for (var c = 0; c < channels; c++)
                {
                    sample += clip.Samples[index + c];
                }

                real[i] = sample / channels * window[i];
                imaginary[i] = 0;
            }

            Fft.Transform(real, imaginary);
            var rise = 0.0;
            var lowRise = 0.0;
            for (var bin = 1; bin < FrameSize / 2; bin++)
            {
                var magnitude = Math.Log(1 + 1000 * Math.Sqrt(real[bin] * real[bin] + imaginary[bin] * imaginary[bin]));
                var up = Math.Max(0, magnitude - previous[bin]);
                rise += up;
                if (bin <= lowBins)
                {
                    lowRise += up;
                }

                previous[bin] = magnitude;
            }

            flux[frame] = rise;
            lowFlux[frame] = lowRise;
        }

        return (LessLocalMean(flux), LessLocalMean(lowFlux));
    }

    // Each value less the mean around it, over about a third of a second, and never below zero.
    private static double[] LessLocalMean(double[] flux)
    {
        var count = flux.Length;
        var onsets = new double[count];
        const int Half = 16;
        var running = 0.0;
        for (var i = 0; i < Math.Min(count, Half); i++)
        {
            running += flux[i];
        }

        for (var i = 0; i < count; i++)
        {
            if (i + Half < count)
            {
                running += flux[i + Half];
            }

            if (i - Half - 1 >= 0)
            {
                running -= flux[i - Half - 1];
            }

            var width = Math.Min(count - 1, i + Half) - Math.Max(0, i - Half) + 1;
            onsets[i] = Math.Max(0, flux[i] - running / width);
        }

        return onsets;
    }
}

// An in-place radix-2 FFT.
internal static class Fft
{
    public static void Transform(double[] real, double[] imaginary)
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
