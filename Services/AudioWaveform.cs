using System.Globalization;
using System.Text;

namespace Sonator.Services;

// The length and a coarse outline of a clip, enough to draw it on a track without decoding it again.
public sealed class AudioWaveformSummary
{
    public const int PeakCount = 256;

    private const double PathWidth = 1000;
    private const double PathHeight = 100;
    private const double PathHalfHeight = PathHeight / 2;

    // The fine outline: the lowest and highest sample of each slice this long, for drawing the wave itself.
    public const int SlicesPerSecond = 200;

    private AudioWaveformSummary(double durationSeconds, float[] peaks, float[] minimums, float[] maximums)
    {
        DurationSeconds = durationSeconds;
        Peaks = peaks;
        Minimums = minimums;
        Maximums = maximums;
    }

    // The lowest and highest sample (of any side) of each slice of SlicesPerSecond, from -1 to 1.
    public float[] Minimums { get; }

    public float[] Maximums { get; }

    public double DurationSeconds { get; }

    // The loudest sample of each slice of the clip, from 0 to 1.
    public float[] Peaks { get; }

    // The outline as a closed path in a 1000 x 100 box, mirrored around the middle line.
    public string PathData => BuildPathData(Peaks);

    public static AudioWaveformSummary FromClip(AudioClip clip)
    {
        var peaks = new float[PeakCount];
        var frames = clip.FrameCount;
        for (var bucket = 0; bucket < PeakCount && frames > 0; bucket++)
        {
            var from = (int)((long)bucket * frames / PeakCount);
            var to = Math.Max(from + 1, (int)((long)(bucket + 1) * frames / PeakCount));
            var peak = 0f;
            for (var frame = from; frame < to && frame < frames; frame++)
            {
                for (var channel = 0; channel < clip.Channels; channel++)
                {
                    peak = Math.Max(peak, Math.Abs(clip.Samples[frame * clip.Channels + channel]));
                }
            }
            peaks[bucket] = Math.Min(1f, peak);
        }

        var slices = Math.Max(1, (int)Math.Ceiling(clip.Duration.TotalSeconds * SlicesPerSecond));
        var minimums = new float[slices];
        var maximums = new float[slices];
        var framesPerSlice = Math.Max(1.0, clip.SampleRate / (double)SlicesPerSecond);
        for (var slice = 0; slice < slices; slice++)
        {
            var from = (int)(slice * framesPerSlice);
            var to = Math.Min(frames, (int)((slice + 1) * framesPerSlice));
            var low = 0f;
            var high = 0f;
            for (var index = from * clip.Channels; index < to * clip.Channels; index++)
            {
                var sample = clip.Samples[index];
                low = Math.Min(low, sample);
                high = Math.Max(high, sample);
            }

            minimums[slice] = Math.Max(-1f, low);
            maximums[slice] = Math.Min(1f, high);
        }

        return new AudioWaveformSummary(clip.Duration.TotalSeconds, peaks, minimums, maximums);
    }

    private static string BuildPathData(float[] peaks)
    {
        var builder = new StringBuilder();
        for (var index = 0; index < peaks.Length; index++)
        {
            var x = index * PathWidth / (peaks.Length - 1);
            var offset = peaks[index] * PathHalfHeight;
            builder.Append(index == 0 ? "M " : " L ");
            builder.Append(CultureInfo.InvariantCulture, $"{x:0.#},{PathHalfHeight - offset:0.#}");
        }

        for (var index = peaks.Length - 1; index >= 0; index--)
        {
            var x = index * PathWidth / (peaks.Length - 1);
            var offset = peaks[index] * PathHalfHeight;
            builder.Append(" L ");
            builder.Append(CultureInfo.InvariantCulture, $"{x:0.#},{PathHalfHeight + offset:0.#}");
        }

        builder.Append(" Z");
        return builder.ToString();
    }
}
