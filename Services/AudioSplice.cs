namespace Sonator.Services;

// Punching a take into a clip: the clip keeps its sound outside the punch and takes the new take's inside it,
// with a short crossfade at each seam so neither clicks. Times are in seconds of the song.
public static class AudioSplice
{
    public const double SeamSeconds = 0.01;

    // The clip (null for none) placed at clipStart, with the part from punchIn to punchOut replaced by the take
    // placed at takeStart. The result is stereo at the clip's rate (the take's when there is no clip), starting at
    // start, which must be no later than the clip's start nor the punch's.
    public static AudioClip Punch(AudioClip? clip, double clipStart, AudioClip take, double takeStart, double punchIn, double punchOut, double start)
    {
        var rate = clip?.SampleRate ?? take.SampleRate;
        var clipEnd = clip is null ? start : clipStart + clip.Duration.TotalSeconds;
        var end = Math.Max(clipEnd, punchOut);
        var frames = Math.Max(0, (int)Math.Ceiling((end - start) * rate));
        var samples = new float[frames * 2];
        for (var i = 0; i < frames; i++)
        {
            var time = start + (double)i / rate;
            var fromTake = Weight(time, punchIn, punchOut);
            var left = 0f;
            var right = 0f;
            if (fromTake < 1 && clip is not null)
            {
                var (l, r) = SampleAt(clip, time - clipStart);
                left += l * (1 - fromTake);
                right += r * (1 - fromTake);
            }

            if (fromTake > 0)
            {
                var (l, r) = SampleAt(take, time - takeStart);
                left += l * fromTake;
                right += r * fromTake;
            }

            samples[i * 2] = left;
            samples[i * 2 + 1] = right;
        }

        return new AudioClip(samples, 2, rate);
    }

    // How much of the take is heard at a time: none outside the punch, all inside, crossfading over a seam at each
    // end.
    private static float Weight(double time, double punchIn, double punchOut)
    {
        var half = SeamSeconds / 2;
        var rise = Math.Clamp((time - (punchIn - half)) / SeamSeconds, 0, 1);
        var fall = Math.Clamp(((punchOut + half) - time) / SeamSeconds, 0, 1);
        return (float)Math.Min(rise, fall);
    }

    // The clip's sound at a time from its start, read between its samples; silence outside it. Mono plays on both.
    private static (float Left, float Right) SampleAt(AudioClip clip, double seconds)
    {
        var position = seconds * clip.SampleRate;
        var frames = clip.Channels > 0 ? clip.Samples.Length / clip.Channels : 0;
        if (position < 0 || position >= frames - 1)
        {
            return (0, 0);
        }

        var frame = (int)position;
        var fraction = (float)(position - frame);
        float Read(int at, int channel) => clip.Samples[at * clip.Channels + Math.Min(channel, clip.Channels - 1)];
        var left = Read(frame, 0) + (Read(frame + 1, 0) - Read(frame, 0)) * fraction;
        var right = Read(frame, 1) + (Read(frame + 1, 1) - Read(frame, 1)) * fraction;
        return (left, right);
    }

    // Clips laid one after another (or over each other) in one stereo clip at rate, each at its start (seconds from
    // the first's), faded in and out as given with an equal-power curve, as the bridge fades a clip.
    public static AudioClip Place(IReadOnlyList<(AudioClip Clip, double Start, double FadeIn, double FadeOut)> clips, int rate)
    {
        var end = clips.Count == 0 ? 0 : clips.Max(placed => placed.Start + placed.Clip.Duration.TotalSeconds);
        var frames = Math.Max(0, (int)Math.Ceiling(end * rate));
        var samples = new float[frames * 2];
        foreach (var (clip, start, fadeIn, fadeOut) in clips)
        {
            var seconds = clip.Duration.TotalSeconds;
            var first = Math.Max(0, (int)Math.Floor(start * rate));
            var last = Math.Min(frames, (int)Math.Ceiling((start + seconds) * rate));
            for (var i = first; i < last; i++)
            {
                var into = (double)i / rate - start;
                var level = 1.0;
                if (fadeIn > 0 && into < fadeIn)
                {
                    level *= Math.Sin(Math.PI / 2 * Math.Max(0, into) / fadeIn);
                }

                if (fadeOut > 0 && seconds - into < fadeOut)
                {
                    level *= Math.Sin(Math.PI / 2 * Math.Max(0, seconds - into) / fadeOut);
                }

                var (left, right) = SampleAt(clip, into);
                samples[i * 2] += (float)(left * level);
                samples[i * 2 + 1] += (float)(right * level);
            }
        }

        return new AudioClip(samples, 2, rate);
    }

    // The part of a clip from one time to another (seconds from its start).
    public static AudioClip Part(AudioClip clip, double from, double to)
    {
        var channels = Math.Max(1, clip.Channels);
        var frames = clip.Samples.Length / channels;
        var first = Math.Clamp((int)Math.Round(from * clip.SampleRate), 0, frames);
        var last = Math.Clamp((int)Math.Round(to * clip.SampleRate), first, frames);
        return new AudioClip(clip.Samples[(first * channels)..(last * channels)], channels, clip.SampleRate);
    }
}
