namespace Sonator.Services.Score;

// A note heard in a sung (or played) take, in seconds from its start, with the MIDI note it is nearest to.
public sealed record SungNote(double Start, double End, int Note);

// Hears the melody of a single voice: its pitch every hundredth of a second (the YIN method, on the take brought
// down to about 11 kHz, plenty for a voice), whether that hundredth is sung at all (loud enough and clearly pitched),
// then notes: a new one where the pitch, its vibrato smoothed away, moves to another semitone and stays there, or
// where the voice starts again after a breath or a consonant. The singer's own tuning, a little sharp or flat throughout, is allowed for.
public static class VoicePitch
{
    public const double FrameSeconds = 0.01;
    private const double WindowSeconds = 0.04;
    private const double LowestHz = 70;
    private const double HighestHz = 1050;
    private const double YinThreshold = 0.15;
    private const double VoicedAperiodicity = 0.25;
    private const double ShortestNote = 0.07;

    // The pitch of each hundredth: in MIDI notes with fractions, NaN where nothing is sung; and its loudness.
    public static (double[] Pitch, double[] Loudness) Track(float[] mono, int sampleRate)
    {
        var factor = Math.Max(1, (int)Math.Round(sampleRate / 11025.0));
        var rate = sampleRate / (double)factor;
        var x = Downsample(mono, factor);

        var hop = (int)Math.Round(rate * FrameSeconds);
        var window = (int)Math.Round(rate * WindowSeconds);
        var minTau = (int)Math.Floor(rate / HighestHz);
        var maxTau = Math.Min(window - 1, (int)Math.Ceiling(rate / LowestHz));
        var frames = Math.Max(0, (x.Length - window - maxTau) / hop);

        var pitch = new double[frames];
        var loudness = new double[frames];
        var aperiodic = new double[frames];
        var d = new double[maxTau + 1];
        for (var f = 0; f < frames; f++)
        {
            var start = f * hop;
            double energy = 0;
            for (var j = 0; j < window; j++)
            {
                energy += x[start + j] * (double)x[start + j];
            }

            loudness[f] = Math.Sqrt(energy / window);

            // The difference function, normalised by its running mean (YIN steps 2 and 3).
            d[0] = 1;
            double running = 0;
            for (var tau = 1; tau <= maxTau; tau++)
            {
                double sum = 0;
                for (var j = 0; j < window; j++)
                {
                    var delta = x[start + j] - x[start + j + tau];
                    sum += delta * delta;
                }

                running += sum;
                d[tau] = running > 0 ? sum * tau / running : 1;
            }

            // The first dip under the threshold, at its bottom; the deepest dip when none goes under.
            var best = -1;
            for (var tau = minTau; tau <= maxTau; tau++)
            {
                if (d[tau] < YinThreshold)
                {
                    while (tau + 1 <= maxTau && d[tau + 1] < d[tau])
                    {
                        tau++;
                    }

                    best = tau;
                    break;
                }
            }

            if (best < 0)
            {
                best = minTau;
                for (var tau = minTau; tau <= maxTau; tau++)
                {
                    if (d[tau] < d[best])
                    {
                        best = tau;
                    }
                }
            }

            aperiodic[f] = d[best];
            var refined = (double)best;
            if (best > minTau && best < maxTau)
            {
                var a = d[best - 1];
                var b = d[best];
                var c = d[best + 1];
                var denominator = a - 2 * b + c;
                if (Math.Abs(denominator) > 1e-12)
                {
                    refined = best + 0.5 * (a - c) / denominator;
                }
            }

            var hz = rate / refined;
            pitch[f] = 69 + 12 * Math.Log2(hz / 440.0);
        }

        // Sung: clearly pitched, and loud enough against the loud parts of the take.
        var sorted = loudness.OrderBy(value => value).ToArray();
        var loud = sorted.Length == 0 ? 0 : sorted[(int)(sorted.Length * 0.95)];
        var floor = Math.Max(0.003, loud * 0.08);
        for (var f = 0; f < frames; f++)
        {
            if (aperiodic[f] > VoicedAperiodicity || loudness[f] < floor)
            {
                pitch[f] = double.NaN;
            }
        }

        return (MedianFilter(pitch, 2), loudness);
    }

