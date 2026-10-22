using NAudio.Wave;

namespace Sonator.Services.Effects;

// Plays a source through a track's effect chain. When the source ends it keeps going for a few
// seconds of silence through the effects, so reverb and echo tails ring out instead of stopping.
public sealed class EffectsSampleProvider : ISampleProvider
{
    private static readonly TimeSpan Tail = TimeSpan.FromSeconds(4);

    private readonly ISampleProvider _source;
    private readonly NativeEffectChain _effects;
    private float[] _block = Array.Empty<float>();
    private int _tailSamplesLeft;
    private bool _sourceEnded;

    public EffectsSampleProvider(ISampleProvider source, NativeEffectChain effects)
    {
        _source = source;
        _effects = effects;
        _tailSamplesLeft = (int)(Tail.TotalSeconds * source.WaveFormat.SampleRate) * source.WaveFormat.Channels;
    }

    public WaveFormat WaveFormat => _source.WaveFormat;

    public int Read(float[] buffer, int offset, int count)
    {
        if (_block.Length < count)
        {
            _block = new float[count];
        }

        var read = _sourceEnded ? 0 : _source.Read(_block, 0, count);
        if (read < count)
        {
            _sourceEnded = true;
            var tail = Math.Min(count - read, _tailSamplesLeft);
            Array.Clear(_block, read, tail);
            _tailSamplesLeft -= tail;
            read += tail;
        }

        if (read == 0)
        {
            return 0;
        }

        var channels = WaveFormat.Channels;
        _effects.Process(_block, read / channels, channels, WaveFormat.SampleRate);
        Array.Copy(_block, 0, buffer, offset, read);
        return read;
    }
}
