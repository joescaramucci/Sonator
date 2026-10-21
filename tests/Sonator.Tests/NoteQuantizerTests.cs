using Sonator.ViewModels;
using Xunit;

namespace Sonator.Tests;

public sealed class NoteQuantizerTests
{
    private static MidiNoteEventViewModel NoteAt(double start, double length)
    {
        var whole = Math.Floor(start);
        var wholeLength = Math.Floor(length);
        return new MidiNoteEventViewModel((int)whole, (int)wholeLength, 60, 100, start - whole, length - wholeLength);
    }

    private static double Start(MidiNoteEventViewModel note) => note.StepIndex + note.StartOffset;

    private static double Length(MidiNoteEventViewModel note) => note.DurationSteps + note.LengthFraction;

    [Theory]
    [InlineData(2.4, 2.0)]
    [InlineData(2.6, 3.0)]
    [InlineData(0.3, 0.0)]
    public void Sixteenths_MoveStartsToTheNearestStep(double start, double expected)
    {
        var result = NoteQuantizer.Quantize(new[] { NoteAt(start, 1) }, grid: 1, strength: 1, swing: 0, quantizeEnds: false);

        Assert.Equal(expected, Start(result[0]), 6);
    }

    [Fact]
    public void HalfStrength_MovesHalfWay()
    {
        var result = NoteQuantizer.Quantize(new[] { NoteAt(2.4, 1) }, grid: 1, strength: 0.5, swing: 0, quantizeEnds: false);

        Assert.Equal(2.2, Start(result[0]), 6);
    }

    [Fact]
    public void EighthTriplets_UseAGridOfTwoThirdsOfAnEighth()
    {
        // An eighth triplet is 4/3 of a sixteenth: grid points at 0, 1.333, 2.667, 4...
        var result = NoteQuantizer.Quantize(new[] { NoteAt(1.5, 1) }, grid: 4.0 / 3.0, strength: 1, swing: 0, quantizeEnds: false);

        Assert.Equal(4.0 / 3.0, Start(result[0]), 6);
    }

    [Fact]
    public void FullSwing_MovesTheOffBeatToTheTripletPosition()
    {
        // With an eighth grid, the second eighth (step 2) moves a third of a grid later: 2 + 2/3.
        var result = NoteQuantizer.Quantize(new[] { NoteAt(2.1, 1), NoteAt(4.1, 1) }, grid: 2, strength: 1, swing: 1, quantizeEnds: false);

        Assert.Equal(2 + 2.0 / 3.0, Start(result[0]), 6);
        Assert.Equal(4.0, Start(result[1]), 6);
    }

    [Fact]
    public void WithoutEnds_EachNoteKeepsItsLength()
    {
        var result = NoteQuantizer.Quantize(new[] { NoteAt(2.4, 1.7) }, grid: 1, strength: 1, swing: 0, quantizeEnds: false);

        Assert.Equal(1.7, Length(result[0]), 6);
    }

    [Fact]
    public void WithEnds_TheEndSnapsToo()
    {
        // Start 2.4 -> 2, end 4.1 -> 4.
        var result = NoteQuantizer.Quantize(new[] { NoteAt(2.4, 1.7) }, grid: 1, strength: 1, swing: 0, quantizeEnds: true);

        Assert.Equal(2.0, Start(result[0]), 6);
        Assert.Equal(2.0, Length(result[0]), 6);
    }

    [Fact]
    public void WithEnds_AVeryShortNoteKeepsAtLeastOneGrid()
    {
        var result = NoteQuantizer.Quantize(new[] { NoteAt(2.1, 0.2) }, grid: 1, strength: 1, swing: 0, quantizeEnds: true);

        Assert.Equal(2.0, Start(result[0]), 6);
        Assert.Equal(1.0, Length(result[0]), 6);
    }

    [Fact]
    public void Grids_IncludeStraightDottedAndTripletValues()
    {
        var labels = NoteQuantizer.Grids.Select(grid => grid.Label).ToList();

        Assert.Contains("1/16", labels);
        Assert.Contains("1/8 dotted", labels);
        Assert.Contains("1/16 triplet", labels);
        Assert.Contains("1/64", labels);
    }
}
