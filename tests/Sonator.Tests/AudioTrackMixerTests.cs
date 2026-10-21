using Sonator.Services;
using Xunit;

namespace Sonator.Tests;

public sealed class AudioTrackMixerTests
{
    private const int Rate = 44100;

    private static AudioClip Constant(float value, int frames, int channels = 2)
    {
        return new AudioClip(Enumerable.Repeat(value, frames * channels).ToArray(), channels, Rate);
    }

    [Fact]
    public void Mix_SecondClipStartsOnItsStep()
    {
        // At 120 BPM a sixteenth note is 0.125 s, so step 4 is 0.5 s = 22050 frames.
        var clips = new Dictionary<string, AudioClip> { ["a"] = Constant(0.5f, 1000), ["b"] = Constant(0.25f, 1000) };
        var placements = new[] { new AudioPlacement("a", 0), new AudioPlacement("b", 4) };

        var mixed = AudioTrackMixer.Mix(placements, 120, 0, path => clips[path])!;

        Assert.Equal(0.5f, mixed.Samples[0]);
        Assert.Equal(0f, mixed.Samples[22050 * 2 - 2] - 0f);
        Assert.Equal(0.25f, mixed.Samples[22050 * 2]);
    }

    [Fact]
    public void Mix_StartingLater_SkipsTheClipsEarlierPart()
    {
        // Starting at step 4 (0.5 s) leaves the clip at step 0 already half-way through its first 0.5 s.
        var clip = new AudioClip(Enumerable.Range(0, 44100 * 2).Select(i => i < 22050 ? 0.1f : 0.9f).ToArray(), 1, Rate);

        var mixed = AudioTrackMixer.Mix(new[] { new AudioPlacement("x", 0) }, 120, 4, _ => clip)!;

        Assert.Equal(0.9f, mixed.Samples[0]);
    }

    [Fact]
    public void Mix_MonoClipIsPlayedOnBothChannels()
    {
        var mixed = AudioTrackMixer.Mix(new[] { new AudioPlacement("m", 0) }, 120, 0, _ => Constant(0.3f, 100, channels: 1))!;

        Assert.Equal(2, mixed.Channels);
        Assert.Equal(0.3f, mixed.Samples[0]);
        Assert.Equal(0.3f, mixed.Samples[1]);
    }

    [Fact]
    public void Mix_NoPlacements_ReturnsNothing()
    {
        Assert.Null(AudioTrackMixer.Mix(Array.Empty<AudioPlacement>(), 120, 0, _ => Constant(0f, 1)));
    }
}
