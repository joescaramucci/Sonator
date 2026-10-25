using NAudio.Wave;

namespace Sonator.Services;

// Plays the metronome's clicks through the default output, so they sound without a MIDI output device.
// Each click is a short decaying sine burst; the volume depends on how strong the beat is.
public sealed class MetronomeClickPlayer : IDisposable
{
    private const int SampleRate = 44100;
    private const double ClickSeconds = 0.035;
    private const double ClickFrequency = 1500;

    private readonly ClickSource _source = new();
    private WaveOutEvent? _output;

    public void Start()
    {
        if (_output is not null)
        {
            return;
        }

        _output = new WaveOutEvent { DesiredLatency = 40, NumberOfBuffers = 2 };
        _output.Init(_source);
        _output.Play();
    }

    // How long a click waits before it sounds, as the plug-ins' tracks wait for the latest of them (delay
    // compensation), so the clicks stay with the beat heard.
    public double DelaySeconds
    {
        get => _source.DelaySeconds;
        set => _source.DelaySeconds = Math.Clamp(double.IsFinite(value) ? value : 0, 0, 1);
    }

    // Starts a click at the next block of audio, or as much later as DelaySeconds. Called from the sequencer's thread.
    public void Trigger(float volume)
    {
        _source.Trigger(volume);
    }

    public void Dispose()
    {
        _output?.Stop();
        _output?.Dispose();
        _output = null;
    }

    private sealed class ClickSource : ISampleProvider
    {
        private readonly object _sync = new();
        private float _pendingVolume;
        private long _pendingDue;

        public double DelaySeconds { get; set; }
        private int _clickSamplesLeft;
        private float _clickVolume;
        private double _phase;

        public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(SampleRate, 2);

        public void Trigger(float volume)
        {
            lock (_sync)
            {
                _pendingVolume = volume;
                _pendingDue = System.Diagnostics.Stopwatch.GetTimestamp() + (long)(DelaySeconds * System.Diagnostics.Stopwatch.Frequency);
            }
        }

        public int Read(float[] buffer, int offset, int count)
        {
            lock (_sync)
            {
                if (_pendingVolume > 0f && System.Diagnostics.Stopwatch.GetTimestamp() >= _pendingDue)
                {
                    _clickVolume = _pendingVolume;
                    _clickSamplesLeft = (int)(ClickSeconds * SampleRate);
                    _phase = 0;
                    _pendingVolume = 0f;
                }
            }

            var frames = count / 2;
            for (var frame = 0; frame < frames; frame++)
            {
                float sample = 0f;
                if (_clickSamplesLeft > 0)
                {
                    var progress = 1.0 - _clickSamplesLeft / (ClickSeconds * SampleRate);
                    var envelope = Math.Exp(-6.0 * progress);
                    sample = (float)(Math.Sin(_phase) * envelope * _clickVolume);
                    _phase += 2 * Math.PI * ClickFrequency / SampleRate;
                    _clickSamplesLeft--;
                }

                buffer[offset + frame * 2] = sample;
                buffer[offset + frame * 2 + 1] = sample;
            }

            return frames * 2;
        }
    }
}
