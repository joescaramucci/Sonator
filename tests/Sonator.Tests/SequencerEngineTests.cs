using System.Collections.ObjectModel;
using Sonator.Services;
using Sonator.ViewModels;
using Xunit;

namespace Sonator.Tests;

public sealed class SequencerEngineTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(-120)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void TempoBpm_InvalidValue_FallsBackToOneBpm(double invalidTempo)
    {
        using var engine = new SequencerEngine();

        engine.TempoBpm = invalidTempo;

        Assert.Equal(1, engine.TempoBpm);
    }

    [Fact]
    public void TempoBpm_ValidValue_IsStored()
    {
        using var engine = new SequencerEngine();

        engine.TempoBpm = 120;

        Assert.Equal(120, engine.TempoBpm);
    }

    [Fact]
    public void MasterVolume_NaN_FallsBackToDefault()
    {
        using var engine = new SequencerEngine();

        engine.MasterVolume = double.NaN;

        Assert.Equal(0.75, engine.MasterVolume);
    }

    [Theory]
    [InlineData(-0.5, 0.0)]
    [InlineData(1.7, 1.0)]
    [InlineData(0.4, 0.4)]
    public void MasterVolume_OutOfRange_IsClamped(double input, double expected)
    {
        using var engine = new SequencerEngine();

        engine.MasterVolume = input;

        Assert.Equal(expected, engine.MasterVolume);
    }

    [Fact]
    public void Start_WithoutPattern_ThrowsInvalidOperation()
    {
        using var engine = new SequencerEngine();
        engine.LoadPattern(null);

        Assert.Throws<InvalidOperationException>(() => engine.Start());
    }

    [Fact]
    public void Stop_WhenNotPlaying_DoesNotThrow()
    {
        using var engine = new SequencerEngine();

        engine.Stop();
        engine.Stop();
    }

    [Fact]
    public void Dispose_CalledTwice_DoesNotThrow()
    {
        var engine = new SequencerEngine();

        engine.Dispose();
        engine.Dispose();
    }

    [Fact]
    public void Track_VolumeOutOfRange_IsClampedOnConstruction()
    {
        var track = CreateTrack(volume: 3.0);

        Assert.Equal(1.0, track.Volume);
    }

    internal static PatternTrackViewModel CreateTrack(int stepCount = 16, double volume = 0.75)
    {
        var steps = new ObservableCollection<StepViewModel>();
        for (var i = 0; i < stepCount; i++)
        {
            steps.Add(new StepViewModel(i));
        }

        return new PatternTrackViewModel(
            name: "Track 1",
            instrument: "Test",
            channel: 0,
            noteNumber: 60,
            steps: steps,
            volume: volume);
    }
}
