using System.Collections.ObjectModel;
using Sonator.ViewModels;
using Xunit;

namespace Sonator.Tests;

public sealed class TimelineAndTrackRulesTests
{
    [Theory]
    [InlineData(0, "00 00 00 00")]
    [InlineData(1, "00 00 00 01")]
    [InlineData(4, "00 01 00 00")]
    [InlineData(6, "00 01 01 00")]
    [InlineData(15, "00 03 01 01")]
    [InlineData(16, "01 00 00 00")]
    [InlineData(35, "02 00 01 01")]
    public void FormatSubdivisionPosition_SplitsStepIntoBarBeatAndSubdivision(int step, string expected)
    {
        Assert.Equal(expected, TimelineFormatting.FormatSubdivisionPosition(step));
    }

    [Theory]
    [InlineData(new[] { 16 }, 1)]
    [InlineData(new[] { 17 }, 2)]
    [InlineData(new[] { 8, 33 }, 3)]
    [InlineData(new int[0], 1)]
    public void PatternBarCount_UsesLongestTrackRoundedUpToBars(int[] trackStepCounts, int expectedBars)
    {
        var tracks = trackStepCounts
            .Select(count => new PatternTrackViewModel("T", "I", 1, 60, Steps(count)))
            .ToList();
        var pattern = new PatternViewModel("P", "", "01", new ObservableCollection<PatternTrackViewModel>(tracks));

        Assert.Equal(expectedBars, TimelineFormatting.PatternBarCount(pattern));
    }

    [Fact]
    public void Validate_ValidDefinition_Passes()
    {
        Assert.True(TrackDefinitionRules.Validate("Bass", 1, 36, out var error));
        Assert.Equal(string.Empty, error);
    }

    [Fact]
    public void Validate_BlankName_Fails()
    {
        Assert.False(TrackDefinitionRules.Validate("  ", 1, 36, out var error));
        Assert.Equal("Track name is required.", error);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(17)]
    public void Validate_ChannelOutOfRange_Fails(int channel)
    {
        Assert.False(TrackDefinitionRules.Validate("Bass", channel, 36, out var error));
        Assert.Equal("MIDI channel must be between 1 and 16.", error);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(128)]
    public void Validate_NoteOutOfRange_Fails(int noteNumber)
    {
        Assert.False(TrackDefinitionRules.Validate("Bass", 1, noteNumber, out var error));
        Assert.Equal("Base note must be between 0 and 127.", error);
    }

    private static ObservableCollection<StepViewModel> Steps(int count)
    {
        var steps = new ObservableCollection<StepViewModel>();
        for (var i = 0; i < count; i++)
        {
            steps.Add(new StepViewModel(i));
        }

        return steps;
    }
}
