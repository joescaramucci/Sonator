using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace Sonator.Services;

// A decoded audio clip. Samples are interleaved 32-bit floats at the clip's sample rate.
public sealed class AudioClip
{
    public AudioClip(float[] samples, int channels, int sampleRate)
    {
        Samples = samples;
        Channels = channels;
        SampleRate = sampleRate;
    }

    public float[] Samples { get; set; }

    public int Channels { get; }

    public int SampleRate { get; }

    public int FrameCount => Samples.Length / Channels;

    public TimeSpan Duration => TimeSpan.FromSeconds(FrameCount / (double)SampleRate);
}

public static class AudioClipLoader
{
    // Decodes any format Windows Media Foundation reads (mp3, wav, m4a, ...) to float samples.
    public static AudioClip Load(string path)
    {
        using var reader = new MediaFoundationReader(path);
        var provider = reader.ToSampleProvider();
        var format = provider.WaveFormat;

        var samples = new List<float[]>();
        var buffer = new float[format.SampleRate * format.Channels];
        int read;
        var total = 0;
        while ((read = provider.Read(buffer, 0, buffer.Length)) > 0)
        {
            var chunk = new float[read];
            Array.Copy(buffer, chunk, read);
            samples.Add(chunk);
            total += read;
        }

        var all = new float[total];
        var offset = 0;
        foreach (var chunk in samples)
        {
            Array.Copy(chunk, 0, all, offset, chunk.Length);
            offset += chunk.Length;
        }
        return new AudioClip(all, format.Channels, format.SampleRate);
    }

    public static void SaveWav(AudioClip clip, string path)
    {
        using var writer = new WaveFileWriter(path, WaveFormat.CreateIeeeFloatWaveFormat(clip.SampleRate, clip.Channels));
        writer.WriteSamples(clip.Samples, 0, clip.Samples.Length);
    }

    // Plays a clip (or a part of it) through the default output.
    public static ISampleProvider ToPlayable(AudioClip clip, int startFrame, int endFrame)
    {
        return new ClipSampleProvider(clip, startFrame, endFrame);
    }

    private sealed class ClipSampleProvider : ISampleProvider
    {
        private readonly AudioClip _clip;
        private readonly int _endSample;
        private int _position;

        public ClipSampleProvider(AudioClip clip, int startFrame, int endFrame)
        {
            _clip = clip;
            WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(clip.SampleRate, clip.Channels);
            _position = startFrame * clip.Channels;
            _endSample = endFrame * clip.Channels;
        }

        public WaveFormat WaveFormat { get; }

        public int Read(float[] buffer, int offset, int count)
        {
            var available = Math.Min(count, _endSample - _position);
            if (available <= 0)
            {
                return 0;
            }

            Array.Copy(_clip.Samples, _position, buffer, offset, available);
            _position += available;
            return available;
        }
    }
}

// Edits on interleaved sample arrays. Frame ranges are [start, end).
public static class AudioEdits
{
    public static float Peak(float[] samples)
    {
        var peak = 0f;
        foreach (var sample in samples)
        {
            peak = Math.Max(peak, Math.Abs(sample));
        }
        return peak;
    }

    // Scales the whole clip so its loudest sample reaches the target peak (0 to 1). Silence is left as it is.
    public static float[] Normalize(float[] samples, float targetPeak = 1f)
    {
        var peak = Peak(samples);
        if (peak <= 0f)
        {
            return (float[])samples.Clone();
        }

        var gain = targetPeak / peak;
        return samples.Select(sample => sample * gain).ToArray();
    }

    public static float[] FadeIn(float[] samples, int channels, int startFrame, int endFrame)
    {
        return ApplyFade(samples, channels, startFrame, endFrame, fadeIn: true);
    }

    public static float[] FadeOut(float[] samples, int channels, int startFrame, int endFrame)
    {
        return ApplyFade(samples, channels, startFrame, endFrame, fadeIn: false);
    }

    public static float[] Silence(float[] samples, int channels, int startFrame, int endFrame)
    {
        var result = (float[])samples.Clone();
        for (var index = startFrame * channels; index < endFrame * channels; index++)
        {
            result[index] = 0f;
        }
        return result;
    }

    // The samples of a range, to be pasted later.
    public static float[] Copy(float[] samples, int channels, int startFrame, int endFrame)
    {
        var length = (endFrame - startFrame) * channels;
        var result = new float[length];
        Array.Copy(samples, startFrame * channels, result, 0, length);
        return result;
    }

    public static float[] Cut(float[] samples, int channels, int startFrame, int endFrame)
    {
        var start = startFrame * channels;
        var end = endFrame * channels;
        return samples.Take(start).Concat(samples.Skip(end)).ToArray();
    }

    public static float[] Paste(float[] samples, int channels, int atFrame, float[] clip)
    {
        var at = atFrame * channels;
        return samples.Take(at).Concat(clip).Concat(samples.Skip(at)).ToArray();
    }

    private static float[] ApplyFade(float[] samples, int channels, int startFrame, int endFrame, bool fadeIn)
    {
        var result = (float[])samples.Clone();
        var frames = endFrame - startFrame;
        if (frames <= 0)
        {
            return result;
        }

        for (var frame = 0; frame < frames; frame++)
        {
            var progress = (frame + 1.0) / frames;
            var gain = (float)(fadeIn ? progress : 1.0 - progress);
            for (var channel = 0; channel < channels; channel++)
            {
                result[(startFrame + frame) * channels + channel] *= gain;
            }
        }
        return result;
    }
}
