namespace Sonator.Services.Effects;

// SonNormalize: turns a whole clip up or down by one amount, so its loudest sample reaches a target
// level. It works on the clip once, not live, and leaves the shape of the sound untouched.
public static class SonNormalize
{
    // Below this a clip is treated as silent and left alone (about -120 dBFS).
    private const double SilentPeak = 1e-6;

    // The loudest sample, in dB below full scale. Negative infinity for a silent clip.
    public static double PeakDb(AudioClip clip)
    {
        var peak = Peak(clip);
        return peak < SilentPeak ? double.NegativeInfinity : 20 * Math.Log10(peak);
    }

    // A new clip whose loudest sample is at targetDb. A silent clip comes back unchanged.
    public static AudioClip Apply(AudioClip clip, double targetDb)
    {
        var peak = Peak(clip);
        var samples = (float[])clip.Samples.Clone();
        if (peak >= SilentPeak)
        {
            var gain = (float)(Math.Pow(10, targetDb / 20) / peak);
            for (var i = 0; i < samples.Length; i++)
            {
                samples[i] *= gain;
            }
        }

        return new AudioClip(samples, clip.Channels, clip.SampleRate);
    }

    private static double Peak(AudioClip clip)
    {
        var peak = 0.0;
        foreach (var sample in clip.Samples)
        {
            peak = Math.Max(peak, Math.Abs(sample));
        }

        return peak;
    }
}
