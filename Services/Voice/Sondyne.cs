using Sonator.Services.Score;

namespace Sonator.Services.Voice;

// One note of a take as Sondyne edits it: the hundredths it spans, the pitch it is sung at (the middle of its
// hundredths, in MIDI notes with fractions), and the changes asked for: Shift semitones up or down, Center how far
// its pitch is brought to the middle of its semitone (0 to 1), Drift how far its wobble is flattened (0 to 1).
public sealed class SondyneNote
{
    public SondyneNote(int startFrame, int endFrame, double sungPitch)
    {
        StartFrame = startFrame;
        EndFrame = endFrame;
        SungPitch = sungPitch;
    }

    public int StartFrame { get; }

    public int EndFrame { get; }

    public double SungPitch { get; }

    public double Shift { get; set; }

    public double Center { get; set; }

    public double Drift { get; set; }

    // The semitone the note is nearest, and where it ends up: its pitch moved toward that semitone, then shifted.
    public int NearestNote => (int)Math.Round(SungPitch);

    public double EditedPitch => SungPitch + Center * (NearestNote - SungPitch) + Shift;

    public bool IsEdited => Math.Abs(Shift) > 1e-6 || Center > 1e-6 || Drift > 1e-6;
}

// A take opened in Sondyne: its sound, its pitch and loudness every hundredth of a second, and its notes.
public sealed class SondyneTake
{
    public SondyneTake(AudioClip clip, double[] pitch, double[] loudness, IReadOnlyList<SondyneNote> notes)
    {
        Clip = clip;
        Pitch = pitch;
        Loudness = loudness;
        Notes = notes;
    }

    public AudioClip Clip { get; }

    // In MIDI notes with fractions, NaN where nothing is sung.
    public double[] Pitch { get; }

    public double[] Loudness { get; }

    public IReadOnlyList<SondyneNote> Notes { get; }

    public double FrameSeconds => VoicePitch.FrameSeconds;
}

// Sondyne's engine: hears a take's notes, and sings it again with them changed. Each hundredth gets a correction in
// semitones (none outside the notes changed); the take is then moved in pitch by that much, hundredth by hundredth,
// by two read heads kept a whole number of the voice's periods apart, as in SonArmo, and blended back into the
// untouched sound where nothing is changed.
public static class Sondyne
{
    public static SondyneTake Analyse(AudioClip clip)
    {
        var mono = VoicePitch.Mono(clip);
        var (pitch, loudness) = VoicePitch.Track(mono, clip.SampleRate);
        var notes = VoicePitch.Notes(mono, clip.SampleRate)
            .Select(note =>
            {
                var start = (int)Math.Round(note.Start / VoicePitch.FrameSeconds);
                var end = Math.Min(pitch.Length, (int)Math.Round(note.End / VoicePitch.FrameSeconds));
                var sung = pitch.Skip(start).Take(Math.Max(0, end - start)).Where(value => !double.IsNaN(value)).OrderBy(value => value).ToList();
                return new SondyneNote(start, end, sung.Count > 0 ? sung[sung.Count / 2] : note.Note);
            })
            .Where(note => note.EndFrame > note.StartFrame)
            .ToList();
        return new SondyneTake(clip, pitch, loudness, notes);
    }

    // What each hundredth sings at after the edits: the edited note's pitch plus what is left of its wobble; the
    // sung pitch where nothing is changed. NaN where nothing is sung.
    public static double[] EditedPitch(SondyneTake take)
    {
        var edited = (double[])take.Pitch.Clone();
        foreach (var note in take.Notes.Where(note => note.IsEdited))
        {
            for (var frame = note.StartFrame; frame < note.EndFrame && frame < edited.Length; frame++)
            {
                if (!double.IsNaN(take.Pitch[frame]))
                {
                    edited[frame] = take.Pitch[frame] + Correction(note, take.Pitch[frame]);
                }
            }
        }

        return edited;
    }

    // The semitones a hundredth of a note is moved: the note brought to its centre and shifted, its wobble reduced.
    private static double Correction(SondyneNote note, double sung)
    {
        var wobble = double.IsNaN(sung) ? 0 : sung - note.SungPitch;
        return note.EditedPitch - note.SungPitch - note.Drift * wobble;
    }

