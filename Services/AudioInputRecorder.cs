using NAudio.CoreAudioApi;
using NAudio.Wave;
using Sonator.Services.Vst3;

namespace Sonator.Services;

/// <summary>
/// Records an audio track from the sound card's input, as a mono clip. With ASIO it reads the driver's input 1
/// through Sonator's own engine, kept running while a track is armed by a silent clip, and takes the driver's
/// input and output latencies off the start, so what was played in time with the song lands in time with it.
/// With WASAPI it records from Windows' default input. While armed, PeakSinceLastAsked feeds the track's meter.
/// </summary>
public sealed class AudioInputRecorder : IDisposable
{
    private readonly IVst3PluginHost _host;
    private readonly Func<AudioOutputSelection> _selection;
    private Vst3ModuleHandle? _keepAlive;
    private WasapiCapture? _capture;
    private readonly object _sync = new();

    // The take: its samples, at its rate, while recording.
    private List<float>? _take;
    private int _takeRate;
    private ulong _position;
    private int _latencyFrames;
    private int _lostFrames;
    private double _wasapiPeak;

    private readonly float[] _readBuffer = new float[48000];

    public AudioInputRecorder(IVst3PluginHost host, Func<AudioOutputSelection> selection)
    {
        _host = host;
        _selection = selection;
    }

    public bool IsArmed { get; private set; }

    public bool IsRecording => _take is not null;

    // What the last arming or take could not do, to tell the player.
    public string? Problem { get; private set; }

    private bool UsesAsio => _selection().UseAsio;

    // Opens the input, so its level shows and a take can start at once.
    public void Arm()
    {
        if (IsArmed)
        {
            return;
        }

        Problem = null;
        if (UsesAsio)
        {
            // Sonator's engine runs only while something plays: a silent clip keeps it, and the input, going.
            if (_host.TryCreateClip(new float[4800 * 2], 2, 48000, 0) is { } module)
            {
                _host.SetClipPlayback(module, true, 0);
                _host.SetClipMonitor(module, true, 0);
                if (_host.TryStartAudio(module, _selection(), out var error))
                {
                    _keepAlive = module;
                }
                else
                {
                    _host.UnloadModule(module);
                    Problem = $"The ASIO input could not be opened: {error}";
                }
            }
            else
            {
                Problem = "Recording needs Sonator's audio engine (VstHostBridge.dll)";
            }

            if (_keepAlive is not null && _host.TryGetAsioInputState(out var hasInput, out _, out _) && !hasInput)
            {
                Problem = "The ASIO driver gives no input 1 to record from";
            }
        }
        else
        {
            try
            {
                _capture = new WasapiCapture(new MMDeviceEnumerator().GetDefaultAudioEndpoint(DataFlow.Capture, Role.Communications));
                _capture.DataAvailable += OnWasapiData;
                _capture.StartRecording();
            }
            catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidOperationException)
            {
                _capture?.Dispose();
                _capture = null;
                Problem = $"Windows' default input could not be opened: {ex.Message}";
            }
        }

        IsArmed = _keepAlive is not null || _capture is not null;
    }

    public void Disarm()
    {
        _take = null;
        if (_keepAlive is { } module)
        {
            _keepAlive = null;
            _host.StopAudio(module);
            _host.UnloadModule(module);
        }

        if (_capture is { } capture)
        {
            _capture = null;
            capture.DataAvailable -= OnWasapiData;
            capture.StopRecording();
            capture.Dispose();
        }

        IsArmed = false;
    }

    // The loudest input sample since the last call, for the armed track's meter.
    public double PeakSinceLastAsked()
    {
        if (_keepAlive is not null)
        {
            return _host.TakeInputPeak();
        }

        lock (_sync)
        {
            var peak = _wasapiPeak;
            _wasapiPeak = 0;
            return peak;
        }
    }

    // Starts a take from now.
    public void StartTake()
    {
        if (!IsArmed)
        {
            return;
        }

        lock (_sync)
        {
            _lostFrames = 0;
            if (_keepAlive is not null)
            {
                _position = _host.GetInputPosition();
                _latencyFrames = _host.TryGetAsioInputState(out _, out var input, out var output) ? input + output : 0;
                _takeRate = 0;
            }
            else
            {
                _latencyFrames = 0;
                _takeRate = _capture!.WaveFormat.SampleRate;
            }

            _take = new List<float>(48000 * 60);
        }
    }

    // Takes what has come in since the last call into the take; called often while recording.
    public void Collect()
    {
        if (_take is null || _keepAlive is null)
        {
            return;
        }

        int count;
        do
        {
            count = _host.ReadInput(_readBuffer, ref _position, out var rate, out var lost);
            _lostFrames += lost;
            if (rate > 0)
            {
                _takeRate = (int)Math.Round(rate);
            }

            for (var i = 0; i < count; i++)
            {
                _take.Add(_readBuffer[i]);
            }
        }
        while (count == _readBuffer.Length);
    }

    // How many frames the take holds so far, the latency still on its start: the places the loop went back at.
    public int FramesRecorded
    {
        get
        {
            Collect();
            lock (_sync)
            {
                return _take?.Count ?? 0;
            }
        }
    }

    // The frames the last take had taken off its start for the latency.
    public int LastTakeSkippedFrames { get; private set; }

    // Ends the take: the mono clip recorded, with the latency taken off its start; null when nothing came in.
    public AudioClip? EndTake()
    {
        Collect();
        List<float>? take;
        lock (_sync)
        {
            take = _take;
            _take = null;
        }

        if (take is null || _takeRate <= 0)
        {
            return null;
        }

        var skip = Math.Min(_latencyFrames, take.Count);
        LastTakeSkippedFrames = skip;
        if (take.Count - skip < _takeRate / 20)
        {
            return null; // less than a twentieth of a second
        }

        if (_lostFrames > 0)
        {
            Problem = $"{_lostFrames} samples of the take were lost: Sonator was too busy to collect them in time";
        }

        return new AudioClip(take.Skip(skip).ToArray(), 1, _takeRate);
    }

    // Windows' input: mixed down to mono floats.
    private void OnWasapiData(object? sender, WaveInEventArgs e)
    {
        if (sender is not WasapiCapture capture)
        {
            return;
        }

        var format = capture.WaveFormat;
        var channels = Math.Max(1, format.Channels);
        // Shared-mode capture gives 32-bit floats, said plainly or in an extensible format.
        var isFloat = format.Encoding == WaveFormatEncoding.IeeeFloat
            || (format.Encoding == WaveFormatEncoding.Extensible && format.BitsPerSample == 32);
        var bytesPerSample = format.BitsPerSample / 8;
        var frames = e.BytesRecorded / (bytesPerSample * channels);
        lock (_sync)
        {
            for (var frame = 0; frame < frames; frame++)
            {
                var sum = 0.0;
                for (var channel = 0; channel < channels; channel++)
                {
                    var offset = (frame * channels + channel) * bytesPerSample;
                    sum += isFloat && bytesPerSample == 4
                        ? BitConverter.ToSingle(e.Buffer, offset)
                        : bytesPerSample == 2 ? BitConverter.ToInt16(e.Buffer, offset) / 32768.0
                        : bytesPerSample == 4 ? BitConverter.ToInt32(e.Buffer, offset) / 2147483648.0 : 0;
                }

                var sample = (float)(sum / channels);
                _wasapiPeak = Math.Max(_wasapiPeak, Math.Abs(sample));
                _take?.Add(sample);
            }
        }
    }

    public void Dispose() => Disarm();
}