    // The notes sung in a take.
    public static IReadOnlyList<SungNote> Notes(float[] mono, int sampleRate)
    {
        var (pitch, loudness) = Track(mono, sampleRate);
        var tuning = Tuning(pitch);
        var steady = Steady(pitch);
        var notes = new List<SungNote>();

        var f = 0;
        while (f < pitch.Length)
        {
            if (double.IsNaN(pitch[f]))
            {
                f++;
                continue;
            }

            // A run of sung hundredths, cut where the pitch settles on another semitone or the voice starts again.
            var start = f;
            // The note it is: where the voice sits over the next 80 ms.
            var current = Math.Round(Median(pitch, f, f + 8) - tuning);
            var away = 0;
            var quietest = loudness[f];
            var loudest = loudness[f];
            for (f++; f <= pitch.Length; f++)
            {
                var ends = f == pitch.Length || double.IsNaN(pitch[f]);
                var onset = !ends && f - start >= 6 && loudness[f] > 2.2 * quietest && quietest < 0.5 * loudest;
                if (!ends && Math.Abs(steady[f] - tuning - current) > 0.6)
                {
                    away++;
                }
                else
                {
                    away = 0;
                }

                var moved = away >= 4;
                if (ends || moved || onset)
                {
                    var end = moved ? f - away + 1 : f;
                    Add(notes, pitch, tuning, start, end);
                    if (ends)
                    {
                        break;
                    }

                    start = end;
                    current = Math.Round(Median(pitch, f, f + 8) - tuning);
                    away = 0;
                    quietest = loudest = loudness[f];
                    continue;
                }

                // How quiet and how loud the voice has been since the note began, for telling a new start.
                quietest = Math.Min(quietest, loudness[f]);
                loudest = Math.Max(loudest, loudness[f]);
            }
        }

        return notes;
    }

    // The pitch with its vibrato smoothed away: the mean over 200 ms (a vibrato's cycle or so), within each
    // stretch sung; for telling where the voice really moves to another note.
    private static double[] Steady(double[] pitch)
    {
        const int Radius = 10;
        var steady = new double[pitch.Length];
        for (var i = 0; i < pitch.Length; i++)
        {
            if (double.IsNaN(pitch[i]))
            {
                steady[i] = double.NaN;
                continue;
            }

            double sum = 0;
            var count = 0;
            for (var j = i; j >= Math.Max(0, i - Radius) && !double.IsNaN(pitch[j]); j--)
            {
                sum += pitch[j];
                count++;
            }

            for (var j = i + 1; j <= Math.Min(pitch.Length - 1, i + Radius) && !double.IsNaN(pitch[j]); j++)
            {
                sum += pitch[j];
                count++;
            }

            steady[i] = sum / count;
        }

        return steady;
    }

    private static void Add(List<SungNote> notes, double[] pitch, double tuning, int start, int end)
    {
        if ((end - start) * FrameSeconds < ShortestNote)
        {
            return;
        }

        var note = (int)Math.Round(Median(pitch, start, end) - tuning);
        notes.Add(new SungNote(start * FrameSeconds, end * FrameSeconds, note));
    }

    // How far the voice sits from the equal-tempered notes overall, in semitones (-0.5 to 0.5): the middle of the
    // fractions of every hundredth sung, taken round the circle.
    internal static double Tuning(double[] pitch)
    {
        double sin = 0, cos = 0;
        foreach (var value in pitch.Where(value => !double.IsNaN(value)))
        {
            var angle = 2 * Math.PI * (value - Math.Round(value));
            sin += Math.Sin(angle);
            cos += Math.Cos(angle);
        }

        return sin == 0 && cos == 0 ? 0 : Math.Atan2(sin, cos) / (2 * Math.PI);
    }

    private static double Median(double[] values, int start, int end)
    {
        var part = new List<double>();
        for (var i = Math.Max(0, start); i < Math.Min(values.Length, end); i++)
        {
            if (!double.IsNaN(values[i]))
            {
                part.Add(values[i]);
            }
        }

        if (part.Count == 0)
        {
            return double.NaN;
        }

        part.Sort();
        return part[part.Count / 2];
    }

    // Each hundredth's pitch as the middle of its neighbours', so a stray octave jump goes; silence stays silence.
    private static double[] MedianFilter(double[] values, int radius)
    {
        var result = new double[values.Length];
        for (var i = 0; i < values.Length; i++)
        {
            result[i] = double.IsNaN(values[i]) ? double.NaN : Median(values, i - radius, i + radius + 1);
        }

        return result;
    }

    // The take at a lower rate, each sample the mean of the ones it stands for (enough of a filter for a voice).
    private static float[] Downsample(float[] mono, int factor)
    {
        if (factor <= 1)
        {
            return mono;
        }

        var result = new float[mono.Length / factor];
        for (var i = 0; i < result.Length; i++)
        {
            double sum = 0;
            for (var j = 0; j < factor; j++)
            {
                sum += mono[i * factor + j];
            }

            result[i] = (float)(sum / factor);
        }

        return result;
    }

    // A clip's channels as one.
    public static float[] Mono(AudioClip clip)
    {
        var mono = new float[clip.FrameCount];
        for (var frame = 0; frame < mono.Length; frame++)
        {
            double sum = 0;
            for (var channel = 0; channel < clip.Channels; channel++)
            {
                sum += clip.Samples[frame * clip.Channels + channel];
            }

            mono[frame] = (float)(sum / clip.Channels);
        }

        return mono;
    }
}
