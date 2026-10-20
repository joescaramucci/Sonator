using System.Collections.ObjectModel;
using Sonator.ViewModels;
using Xunit;

namespace Sonator.Tests;

public sealed class TrackEditOperationsTests
{
    [Fact]
    public void Transpose_StepWithoutOwnNote_MovesOnceFromOriginalBaseNote()
    {
        // Regression: the fallback used to read the base note after it had already been transposed.
        var track = CreateTrack(baseNote: 60, steps: new[] { (Active: true, Note: (int?)null) });

        TrackEditOperations.Transpose(track, 2);

        Assert.Equal(62, track.NoteNumber);
        Assert.Equal(62, track.Steps[0].MidiNoteNumber);
    }

    [Fact]
    public void Transpose_StepWithOwnNote_MovesByTheSameAmount()
    {
        var track = CreateTrack(baseNote: 60, steps: new[] { (Active: true, Note: (int?)64) });

        TrackEditOperations.Transpose(track, -3);

        Assert.Equal(57, track.NoteNumber);
        Assert.Equal(61, track.Steps[0].MidiNoteNumber);
    }

    [Fact]
    public void Transpose_InactiveStep_IsUnchanged()
    {
        var track = CreateTrack(baseNote: 60, steps: new[] { (Active: false, Note: (int?)70) });

        TrackEditOperations.Transpose(track, 5);

        Assert.Equal(70, track.Steps[0].MidiNoteNumber);
    }

    [Fact]
    public void Transpose_ClampsToMidiRange()
    {
        var track = CreateTrack(baseNote: 125, steps: new[] { (Active: true, Note: (int?)127) });

        TrackEditOperations.Transpose(track, 5);

        Assert.Equal(127, track.NoteNumber);
        Assert.Equal(127, track.Steps[0].MidiNoteNumber);
    }

    [Fact]
    public void Transpose_ShiftsRecordedNoteEvents()
    {
        var track = CreateTrack(baseNote: 60, steps: Array.Empty<(bool Active, int? Note)>());
        track.MidiNoteEvents = new List<MidiNoteEventViewModel> { new(0, 2, 64, 100) };

        TrackEditOperations.Transpose(track, 1);

        Assert.Equal(65, track.MidiNoteEvents[0].NoteNumber);
    }

    [Fact]
    public void QuantizeActiveSteps_FillsNoteDurationAndVelocity()
    {
        var track = CreateTrack(baseNote: 48, steps: new[]
        {
            (Active: true, Note: (int?)null),
            (Active: true, Note: (int?)55),
            (Active: false, Note: (int?)null)
        });

        TrackEditOperations.QuantizeActiveSteps(track);

        Assert.Equal(48, track.Steps[0].MidiNoteNumber);
        Assert.Equal(55, track.Steps[1].MidiNoteNumber);
        Assert.All(new[] { track.Steps[0], track.Steps[1] }, step =>
        {
            Assert.Equal(1, step.DurationSteps);
            Assert.Equal(96, step.Velocity);
        });
        Assert.Null(track.Steps[2].MidiNoteNumber);
    }

    private static PatternTrackViewModel CreateTrack(int baseNote, IEnumerable<(bool Active, int? Note)> steps)
    {
        var stepItems = new ObservableCollection<StepViewModel>();
        var index = 0;
        foreach (var (active, note) in steps)
        {
            stepItems.Add(new StepViewModel(index++, active) { MidiNoteNumber = note });
        }

        return new PatternTrackViewModel("Track", "Test", 1, baseNote, stepItems);
    }
}
