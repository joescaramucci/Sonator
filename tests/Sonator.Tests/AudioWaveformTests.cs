using System.Collections.ObjectModel;
using Sonator.Models;
using Sonator.Services;
using Sonator.ViewModels;
using Xunit;

namespace Sonator.Tests;

public sealed class AudioWaveformTests
{
    private const int Rate = 44100;

    [Fact]
    public void FromClip_ReportsDurationAndPeakPerSlice()
    {
        var clip = new AudioClip(Enumerable.Repeat(0.5f, Rate * 2).ToArray(), 1, Rate);

        var summary = AudioWaveformSummary.FromClip(clip);

        Assert.Equal(2.0, summary.DurationSeconds, 3);
        Assert.Equal(AudioWaveformSummary.PeakCount, summary.Peaks.Length);
        Assert.All(summary.Peaks, peak => Assert.Equal(0.5f, peak));
    }

    [Fact]
    public void PathData_IsAClosedOutlineAroundTheMiddleLine()
    {
        var clip = new AudioClip(new float[Rate], 1, Rate);

        var path = AudioWaveformSummary.FromClip(clip).PathData;

        Assert.StartsWith("M 0,50", path);
        Assert.EndsWith(" Z", path);
    }

    [Fact]
    public void ArrangeTrack_ClipLengthFollowsTheTempo()
    {
        // Two seconds at 120 BPM is four beats, so 16 sixteenth steps.
        var summary = AudioWaveformSummary.FromClip(new AudioClip(new float[Rate * 2], 1, Rate));
        var track = new PatternTrackViewModel("Audio", "Audio clip", 0, 60, new ObservableCollection<StepViewModel>());

        var timeline = new ArrangeTimelineTrackViewModel(track, new ObservableCollection<ArrangeTimelineStepViewModel>(), summary, 120);

        Assert.Equal(16, timeline.AudioLengthSteps, 3);
    }

    [Fact]
    public void ArrangeTrack_WithoutClip_HasNoLength()
    {
        var track = new PatternTrackViewModel("Midi", "Test", 0, 60, new ObservableCollection<StepViewModel>());

        var timeline = new ArrangeTimelineTrackViewModel(track, new ObservableCollection<ArrangeTimelineStepViewModel>());

        Assert.Equal(0, timeline.AudioLengthSteps);
        Assert.Equal(string.Empty, timeline.AudioPathData);
    }
}
