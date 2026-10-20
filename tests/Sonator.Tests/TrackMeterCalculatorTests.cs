using System.Collections.ObjectModel;
using Sonator.ViewModels;
using Xunit;

namespace Sonator.Tests;

public sealed class TrackMeterCalculatorTests
{
    private const double Tolerance = 1e-9;

    [Fact]
    public void Update_ActiveFullVelocityStep_RaisesTrackMeterToFullScale()
    {
        var track = CreateTrack(activeSteps: new[] { 0 }, velocity: 127, volume: 1.0);
        var calculator = new TrackMeterCalculator();

        calculator.Update(new[] { track }, stepIndex: 0, masterVolume: 1.0);

        Assert.Equal(1.0, track.SignalLevel, Tolerance);
    }

    [Fact]
    public void Update_InactiveStepAfterHit_ReleasesTrackMeter()
    {
        var track = CreateTrack(activeSteps: new[] { 0 }, velocity: 127, volume: 1.0);
        var calculator = new TrackMeterCalculator();

        calculator.Update(new[] { track }, stepIndex: 0, masterVolume: 1.0);
        calculator.Update(new[] { track }, stepIndex: 1, masterVolume: 1.0);

        Assert.Equal(0.78, track.SignalLevel, Tolerance);
    }

    [Fact]
    public void Update_MutedTrack_IsSilencedAndExcludedFromMaster()
    {
        var audible = CreateTrack(activeSteps: Array.Empty<int>(), velocity: 127, volume: 1.0);
        var muted = CreateTrack(activeSteps: new[] { 0 }, velocity: 127, volume: 1.0, isMuted: true);
        var calculator = new TrackMeterCalculator();

        var master = calculator.Update(new[] { audible, muted }, stepIndex: 0, masterVolume: 1.0);

        Assert.Equal(0, muted.SignalLevel, Tolerance);
        Assert.Equal(0, master, Tolerance);
    }

    [Fact]
    public void Update_MasterLevel_IsSmoothedTowardTarget()
    {
        var track = CreateTrack(activeSteps: new[] { 0 }, velocity: 127, volume: 1.0);
        var calculator = new TrackMeterCalculator();

        // A single full-scale hit reaches the clamped target of 1.0 at 28% per update.
        var first = calculator.Update(new[] { track }, stepIndex: 0, masterVolume: 1.0);
        var second = calculator.Update(new[] { track }, stepIndex: 0, masterVolume: 1.0);

        Assert.Equal(0.28, first, Tolerance);
        Assert.True(second > first && second < 1.0, $"Expected a rising, unsaturated level, got {second}.");
    }

    [Fact]
    public void Reset_ClearsTrackMetersAndSmoothedMaster()
    {
        var track = CreateTrack(activeSteps: new[] { 0 }, velocity: 127, volume: 1.0);
        var calculator = new TrackMeterCalculator();
        calculator.Update(new[] { track }, stepIndex: 0, masterVolume: 1.0);

        calculator.Reset(new[] { track });
        var master = calculator.Update(new[] { track }, stepIndex: 1, masterVolume: 1.0);

        Assert.Equal(0, track.SignalLevel, Tolerance);
        Assert.Equal(0, master, Tolerance);
    }

    [Fact]
    public void Update_NoTracks_ReturnsSilence()
    {
        var calculator = new TrackMeterCalculator();

        var master = calculator.Update(Array.Empty<PatternTrackViewModel>(), stepIndex: 0, masterVolume: 1.0);

        Assert.Equal(0, master, Tolerance);
    }

    private static PatternTrackViewModel CreateTrack(int[] activeSteps, int velocity, double volume, bool isMuted = false)
    {
        var steps = new ObservableCollection<StepViewModel>();
        for (var i = 0; i < 16; i++)
        {
            steps.Add(new StepViewModel(i, activeSteps.Contains(i)) { Velocity = velocity });
        }

        var track = new PatternTrackViewModel("Track", "Test", 0, 60, steps, volume: volume);
        track.IsMuted = isMuted;
        return track;
    }
}
