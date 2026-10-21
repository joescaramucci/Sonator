namespace Sonator.Services;

// Changes the duration of audio without changing its pitch, using WSOLA (waveform similarity overlap-add).
// Each output segment is placed at a fixed hop, and its input position is the best match near the nominal
// position, so the segments line up and the signal does not sound like a repeated loop.
public static class TimeStretcher
{
    private const int WindowFrames = 2048;
    private const int HopFrames = 512;
    private const int SearchFrames = 256;
    private const int CorrelationStep = 4;

    // ratio is output length divided by input length: 2 doubles the duration, 0.5 halves it.
    public static float[] Stretch(float[] samples, int channels, double ratio)
    {
        var inputFrames = samples.Length / channels;
        if (ratio <= 0 || double.IsNaN(ratio) || double.IsInfinity(ratio) || Math.Abs(ratio - 1.0) < 1e-6 || inputFrames < WindowFrames * 2)
        {
            return (float[])samples.Clone();
        }

        var outputFrames = (int)Math.Round(inputFrames * ratio);
        var output = new float[outputFrames * channels];
        var weight = new float[outputFrames];
        var mono = MixToMono(samples, channels);
        var window = HannWindow(WindowFrames);
        var inputPerHop = HopFrames / ratio;

        var previousInput = 0;
        for (var segment = 0; ; segment++)
        {
            var outputStart = segment * HopFrames;
            if (outputStart + WindowFrames > outputFrames)
            {
                break;
            }

            int inputStart;
            if (segment == 0)
            {
                inputStart = 0;
            }
            else
            {
                // The natural continuation of the previous segment is one hop further on in the input.
                var template = previousInput + HopFrames;
                var nominal = (int)Math.Round(segment * inputPerHop);
                inputStart = BestMatch(mono, template, nominal, inputFrames);
            }

            for (var frame = 0; frame < WindowFrames; frame++)
            {
                var source = inputStart + frame;
                if (source >= inputFrames)
                {
                    break;
                }

                var w = window[frame];
                var target = outputStart + frame;
                weight[target] += w;
                for (var channel = 0; channel < channels; channel++)
                {
                    output[target * channels + channel] += samples[source * channels + channel] * w;
                }
            }

            previousInput = inputStart;
        }

        for (var frame = 0; frame < outputFrames; frame++)
        {
            if (weight[frame] > 1e-6f)
            {
                for (var channel = 0; channel < channels; channel++)
                {
                    output[frame * channels + channel] /= weight[frame];
                }
            }
        }
        return output;
    }

    // Input position near the nominal one whose window best matches the template, by normalized correlation.
    private static int BestMatch(float[] mono, int template, int nominal, int inputFrames)
    {
        var best = Math.Clamp(nominal, 0, inputFrames - WindowFrames);
        var bestScore = double.MinValue;
        var from = Math.Max(0, nominal - SearchFrames);
        var to = Math.Min(inputFrames - WindowFrames, nominal + SearchFrames);
        if (template + WindowFrames > inputFrames)
        {
            return best;
        }

        for (var candidate = from; candidate <= to; candidate++)
        {
            double dot = 0, candidateEnergy = 0, templateEnergy = 0;
            for (var frame = 0; frame < WindowFrames; frame += CorrelationStep)
            {
                var a = mono[template + frame];
                var b = mono[candidate + frame];
                dot += a * b;
                candidateEnergy += b * b;
                templateEnergy += a * a;
            }

            var denominator = Math.Sqrt(candidateEnergy * templateEnergy);
            var score = denominator > 1e-12 ? dot / denominator : 0;
            if (score > bestScore)
            {
                bestScore = score;
                best = candidate;
            }
        }
        return best;
    }

    private static float[] MixToMono(float[] samples, int channels)
    {
        var frames = samples.Length / channels;
        var mono = new float[frames];
        for (var frame = 0; frame < frames; frame++)
        {
            var sum = 0f;
            for (var channel = 0; channel < channels; channel++)
            {
                sum += samples[frame * channels + channel];
            }
            mono[frame] = sum / channels;
        }
        return mono;
    }

    private static float[] HannWindow(int length)
    {
        var window = new float[length];
        for (var index = 0; index < length; index++)
        {
            window[index] = (float)(0.5 - 0.5 * Math.Cos(2 * Math.PI * index / (length - 1)));
        }
        return window;
    }
}
