using System.IO;
using Sonator.Services;
using Xunit;
using Xunit.Abstractions;

namespace Sonator.Tests;

public sealed class AudioEditsTests
{
    private const string SamplePath = @"C:\Users\joe\Desktop\flipall 30 sec.mp3";
    private readonly ITestOutputHelper _output;

    public AudioEditsTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public void Normalize_ScalesPeakToTarget()
    {
        var result = AudioEdits.Normalize(new[] { 0.1f, -0.4f, 0.2f }, targetPeak: 1f);

        Assert.Equal(1f, AudioEdits.Peak(result), precision: 5);
        Assert.Equal(-1f, result[1], precision: 5);
    }

    [Fact]
    public void Normalize_Silence_StaysSilent()
    {
        Assert.Equal(new[] { 0f, 0f }, AudioEdits.Normalize(new[] { 0f, 0f }));
    }

    [Fact]
    public void Silence_ZeroesOnlyTheRange()
    {
        var result = AudioEdits.Silence(new[] { 1f, 1f, 1f, 1f }, channels: 2, startFrame: 1, endFrame: 2);

        Assert.Equal(new[] { 1f, 1f, 0f, 0f }, result);
    }

    [Fact]
    public void FadeOut_EndsAtZeroAndKeepsTheStart()
    {
        var result = AudioEdits.FadeOut(new[] { 1f, 1f, 1f, 1f }, channels: 1, startFrame: 0, endFrame: 4);

        Assert.Equal(1f * 0.75f, result[0], precision: 5);
        Assert.Equal(0f, result[3], precision: 5);
    }

    [Fact]
    public void FadeIn_StartsNearZeroAndEndsAtFullLevel()
    {
        var result = AudioEdits.FadeIn(new[] { 1f, 1f, 1f, 1f }, channels: 1, startFrame: 0, endFrame: 4);

        Assert.Equal(0.25f, result[0], precision: 5);
        Assert.Equal(1f, result[3], precision: 5);
    }

    [Fact]
    public void CutCopyPaste_MoveAStretchOfAudio()
    {
        var samples = new[] { 1f, 2f, 3f, 4f, 5f, 6f }; // mono
        var clip = AudioEdits.Copy(samples, channels: 1, startFrame: 1, endFrame: 3);
        var cut = AudioEdits.Cut(samples, channels: 1, startFrame: 1, endFrame: 3);
        var pasted = AudioEdits.Paste(cut, channels: 1, atFrame: 4, clip);

        Assert.Equal(new[] { 2f, 3f }, clip);
        Assert.Equal(new[] { 1f, 4f, 5f, 6f }, cut);
        Assert.Equal(new[] { 1f, 4f, 5f, 6f, 2f, 3f }, pasted);
    }

    [Fact]
    public void LoadSample_DecodesTheDesktopFile_WhenPresent()
    {
        if (!File.Exists(SamplePath))
        {
            return;
        }

        var clip = AudioClipLoader.Load(SamplePath);

        _output.WriteLine($"duration={clip.Duration.TotalSeconds:F2}s channels={clip.Channels} rate={clip.SampleRate} peak={AudioEdits.Peak(clip.Samples):F3}");
        Assert.InRange(clip.Duration.TotalSeconds, 25, 35);
        Assert.True(AudioEdits.Peak(clip.Samples) > 0.01f);
    }
}
