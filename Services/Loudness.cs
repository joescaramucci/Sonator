namespace Sonator.Services;

// How loud a finished mix is, as streaming services measure it (ITU-R BS.1770-4 / EBU R128): the integrated loudness
// in LUFS (K-weighted, in 400 ms blocks overlapping by 75%, gated at -70 LUFS and 10 LU under the ungated level), and
// the true peak in dBTP (each side looked at four times finer, between its samples).
public static class Loudness
{
    public readonly record struct Measure(double IntegratedLufs, double TruePeakDb);

    public static Measure Of(float[] interleaved, int channels, int sampleRate)
    {
        var frames = interleaved.Length / Math.Max(1, channels);
        var shelf = Biquad.HighShelf(4.0, 1681.97, 0.7072, sampleRate);
        var highPass = Biquad.HighPass(38.135, 0.5003, sampleRate);

        // Each side K-weighted, its energy summed into 100 ms steps.
        var step = Math.Max(1, sampleRate / 10);
        var steps = new double[(frames + step - 1) / step];
        for (var channel = 0; channel < Math.Min(channels, 2); channel++)
        {
            var first = shelf.Fresh();
            var second = highPass.Fresh();
            for (var frame = 0; frame < frames; frame++)
            {
                var weighted = second.Run(first.Run(interleaved[frame * channels + channel]));
                steps[frame / step] += weighted * weighted;
            }
        }

        // 400 ms blocks every 100 ms, gated.
        var blocks = new List<double>();
        for (var start = 0; start + 4 <= steps.Length; start++)
        {
            var energy = (steps[start] + steps[start + 1] + steps[start + 2] + steps[start + 3]) / (4.0 * step);
            blocks.Add(energy);
        }

        static double Lufs(double energy) => energy > 1e-12 ? -0.691 + 10 * Math.Log10(energy) : double.NegativeInfinity;
        var loud = blocks.Where(energy => Lufs(energy) > -70).ToList();
        var integrated = double.NegativeInfinity;
        if (loud.Count > 0)
        {
            var relative = Lufs(loud.Average()) - 10;
            var gated = loud.Where(energy => Lufs(energy) > relative).ToList();
            integrated = gated.Count > 0 ? Lufs(gated.Average()) : Lufs(loud.Average());
        }

        return new Measure(integrated, TruePeak(interleaved, channels));
    }

    // The highest level between the samples too, read four times finer on a curve through the samples around.
    private static double TruePeak(float[] interleaved, int channels)
    {
        var frames = interleaved.Length / Math.Max(1, channels);
        double peak = 0;
        for (var channel = 0; channel < channels; channel++)
        {
            float At(int frame) => interleaved[Math.Clamp(frame, 0, frames - 1) * channels + channel];
            for (var frame = 0; frame < frames; frame++)
            {
                var y0 = At(frame - 1);
                var y1 = At(frame);
                var y2 = At(frame + 1);
                var y3 = At(frame + 2);
                peak = Math.Max(peak, Math.Abs(y1));
                for (var quarter = 1; quarter < 4; quarter++)
                {
                    var t = quarter / 4.0;
                    // Catmull-Rom between y1 and y2.
                    var value = 0.5 * (2 * y1 + (-y0 + y2) * t + (2 * y0 - 5 * y1 + 4 * y2 - y3) * t * t + (-y0 + 3 * y1 - 3 * y2 + y3) * t * t * t);
                    peak = Math.Max(peak, Math.Abs(value));
                }
            }
        }

        return peak > 0 ? 20 * Math.Log10(peak) : double.NegativeInfinity;
    }

    // A two-pole filter of the RBJ cookbook.
    private sealed class Biquad
    {
        private readonly double _b0, _b1, _b2, _a1, _a2;
        private double _x1, _x2, _y1, _y2;

        private Biquad(double b0, double b1, double b2, double a0, double a1, double a2)
        {
            _b0 = b0 / a0;
            _b1 = b1 / a0;
            _b2 = b2 / a0;
            _a1 = a1 / a0;
            _a2 = a2 / a0;
        }

        public Biquad Fresh() => new(_b0, _b1, _b2, 1, _a1, _a2);

        public double Run(double x)
        {
            var y = _b0 * x + _b1 * _x1 + _b2 * _x2 - _a1 * _y1 - _a2 * _y2;
            _x2 = _x1;
            _x1 = x;
            _y2 = _y1;
            _y1 = y;
            return y;
        }

        public static Biquad HighShelf(double gainDb, double frequency, double q, int sampleRate)
        {
            var a = Math.Pow(10, gainDb / 40);
            var w0 = 2 * Math.PI * frequency / sampleRate;
            var cosw = Math.Cos(w0);
            var alpha = Math.Sin(w0) / (2 * q);
            var rootA = 2 * Math.Sqrt(a) * alpha;
            return new Biquad(
                a * ((a + 1) + (a - 1) * cosw + rootA),
                -2 * a * ((a - 1) + (a + 1) * cosw),
                a * ((a + 1) + (a - 1) * cosw - rootA),
                (a + 1) - (a - 1) * cosw + rootA,
                2 * ((a - 1) - (a + 1) * cosw),
                (a + 1) - (a - 1) * cosw - rootA);
        }

        public static Biquad HighPass(double frequency, double q, int sampleRate)
        {
            var w0 = 2 * Math.PI * frequency / sampleRate;
            var cosw = Math.Cos(w0);
            var alpha = Math.Sin(w0) / (2 * q);
            return new Biquad((1 + cosw) / 2, -(1 + cosw), (1 + cosw) / 2, 1 + alpha, -2 * cosw, 1 - alpha);
        }
    }
}
