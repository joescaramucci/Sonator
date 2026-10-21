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

    private AudioWaveformSummary(double durationSeconds, float[] peaks)
    {
        DurationSeconds = durationSeconds;
        Peaks = peaks;
    }

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

        return new AudioWaveformSummary(clip.Duration.TotalSeconds, peaks);
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