    // Each hundredth's correction in semitones, eased over 30 ms at the ends of the notes changed so they do not
    // click; and whether the hundredth is changed at all.
    internal static (double[] Shift, bool[] Changed) Corrections(SondyneTake take)
    {
        var frames = take.Pitch.Length;
        var shift = new double[frames];
        var changed = new bool[frames];
        foreach (var note in take.Notes.Where(note => note.IsEdited))
        {
            for (var frame = Math.Max(0, note.StartFrame - 3); frame < Math.Min(frames, note.EndFrame + 3); frame++)
            {
                var inside = Math.Clamp(frame, note.StartFrame, note.EndFrame - 1);
                shift[frame] = Correction(note, take.Pitch[inside]);
                changed[frame] = true;
            }
        }

        // A short moving average, so the correction glides instead of stepping.
        var smoothed = new double[frames];
        for (var frame = 0; frame < frames; frame++)
        {
            double sum = 0;
            var count = 0;
            for (var near = Math.Max(0, frame - 2); near <= Math.Min(frames - 1, frame + 2); near++)
            {
                sum += shift[near];
                count++;
            }

            smoothed[frame] = count == 0 ? 0 : sum / count;
        }

        return (smoothed, changed);
    }

    // The take sung again with its notes changed.
    public static AudioClip Render(SondyneTake take)
    {
        var clip = take.Clip;
        var (shift, changed) = Corrections(take);
        if (shift.Length == 0 || !changed.Any(value => value))
        {
            return new AudioClip((float[])clip.Samples.Clone(), clip.Channels, clip.SampleRate);
        }

        var rate = clip.SampleRate;
        var frames = clip.FrameCount;
        var channels = clip.Channels;
        var samplesPerFrame = rate * VoicePitch.FrameSeconds;
        var grain = Math.Max(256, (int)(rate * 0.040));
        const double MinDelay = 8;
        var latency = MinDelay + grain * 0.5; // the heads' delay, read ahead of so the voice stays in time

        double[] delay = [MinDelay + grain * 0.25, MinDelay + grain * 0.75];
        double[] span = [grain, grain];
        var period = 0.0;
        var wetness = 0.0;
        var glide = Math.Exp(-1.0 / (0.010 * rate));
        var output = new float[clip.Samples.Length];

        double Read(int channel, double position)
        {
            position = Math.Clamp(position, 0, frames - 1);
            var index = (int)position;
            var fraction = position - index;
            var a = clip.Samples[index * channels + channel];
            var b = clip.Samples[Math.Min(index + 1, frames - 1) * channels + channel];
            return a + (b - a) * fraction;
        }

        double IdealSpan()
        {
            if (period <= 0)
            {
                return grain;
            }

            var periods = Math.Max(1, Math.Ceiling(grain / (2 * period)));
            return Math.Min(2 * periods * period, 2.5 * grain);
        }

        Span<double> weights = stackalloc double[2];
        for (var n = 0; n < frames; n++)
        {
            // The correction and the voice's period here, read between the hundredths.
            var at = n / samplesPerFrame;
            var frame = Math.Min(shift.Length - 1, (int)at);
            var next = Math.Min(shift.Length - 1, frame + 1);
            var fraction = at - frame;
            var semitones = shift[frame] + (shift[next] - shift[frame]) * fraction;
            if (!double.IsNaN(take.Pitch[frame]))
            {
                period = rate / (440 * Math.Pow(2, (take.Pitch[frame] - 69) / 12));
            }

            var wanted = changed[frame] ? 1.0 : 0.0;
            wetness = wanted + (wetness - wanted) * glide;

            var speed = Math.Pow(2, semitones / 12) - 1;
            double weightSum = 0;
            for (var head = 0; head < 2; head++)
            {
                delay[head] -= speed;
                if (delay[head] < MinDelay || delay[head] > MinDelay + span[head])
                {
                    span[head] = IdealSpan();
                    delay[head] = Math.Clamp(speed > 0 ? delay[1 - head] + span[head] / 2 : delay[1 - head] - span[head] / 2, MinDelay, MinDelay + span[head]);
                }

                var place = Math.Clamp((delay[head] - MinDelay) / span[head], 0, 1);
                weights[head] = Math.Pow(Math.Sin(Math.PI * place), 2);
                weightSum += weights[head];
            }

            for (var channel = 0; channel < channels; channel++)
            {
                var dry = clip.Samples[n * channels + channel];
                if (wetness < 1e-4)
                {
                    output[n * channels + channel] = dry;
                    continue;
                }

                double wet = 0;
                for (var head = 0; head < 2; head++)
                {
                    wet += Read(channel, n + latency - delay[head]) * weights[head];
                }

                wet = weightSum > 1e-3 ? wet / weightSum : dry;
                output[n * channels + channel] = (float)(dry + (wet - dry) * wetness);
            }
        }

        return new AudioClip(output, channels, rate);
    }
}
