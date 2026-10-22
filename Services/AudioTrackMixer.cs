using NAudio.Wave;

namespace Sonator.Services;

// An audio clip placed on the song timeline, starting at a sixteenth-note step.
public sealed record AudioPlacement(string Path, int StartStep);

// Mixes the audio clips of the tracks into one stereo clip, starting from a given step at a given tempo.
// Clips are placed by step, so they stay in time with the MIDI tracks when the tempo is changed.
public static class AudioTrackMixer
{
    private const int OutputChannels = 2;

    public static AudioClip? Mix(IReadOnlyList<AudioPlacement> placements, double tempoBpm, int fromStep, Func<string, AudioClip> load)
    {
        if (placements.Count == 0 || tempoBpm <= 0)
        {
            return null;
        }

        var clips = placements.Select(placement => (Placement: placement, Clip: load(placement.Path))).ToList();
        var sampleRate = clips[0].Clip.SampleRate;
        var secondsPerStep = 60.0 / tempoBpm / 4.0;

        var layout = clips.Select(item =>
        {
            var startFrame = (int)Math.Round((item.Placement.StartStep - fromStep) * secondsPerStep * sampleRate);
            var skip = Math.Max(0, -startFrame);
            return (item.Clip, Start: Math.Max(0, startFrame), Skip: skip);
        }).ToList();

        var totalFrames = layout.Max(item => item.Start + item.Clip.FrameCount - item.Skip);
        if (totalFrames <= 0)
        {
            return null;
        }

        var output = new float[totalFrames * OutputChannels];
        foreach (var (clip, start, skip) in layout)
        {
            if (clip.SampleRate != sampleRate)
            {
                continue; // clips at another sample rate are left out rather than played at the wrong speed
            }

            for (var frame = skip; frame < clip.FrameCount; frame++)
            {
                var target = (start + frame - skip) * OutputChannels;
                for (var channel = 0; channel < OutputChannels; channel++)
                {
                    var sourceChannel = Math.Min(channel, clip.Channels - 1);
                    output[target + channel] += clip.Samples[frame * clip.Channels + sourceChannel];
                }
            }
        }

        return new AudioClip(output, OutputChannels, sampleRate);
    }
}

// Plays a mixed clip through the default output, from its first frame.
public static class AudioTrackPlayback
{
    private static WaveOutEvent? _output;

    public static void Play(AudioClip clip)
    {
        Stop();
        _output = new WaveOutEvent();
        _output.Init(AudioClipLoader.ToPlayable(clip, 0, clip.FrameCount).ToWaveProvider());
        _output.Play();
    }

    // Plays each track's clip through its own effects, mixed live, so turning a knob is heard at once.
    // Clips at another sample rate than the first are left out rather than played at the wrong speed.
    public static void Play(IReadOnlyList<(AudioClip Clip, Effects.NativeEffectChain? Effects)> tracks)
    {
        Stop();
        if (tracks.Count == 0)
        {
            return;
        }

        var sampleRate = tracks[0].Clip.SampleRate;
        var sources = tracks
            .Where(track => track.Clip.SampleRate == sampleRate)
            .Select(track =>
            {
                var source = AudioClipLoader.ToPlayable(track.Clip, 0, track.Clip.FrameCount);
                return track.Effects is null ? source : new Effects.EffectsSampleProvider(source, track.Effects);
            })
            .ToList();
        var mixer = new NAudio.Wave.SampleProviders.MixingSampleProvider(sources) { ReadFully = false };
        _output = new WaveOutEvent();
        _output.Init(mixer.ToWaveProvider());
        _output.Play();
    }

    public static void Stop()
    {
        if (_output is null)
        {
            return;
        }
        _output.Stop();
        _output.Dispose();
        _output = null;
    }
}
